using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Chat Reader — continuously OCRs the GW2 chat region and speaks new lines.
///
/// WORKFLOW:
///   1. User clicks "Set Chat Region" to drag-select the chat area
///   2. User clicks "Start Reading" to begin polling
///   3. Every N seconds (default 3.5), we capture the region, OCR it, and
///      compare line-by-line against a seen-set
///   4. The FIRST scan seeds the seen-set silently (so we don't scream out
///      the entire chat history the instant they enable it)
///   5. Subsequent scans speak the newest M lines (default 3) that weren't
///      in the seen-set
///
/// SEEN-SET MANAGEMENT:
///   HashSet&lt;string&gt; of raw OCR'd lines. Cap at 500 entries; trim to
///   the most recent 200 when that's exceeded. This prevents unbounded growth
///   during long play sessions while keeping enough history to avoid double-
///   reading when the chat scrolls a line back into view.
///
/// ARCHITECTURAL NOTES:
///   Uses the same SelectionOverlayWindow pattern as Screen Reader — no
///   z-index trap possible. Uses Windows.Media.Ocr directly, no PSM bug.
/// </summary>
public partial class ChatReaderPanel : UserControl, IPanel, IBackgroundPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private Rect? _region;
    private DispatcherTimer? _timer;
    private bool _enabled;
    private bool _firstScan = true;
    private readonly HashSet<string> _seenKeys = new();  // de-dup: speaker + first words
    private readonly Queue<string> _recent = new(); // last 10 read, newest at the end

    private readonly record struct ChatMsg(string Tag, string Speaker, string Text);

    public ChatReaderPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntervalSlider.Value = App.Settings.Current.ChatReaderInterval / 1000.0;
            CountSlider.Value = App.Settings.Current.ChatReaderMessageCount;
            PauseCombatToggle.IsChecked = App.Settings.Current.ChatPauseInCombat;
        };
        // NOTE: deliberately does NOT stop on Unloaded. Closing the window should
        // not stop reading chat — that's the whole point of the feature. It runs
        // until the user presses Stop Reading, or the app exits.
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    /// <summary>Stop scanning. Called on app exit (see IBackgroundPanel).</summary>
    public void StopBackgroundWork() { _enabled = false; if (_tts != null) _tts.BackgroundReaderActive = false; StopTimer(); }

    // ── Region selection ─────────────────────────────────────────────────────
    private async void SetRegion_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this);
        var sel = new SelectionOverlayWindow();
        Rect? chosen = null;
        sel.RegionSelected += (_, r) => chosen = r;

        if (mainWindow != null) mainWindow.Visibility = Visibility.Hidden;

        var tcs = new TaskCompletionSource();
        sel.Closed += (_, _) => tcs.TrySetResult();
        sel.Show();
        sel.Activate();
        await tcs.Task;

        if (mainWindow != null) mainWindow.Visibility = Visibility.Visible;

        if (chosen is { } r)
        {
            _region = r;
            _firstScan = true;
            _seenKeys.Clear();

            var st = App.Settings.Current;
            st.ChatRegionX = r.X; st.ChatRegionY = r.Y;
            st.ChatRegionW = r.Width; st.ChatRegionH = r.Height;
            App.Settings.NotifyChanged();          // remembered for next time

            StatusText.Text = $"Region set: {(int)r.Width}×{(int)r.Height} at ({(int)r.X}, {(int)r.Y}). Click Start Reading.";
        }
        else
        {
            StatusText.Text = "Selection cancelled.";
        }
    }

    // ── Enable toggle ────────────────────────────────────────────────────────
    private void EnableToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return; // construction-time safety (v21.4)
        if (_region == null)
        {
            StatusText.Text = "⚠ Set a chat region first.";
            EnableToggle.IsChecked = false;
            return;
        }
        _enabled = true;
        if (_tts != null) _tts.BackgroundReaderActive = true; // pause hover-read
        EnableToggle.Content = "⏹ Stop Reading";
        StartTimer();
        StatusText.Text = "Reading chat. First scan silently seeds known messages.";
    }

    private void EnableToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return; // construction-time safety (v21.4)
        _enabled = false;
        if (_tts != null) _tts.BackgroundReaderActive = false; // hover-read may resume
        EnableToggle.Content = "🔴 Start Reading";
        StopTimer();
        StatusText.Text = "Paused.";
    }

    private void StartTimer()
    {
        StopTimer();
        var interval = TimeSpan.FromMilliseconds(App.Settings.Current.ChatReaderInterval);
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await ScanAsync();
        _timer.Start();
        // Fire an immediate scan so user doesn't wait the first interval
        _ = Dispatcher.InvokeAsync(async () => await ScanAsync());
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// <summary>True only when Guild Wars 2 is the active/foreground window.</summary>
    private static bool IsGw2Foreground()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.StartsWith("Gw2", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── The scan cycle ───────────────────────────────────────────────────────
    private async Task ScanAsync()
    {
        if (!_enabled || _region is null) return;

        try
        {
            // Only read while Guild Wars 2 is the ACTIVE window. When you alt-tab to
            // navigate your PC, the reader goes quiet so it isn't fighting NVDA (or
            // reading a chat box that's now hidden behind other windows). It resumes
            // the moment GW2 is focused again.
            if (!IsGw2Foreground()) return;

            // Pause while you're in combat — reading chat on top of everything else
            // mid-fight is too much; resume automatically when combat ends.
            if (App.Settings.Current.ChatPauseInCombat && _mumble?.Read()?.IsInCombat == true)
            {
                DiagLog.Log("CHAT", "paused (in combat)");
                return;
            }

            // Grab a little extra on the LEFT so player names aren't clipped
            // ("Dance Fight" was reading as "ce Fight" when the region edge cut them).
            var r = _region.Value;
            double lx = Math.Max(0, r.X - 34);
            var cap = new Rect(lx, r.Y, r.Width + (r.X - lx), r.Height);
            var png = ScreenCaptureService.CapturePng(cap);
            if (png == null) return;

            // Use the engine's OWN line segmentation rather than splitting a
            // flattened string — it keeps one chat message per entry instead of
            // merging two messages or splitting one, which is what made the same
            // text get re-read.
            var rawLines = await OcrService.ReadLinesAsync(png);
            DiagLog.Log("CHAT", $"raw {rawLines.Count}: {string.Join(" | ", rawLines)}");

            // Faded chat / the 3D scene behind it produce whole frames of noise.
            // Skip those outright instead of reading fragments out of them.
            if (!FrameLooksLikeChat(rawLines)) { DiagLog.Log("CHAT", "frame skipped (mostly noise)"); return; }

            var lines = new List<string>();
            foreach (var raw0 in rawLines)
            {
                var l = CleanLine(raw0);
                if (l.Length > 0) lines.Add(l);
            }
            if (lines.Count == 0) return;

            // Group the OCR lines into whole MESSAGES: a line with a "speaker:" head
            // starts one; a following line without a head is a word-wrapped
            // continuation and gets joined on — so messages are read complete.
            var messages = GroupMessages(lines)
                .Select(m => m with { Text = TrimGarbageTail(m.Text) })
                .Where(m => PlausibleSpeaker(m.Speaker)
                            && !string.IsNullOrWhiteSpace(m.Text)
                            && !IsSystemSpam(m.Text)
                            && IsLikelyChatLine(m.Text) && LooksLikeRealText(m.Text)
                            && HasRealContent(m.Text)
                            && !IsChrome(m.Text)
                            && !IsBlob(m.Text))          // drop pure-OCR-noise lines
                .ToList();

            // De-dup on a STABLE key: the speaker plus the first few words of the
            // message. The start of a message is reliable; only the tail jitters
            // (garbage/links), so keying on the start stops the same message being
            // re-read every scan.
            var newMsgs = new List<ChatMsg>();
            foreach (var m in messages)
            {
                var key = MsgKey(m);
                if (key.Length == 0) continue;
                if (!_seenKeys.Add(key)) continue;
                newMsgs.Add(m);
            }
            if (_seenKeys.Count > 400) _seenKeys.Clear();

            if (_firstScan)
            {
                _firstScan = false;
                StatusText.Text = $"Watching for new messages ({messages.Count} already on screen).";
                return;
            }
            if (newMsgs.Count == 0) return;

            // Read the last N new messages, in chat order, each as: tag, name, message.
            int count = (int)CountSlider.Value;
            var toRead = newMsgs.TakeLast(count).ToList();

            var startEpoch = _tts?.StopEpoch ?? 0;
            RememberScan(messages.Select(x => x.Text));

            foreach (var m in toRead)
            {
                var spoken = FormatMessage(m);
                if (!Confirmed(m.Text)) continue;       // not on screen last scan - wait and see
                if (AlreadySaid(m.Text)) continue;      // heard this already, in some spelling
                AddRecent(spoken);
                DiagLog.Log("CHAT", $"read: \"{spoken}\"");
                if (_tts != null)
                {
                    await _tts.SpeakAsync(spoken, engineOverride: ChatEngine(),
                                     lane: TtsService.SpeechLane.Background,
                                     voiceOverride: App.Settings.Current.ChatVoiceId);
                    if (_tts.StopEpoch != startEpoch) break;
                }
                if (!_enabled) break;
            }
            StatusText.Text = $"Read {toRead.Count} new message(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan error: {ex.Message}";
            CrashLogger.Log("ChatReaderPanel.ScanAsync", ex);
        }
    }

    /// <summary>
    /// Filter out OCR debris. Real chat has several letters and usually a
    /// speaker; a line that's mostly punctuation or digits is a misread icon or
    /// a chopped timestamp, and reading it aloud is worse than skipping it.
    /// </summary>
    /// <summary>
    /// Clean an OCR chat line. The big win: strip the jittery junk OCR appends to
    /// the chat's right edge (a short token after a long whitespace gap, e.g.
    /// "...map ?        X"). That garbage changed every scan, so the same message
    /// looked new each time and got re-read endlessly. Also collapses whitespace,
    /// drops trailing stray symbols, and removes a stray leading char before a tag
    /// ("J[RBL]" -> "[RBL]").
    /// </summary>
    // ── Message grouping / parsing ────────────────────────────────────────────
    /// <summary>Turn OCR lines into whole messages, joining word-wrapped
    /// continuation lines (those with no "speaker:" head) onto the message above.</summary>
    /// <summary>Longest a word-wrapped continuation can make a message before we stop
    /// believing it is one message. Real chat wraps to two or three lines; a hundreds-of-
    /// characters "message" is glued-together scrollback.</summary>
    private const int MaxMessageChars = 220;

    /// <summary>How many wrapped lines one message may absorb.</summary>
    private const int MaxContinuations = 2;

    /// <summary>Is this several messages run together rather than one?
    ///
    /// A backstop behind the grouping rules: even with those, a badly mangled scan can
    /// still hand us a line carrying half the scrollback, and reading it aloud buries a
    /// real callout inside a minute of speech.</summary>
    private static bool IsBlob(string line)
    {
        if (line.Length < 140) return false;
        return line.Count(c => c == ':') >= 3;
    }

    private static List<ChatMsg> GroupMessages(List<string> lines)
    {
        var msgs = new List<ChatMsg>();
        int continuations = 0;

        foreach (var l in lines)
        {
            int c = HeaderColon(l);

            // A line that is not recognised as a head, but plainly contains one further
            // along, is a NEW MESSAGE and not a continuation.
            //
            // This is what turned the whole chat box into one sentence. HeaderColon only
            // accepts a speaker colon inside the first 35 characters; when OCR mangles a
            // channel tag ("[M1", "[MI", "m [M1]") the colon lands past that, so the line
            // was filed as word wrap and glued onto the message above. Several in a row
            // and Quinn heard "Dewdman: or not. Inbis: unreal. Inbis: never rezzing
            // anyone again. Finnbarr Maruun: durios open..." as a single utterance.
            if (c <= 0) c = LateHeaderColon(l);

            if (c > 0)
            {
                var (tag, sp) = ParseHeader(l[..c]);
                msgs.Add(new ChatMsg(tag, sp, l[(c + 1)..].Trim()));
                continuations = 0;
            }
            else if (msgs.Count > 0
                     && continuations < MaxContinuations
                     && msgs[^1].Text.Length + l.Length <= MaxMessageChars)
            {
                var p = msgs[^1];
                msgs[^1] = p with { Text = (p.Text + " " + l).Trim() };
                continuations++;
            }
            else
            {
                msgs.Add(new ChatMsg("", "", l));  // system line, or we stopped gluing
                continuations = 0;
            }
        }
        return msgs;
    }

    /// <summary>A speaker colon further into the line than a tidy head would put it, but
    /// still with a plausible short name in front of it. Used only when the strict head
    /// test has already failed, so a mangled channel tag cannot turn a real message into
    /// somebody else's word wrap.</summary>
    private static int LateHeaderColon(string l)
    {
        int idx = l.IndexOf(':');
        if (idx <= 0 || idx > 60) return -1;

        var head = l[..idx];
        int letters = head.Count(char.IsLetter);
        if (letters < 3) return -1;

        // The part after the colon has to look like something someone said.
        var rest = l[(idx + 1)..].Trim();
        return rest.Length >= 2 ? idx : -1;
    }

    /// <summary>Index of the "speaker:" colon — the first ':' near the start with
    /// letters before it. -1 if the line isn't a message head (a continuation).</summary>
    private static int HeaderColon(string l)
    {
        int idx = l.IndexOf(':');
        if (idx > 0 && idx <= 35 && l[..idx].Any(char.IsLetter)) return idx;
        return -1;
    }

    /// <summary>Split a message head into a [TAG] and a speaker name, stripping the
    /// stray icon characters OCR puts at the very start ("J[RBL]", "5]", "1").</summary>
    private static (string tag, string speaker) ParseHeader(string header)
    {
        var m = System.Text.RegularExpressions.Regex.Match(header, @"\[([^\]]+)\]");
        string tag = m.Success ? m.Groups[1].Value.Trim() : "";
        string speaker = System.Text.RegularExpressions.Regex.Replace(header, @"\[[^\]]*\]", " ");
        speaker = System.Text.RegularExpressions.Regex.Replace(speaker, @"^[^A-Za-z]+", "");
        speaker = System.Text.RegularExpressions.Regex.Replace(speaker.Trim(), @"\s+", " ");
        return (tag, speaker);
    }

    /// <summary>Read order: the map/channel tag, then the name, then the message.</summary>
    /// <summary>
    /// The engine chat should speak through.
    ///
    /// If a chat voice has been chosen, its OWN engine must be used or the voice is
    /// silently ignored: a Piper voice id handed to the Windows engine matches nothing
    /// and falls back to the default voice, which is why chat "did not sound like
    /// Piper". With no chat voice chosen we stay on the Windows voice, which is the
    /// reliable one for a reader that runs all session.
    /// </summary>
    private static string ChatEngine()
    {
        var s = App.Settings.Current;
        return !string.IsNullOrWhiteSpace(s.ChatVoiceId) && !string.IsNullOrWhiteSpace(s.ChatVoiceEngine)
            ? s.ChatVoiceEngine
            : "winnatural";
    }

    private string FormatMessage(ChatMsg m)
    {
        if (!App.Settings.Current.ChatSpeakNames) return m.Text;
        var sp = SettledName(CleanSpeaker(m.Speaker));
        return string.IsNullOrEmpty(sp) ? m.Text : $"{sp}: {m.Text}";
    }

    // -- Names that hold still -------------------------------------------------
    //
    // A name is the hardest thing on the screen to read and the least forgiving to get
    // wrong. One player came back over a single minute as Thecrunchytube, Thcrunchytube,
    // Therocrunchytube and ytube; another as Kansee Durga, Kanssee Durga and Kansee
    // Durga [UM]. Worse, a mangled name is often unpronounceable - "TbRavost", "MIJ" -
    // and the voice then SPELLS IT OUT, letter by letter, which is what Quinn heard.
    //
    // Two rules. Keep a tally of the readings of each name and speak the one seen most
    // often, so a name converges on its commonest spelling instead of jittering. And if
    // a reading is not pronounceable and matches nothing we have seen, say nothing at
    // all: the message is the part that matters, and no name beats a spelled-out one.

    private readonly Dictionary<string, int> _nameTally = new(StringComparer.OrdinalIgnoreCase);

    private string SettledName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim();

        // Closest name we already know, by letter shape.
        var g = Trigrams(Shape(name));
        string? best = null; double bestScore = 0;
        foreach (var known in _nameTally.Keys)
        {
            double sc = Jaccard(g, Trigrams(Shape(known)));
            if (sc > bestScore) { bestScore = sc; best = known; }
        }

        if (best != null && bestScore >= 0.5)
        {
            // Same person, spelled differently. Count this sighting against the name we
            // already have, and answer with whichever spelling has been seen most.
            _nameTally[best] = _nameTally[best] + 1;
            return best;
        }

        if (!Pronounceable(name)) return "";       // never spell a mangle out loud
        _nameTally[name] = _nameTally.TryGetValue(name, out var n) ? n + 1 : 1;
        if (_nameTally.Count > 200) _nameTally.Clear();
        return name;
    }

    /// <summary>Would a voice make a word of this, or spell it out? Wants a vowel, a
    /// sane length, and not to be a block of capitals or a consonant pile-up.</summary>
    private static bool Pronounceable(string s)
    {
        var letters = new string(s.Where(char.IsLetter).ToArray());
        if (letters.Length < 3 || letters.Length > 24) return false;
        if (letters.All(char.IsUpper)) return false;                 // "MIJ", "QL"
        if (!letters.Any(c => "aeiouyAEIOUY".Contains(c))) return false;

        int run = 0;
        foreach (var c in letters.ToLowerInvariant())
        {
            run = "aeiouy".Contains(c) ? 0 : run + 1;
            if (run >= 5) return false;                              // "TbRvst"
        }
        return true;
    }

    /// <summary>Reduce a message head to just the player/NPC name. Strips a leading
    /// "A]" remnant, then any GUILD-TAG tokens OCR leaves on the front (short,
    /// mostly-uppercase, or with digits/brackets — e.g. "[TIEBG]" mangled to
    /// "TIEBGI", or "[M1]" to "M1"), stopping at the first normal name word. Also
    /// drops channel words. This is what stops the tag jitter that caused both the
    /// cluttered reads AND the repeats.</summary>
    private static string CleanSpeaker(string sp)
    {
        sp = System.Text.RegularExpressions.Regex.Replace(sp, @"^\s*[A-Za-z0-9][\]\)\|]\s*", "");
        var toks = sp.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (toks.Count > 1 && IsTagToken(toks[0])) toks.RemoveAt(0);
        var name = string.Join(" ", toks);
        name = System.Text.RegularExpressions.Regex.Replace(name,
            @"\b(Map|Team|Say|Party|Guild|Squad|Whisper|WvW)\b", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"\s+", " ").Trim();
    }

    /// <summary>Does this leading token look like a guild/channel TAG rather than a
    /// name word? Short, and either mostly uppercase, or carrying digits/brackets.</summary>
    private static bool IsTagToken(string t)
    {
        var core = t.Trim('[', ']', '(', ')', '|', '.', ',');
        if (core.Length == 0 || core.Length > 8) return false;
        if (t.IndexOfAny(new[] { '[', ']', '|' }) >= 0 || core.Any(char.IsDigit)) return true;
        int upper = core.Count(char.IsUpper), letters = core.Count(char.IsLetter);
        return letters > 0 && upper >= letters * 0.7;   // ALL/mostly caps = a tag
    }

    /// <summary>Stable de-dup key: the first 6 content words of the MESSAGE only —
    /// deliberately NOT the speaker. A player's name often OCRs differently each
    /// scan ("xv kk dle" / "xy kiddle" / "xy k ddl"), and keying on it made every
    /// jitter look like a new message, so the same line was read over and over.
    /// The message text is the stable part, so we key on that.</summary>
    private static string MsgKey(ChatMsg m)
    {
        // Key on just the FIRST FEW words — the start of a message OCRs reliably,
        // but the END garbles differently every scan ("...we can" / "canjig" /
        // "canis"), which was making the same line count as new and re-read.
        var words = System.Text.RegularExpressions.Regex.Matches(m.Text.ToLowerInvariant(), @"[a-z]{3,}")
            .Select(x => x.Value).Take(4);
        return string.Join(" ", words);
    }

    /// <summary>Does this whole capture look like real chat, or is it mostly OCR
    /// noise (faded chat / the 3D scene bleeding through)? If mostly noise, we skip
    /// the scan entirely rather than reading fragments.</summary>
    private static bool FrameLooksLikeChat(List<string> lines)
    {
        var toks = string.Join(" ", lines).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length < 3) return false;
        int real = toks.Count(t =>
        {
            var c = t.Trim(Punct.ToCharArray());
            return c.Length >= 3 && c.Any(ch => "aeiouyAEIOUY".Contains(ch))
                && c.Count(char.IsLetter) >= c.Length * 0.7
                && !(c.Length > 1 && c.All(char.IsUpper));
        });
        return real >= 4 && real >= toks.Length * 0.22;
    }

    private const string Punct = ".,!?\"'~;:-()[]";

    /// <summary>Common 1-2 letter words / chat shorthand that are real, not debris.</summary>
    private static readonly HashSet<string> CommonShort = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","i","u","ok","hi","no","yo","gg","gj","ty","wp","hp","op","so","me","we","he","go","do","up",
        "to","in","on","of","or","at","it","is","as","be","by","my","ez","gl","hf","np","rip","lol","omg",
        "brb","afk","idk","imo","ppl","sm","pug","sec","yes","yea","yep","nah","lmao","wtf","gz","ns","kk",
    };

    /// <summary>A "weak" token — the kind OCR debris is made of: a stray 1-2 char
    /// fragment, a random all-caps cluster, or a vowel-less blob. Real words aren't.</summary>
    private static bool IsWeakTok(string t)
    {
        var c = t.Trim(Punct.ToCharArray());
        if (c.Length == 0) return true;
        if (CommonShort.Contains(c)) return false;
        if (c.Length <= 2) return true;
        if (c.All(char.IsUpper)) return true;
        if (!c.Any(ch => "aeiouyAEIOUY".Contains(ch))) return true;
        return false;
    }

    /// <summary>
    /// Keep only the coherent part of a message. OCR appends garbage that looks
    /// word-ish one token at a time ("...how are you? Soon orld re clan dd Borer"),
    /// which both gets read aloud AND jitters the de-dup key so the line repeats.
    /// We cut: (1) at any link/symbol, (2) after the first sentence end when more
    /// follows (a real message is usually one utterance; the rest is debris/bleed),
    /// (3) at the first run of two debris tokens, then drop trailing stray/channel
    /// words. Turns the example above into "how are you?".
    /// </summary>
    private static string TrimGarbageTail(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c > 127 || "[]{}|\\=<>©¥®_".IndexOf(c) >= 0) { text = text[..i]; break; }
        }

        var toks = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (toks.Count == 0) return "";

        // (2) Cut after the first sentence-ending token that has more after it.
        for (int i = 1; i < toks.Count - 1; i++)
        {
            char last = toks[i][^1];
            if (last is '.' or '?' or '!') { toks = toks.Take(i + 1).ToList(); break; }
        }

        // (3) Cut at the first run of 2+ debris tokens.
        int weak = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            if (IsWeakTok(toks[i])) { weak++; if (weak >= 2) { toks = toks.Take(i - 1).ToList(); break; } }
            else weak = 0;
        }

        // Drop trailing stray tokens, then a trailing channel indicator.
        while (toks.Count > 0 && !IsCleanWord(toks[^1]) && !CommonShort.Contains(toks[^1].Trim(Punct.ToCharArray())))
            toks.RemoveAt(toks.Count - 1);
        var chan = new[] { "map", "team", "say", "party", "guild", "squad", "whisper", "wvw" };
        while (toks.Count > 0 && chan.Contains(toks[^1].ToLowerInvariant().Trim('.', ',', ']', '[', ')', '(')))
            toks.RemoveAt(toks.Count - 1);

        return string.Join(" ", toks).Trim();
    }

    /// <summary>After trimming, is there enough real content to be worth reading?
    /// Drops the pure-noise lines OCR produces from empty chat area.</summary>
    private static bool HasRealContent(string text)
    {
        var toks = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return false;
        int good = toks.Count(x => IsCleanWord(x) || CommonShort.Contains(x.Trim(Punct.ToCharArray())));
        if (good < toks.Length * 0.5) return false;
        // Needs a real 3+ letter word, OR be entirely legit shorthand ("gg wp").
        bool hasWord = toks.Any(x => x.Trim(Punct.ToCharArray()).Length >= 3 && IsCleanWord(x));
        bool allShort = toks.All(x => CommonShort.Contains(x.Trim(Punct.ToCharArray())));
        return hasWord || allShort;
    }

    /// <summary>A clean readable word: has a vowel, no digits, not ALL-CAPS noise.</summary>
    private static bool IsCleanWord(string t)
    {
        var core = t.Trim('.', ',', '!', '?', '"', '\'', '~', ';', ':', '-', '(', ')');
        if (core.Length == 0 || core.Any(char.IsDigit)) return false;
        int letters = core.Count(char.IsLetter);
        if (letters == 0 || !core.Any(c => "aeiouyAEIOUY".Contains(c))) return false;
        if (core.Length > 1 && core.All(char.IsUpper)) return false;   // ALL-CAPS = OCR junk
        return letters >= core.Length * 0.7;
    }

    /// <summary>Drop repeating system overlays that bleed into the chat region —
    /// the AFK-kick countdown, screenshot-saved path, and WvW score notices — so
    /// they aren't read as if they were chat.</summary>
    private static bool IsSystemSpam(string text)
    {
        var t = text.ToLowerInvariant();
        return t.Contains("will be kicked")
            || t.Contains("screenshot")            // "Screenshot saved as ..."
            || t.Contains("onedrive")              // the file PATH — mangled or not
            || t.Contains("users\\")
            || t.Contains("userscomma")            // OCR drops the backslashes
            || t.Contains("\\screens")
            || (t.Contains("documents") && t.Contains("guild wars"))  // the save path
            // NOT filtered any more: "Your world has claimed Borderlands Bloodlust!",
            // "…deliver a crushing defeat". These were treated as spam, and they are
            // exactly the World versus World event announcements Quinn wants read out -
            // the "popup" she asked whether OCR could catch. It is not a popup at all,
            // it is a system line in the chat box, and we were deleting it.
            || t.Contains("carry more supplies");   // fires constantly in WvW
    }

    /// <summary>A real speaker name is short and mostly letters. Filters out lines
    /// where the "speaker" is OCR garbage from non-chat UI.</summary>
    private static bool PlausibleSpeaker(string sp)
    {
        if (string.IsNullOrWhiteSpace(sp) || sp.Length > 30) return false;
        int letters = sp.Count(char.IsLetter);
        return letters >= 2 && letters >= sp.Length * 0.6;
    }

    // -- "Have we already said this?" ----------------------------------------
    //
    // The first attempt compared sets of whole words, which was still far too strict.
    // The log shows why: one player's message came back across a minute as
    //
    //     Yeeeeeaaassssshhhhh / Yeeeeeeaaaassssshhhhh / Yeeeeaaaaassshhhhhh
    //     Yeeeeaaassssshhhhh  / Yeeeahaaassssshhhhh
    //
    // and the speaker with it: Thecrunchytube / Thcrunchytube / Therocrunchytube /
    // ytube. Not one of those words matches another exactly, so every pass looked new.
    // Same story for "[Bravest Escarpment] now here", which arrived as Bravescap
    // Erscertainment, Bravost Escape, BBrave Escortment and Bra... Escarm...
    //
    // So compare on shape rather than spelling:
    //   1. runs of a repeated letter collapse to one, which alone makes every
    //      Yeeeaaassshhh variant identical;
    //   2. what is left is compared by character TRIGRAMS, so a few wrong letters in
    //      a long message no longer make it a different message.

    private const double SameByWords = 0.6;      // 3 words in 5 shared
    private const double SameByShape = 0.45;     // or the text simply looks the same
    private readonly List<(HashSet<string> Words, HashSet<string> Grams)> _recentSaid = new();

    /// <summary>True if this is close enough to something recently read to be a repeat.
    /// Records it either way, so a near-miss still blocks the next near-miss.</summary>
    private bool AlreadySaid(string text)
    {
        var clean = StripNarration(text);
        var words = WordSet(clean);
        var grams = Trigrams(Shape(clean));
        if (words.Count == 0 && grams.Count == 0) return true;   // nothing to say

        foreach (var (pw, pg) in _recentSaid)
            if (Jaccard(words, pw) >= SameByWords || Jaccard(grams, pg) >= SameByShape)
                return true;

        _recentSaid.Add((words, grams));
        if (_recentSaid.Count > 60) _recentSaid.RemoveRange(0, 20);
        return false;
    }

    /// <summary>Letters only, lower case, with runs of a repeated letter collapsed to
    /// one. "Yeeeeeaaassssshhhhh" and "Yeeeahaaassssshhhhh" both become "yeash".</summary>
    private static string Shape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        char last = '\0';
        foreach (var ch in s.ToLowerInvariant())
        {
            if (!char.IsLetter(ch)) { last = '\0'; continue; }
            if (ch == last) continue;
            sb.Append(ch);
            last = ch;
        }
        return sb.ToString();
    }

    private static HashSet<string> Trigrams(string s)
    {
        var set = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }

    /// <summary>Strip what the transcriber added rather than read: its own "Speaker:" /
    /// "User:" / "Chat Box:" lead-ins, any markup tags it leaked, the channel tags, and
    /// accents.
    ///
    /// The lead-ins mattered more than they look. The de-dup key split on the FIRST
    /// colon, so "Speaker: [G6] Cora: welcome seryn" keyed on "waw cora welcome seryn"
    /// while the same line without the prefix keyed on "welcome seryn" - two different
    /// keys for one message, read twice. The tags matter because the model has been
    /// caught emitting its own scaffolding - &lt;/image-data&gt;, &lt;/doc&gt;,
    /// &lt;say&gt; - which was being read out loud as if a player had typed it.</summary>
    private static string StripNarration(string line)
    {
        var t = line.Trim();

        // Markup the model leaked out of its own template: </image-data>, </doc>, <say>,
        // and the markdown it wraps around anything it decides is a heading.
        t = System.Text.RegularExpressions.Regex.Replace(t, @"</?[A-Za-z][-A-Za-z0-9_]*\s*/?>", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[*_#`]{1,3}", " ");

        for (int i = 0; i < 3; i++)
        {
            var before = t;
            foreach (var lead in new[] { "speaker:", "user:", "chat box:", "chat:", "assistant:" })
                if (t.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                    t = t[lead.Length..].TrimStart();
            if (t == before) break;
        }

        // Channel tags: [S], [G6], [WAW], [M] ...
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[[^\]]{0,12}\]", " ");

        // Accents: the same name comes back as Cora / Cora / Cora / Cora.
        var norm = t.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(norm.Length);
        foreach (var ch in norm)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Trim();
    }

    /// <summary>Is this the interface rather than a person talking?
    ///
    /// The chat tab bar ("Chat x  Gchat +"), the model's own "**Title:**" headings, a
    /// speaker name with nothing after it, and GW2's own "Screenshot saved as
    /// C:\Users\..." notice all reached the voice. The screenshot one is worth killing
    /// twice over: it is noise AND it reads the user's file path out loud.</summary>
    private static bool IsChrome(string line)
    {
        var t = StripNarration(line).Trim(' ', '-', ':', '.', '|', '\t');
        if (t.Length == 0) return true;

        if (t.Contains("Screenshot saved", StringComparison.OrdinalIgnoreCase)) return true;

        // A speaker with nothing to say: "[Tv] Nazareeth:"
        int colon = t.LastIndexOf(':');
        if (colon >= 0 && t[(colon + 1)..].Trim(' ', '-', '.', '|').Length < 2) return true;

        var words = System.Text.RegularExpressions.Regex
            .Matches(t.ToLowerInvariant(), @"[a-z]{2,}")
            .Select(m => m.Value).ToList();
        if (words.Count == 0) return true;

        // Window furniture and nothing else.
        var chrome = new HashSet<string> { "chat", "gchat", "say", "title", "tab", "enter", "press", "to" };
        return words.All(chrome.Contains);
    }

    // -- Only say it once it has held still ------------------------------------
    //
    // The deeper problem is that the vision model invents a slightly different reading
    // every pass, so a lot of what gets spoken was never on screen at all. Waiting for a
    // line to appear in TWO CONSECUTIVE scans before speaking it throws those away: a
    // real message is still there three seconds later, a hallucination is not.
    //
    // The cost is that chat arrives about one scan late, which is a fair trade for not
    // reading out "[Bravescap Erscertainment]".
    private List<HashSet<string>> _lastScanShapes = new();

    /// <summary>Was something like this on screen in the previous scan too?</summary>
    private bool Confirmed(string text)
    {
        var g = Trigrams(Shape(StripNarration(text)));
        if (g.Count == 0) return false;
        foreach (var prev in _lastScanShapes)
            if (Jaccard(g, prev) >= SameByShape) return true;
        return false;
    }

    /// <summary>Remember this scan's lines, so the next one can confirm against them.</summary>
    private void RememberScan(IEnumerable<string> lines)
    {
        _lastScanShapes = lines
            .Select(l => Trigrams(Shape(StripNarration(l))))
            .Where(g => g.Count > 0)
            .ToList();
    }

    private static HashSet<string> WordSet(string s) =>
        System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[a-z]{3,}")
            .Select(x => x.Value).ToHashSet();

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count(x => b.Contains(x));
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static string CleanLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return "";
        // Trailing detached junk after a big gap (run twice for two junk tokens).
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{3,}\S{1,4}\s*$", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s{3,}\S{1,4}\s*$", "");
        line = System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " ");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"[\s|\\/]+$", "").Trim();
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^[A-Za-z0-9]\[", "[");
        return line.Trim();
    }

    private static bool IsLikelyChatLine(string line)
    {
        if (line.Length < 3) return false;
        int letters = line.Count(char.IsLetter);
        if (letters < 2) return false;
        return letters >= line.Length * 0.4;   // at least 40% actual letters
    }

    /// <summary>
    /// Reject OCR gibberish before it's spoken. Real words have vowels and don't
    /// pile up long consonant runs; garbled misreads ("Xthq rll vbnm") do. We keep
    /// a line only if at least half of its real words look word-like. This trades a
    /// rare dropped abbreviation for not reading nonsense aloud — the thing that
    /// made the chat reader sound "garbled".
    /// </summary>
    private static bool LooksLikeRealText(string line)
    {
        const string vowels = "aeiouyAEIOUY";
        var words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int alphaWords = 0, good = 0;
        foreach (var w in words)
        {
            var letters = new string(w.Where(char.IsLetter).ToArray());
            if (letters.Length < 2) continue;      // skip punctuation / single letters
            alphaWords++;

            bool hasVowel = letters.Any(c => vowels.Contains(c));
            int run = 0; bool badCluster = false;
            foreach (var c in letters)
            {
                if (vowels.Contains(c)) run = 0;
                else if (++run >= 5) badCluster = true;   // 5+ consonants in a row = misread
            }
            if (hasVowel && !badCluster) good++;
        }
        if (alphaWords == 0) return false;
        // Lenient: keep the line if AT LEAST ONE word looks real. Requiring half
        // the words to be clean dropped too much genuine chat (names, tags, short
        // words), which is why the reader went quiet. We only drop lines that are
        // entirely gibberish now.
        return good >= 1;
    }

    private void AddRecent(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > 10) _recent.Dequeue();

        RecentList.Items.Clear();
        foreach (var l in _recent.Reverse()) // newest on top
        {
            var tb = new TextBlock
            {
                Text = $"• {l}",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 13,
            };
            RecentList.Items.Add(tb);
        }
    }

    // ── Slider change handlers ───────────────────────────────────────────────
    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists (v21.4).
        // Also prevents the XAML default from stomping the saved setting.
        if (IntervalLabel == null) return;
        var ms = (int)(IntervalSlider.Value * 1000);
        App.Settings.Current.ChatReaderInterval = ms;
        App.Settings.NotifyChanged();
        IntervalLabel.Text = $"{IntervalSlider.Value:0.#}s";
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private void CountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists (v21.4).
        if (CountLabel == null) return;
        App.Settings.Current.ChatReaderMessageCount = (int)CountSlider.Value;
        App.Settings.NotifyChanged();
        CountLabel.Text = $"{(int)CountSlider.Value} message{((int)CountSlider.Value == 1 ? "" : "s")}";
    }

    private void PauseCombat_Changed(object sender, RoutedEventArgs e)
    {
        if (PauseCombatToggle == null) return;
        App.Settings.Current.ChatPauseInCombat = PauseCombatToggle.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _seenKeys.Clear();
        _recent.Clear();
        _firstScan = true;
        RecentList.Items.Clear();
        StatusText.Text = "History cleared. Next scan will re-seed.";
    }
}
