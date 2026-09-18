using System.IO;
using EchoesUnseen.Models;
using NAudio.Wave;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Unified TTS facade. Every panel calls this, not the individual engines.
///
/// AVAILABLE ENGINES:
///   * Piper (default) — local neural TTS that produces natural-sounding voices.
///     Runs entirely on the user's machine; no network calls. Voices are bundled
///     in Resources/Piper/voices/ (or installed via install-piper.ps1).
///   * ElevenLabs — cloud premium TTS. Requires an API key. Sends only text WE
///     generate (wiki readouts, transcripts) — never GW2 game data.
///   * Windows SAPI — classical Windows OS speech synthesis. Robotic but
///     always available, used as the terminal fallback.
///
/// COMPLIANCE NOTE:
///   Piper and ElevenLabs both use neural networks. Neither ever receives
///   Guild Wars 2 game data — they only synthesize text the app generated
///   (Wiki articles, user transcripts, panel readouts). The product website
///   discloses this trade-off so users know what they're opting into.
///
/// ROUTING LOGIC:
///   1. Engine from settings.VoiceEngine ("piper" / "elevenlabs" / "sapi")
///   2. If the preferred engine throws, fall back to the next tier.
///   3. SAPI is the terminal fallback — it's always available on Windows,
///      so we'll never run out of fallbacks.
///
/// PLAYBACK:
///   All audio is decoded via NAudio. WAV plays through WaveFileReader, MP3
///   through Mp3FileReader. One output device at a time: starting a new speech
///   call stops any in-progress speech immediately (the Escape hotkey also
///   calls StopSpeaking for the same behavior).
///
/// THREADING:
///   Synthesis happens on the thread pool. Playback starts on the thread pool,
///   but the _currentOutput field is guarded by a lock so the stop-speaking
///   hotkey from the UI thread can safely interrupt it.
/// </summary>
public class TtsService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PiperTtsEngine _piper;
    private readonly ElevenLabsTtsEngine _elevenLabs;
    private readonly SapiTtsEngine _sapi;
    private readonly WinRtTtsEngine _winNatural;

    private readonly object _playbackLock = new();
    private WaveOutEvent? _currentOutput;
    private Stream? _currentStream;
    private CancellationTokenSource? _currentCts;

    // Serialises synthesis+playback to exactly one utterance at a time. Without
    // it, overlapping SpeakAsync calls (compass, chat reader, hover, panels) each
    // opened a WaveOutEvent and could orphan one before it was disposed. Windows
    // caps simultaneous waveOut handles, so after a while EVERY audio output —
    // TTS and the sonar — failed to open and went silent. Replace-mode is kept:
    // a new call cancels the current one, which releases the gate promptly.
    private readonly SemaphoreSlim _speakGate = new(1, 1);

    /// <summary>Fired when TTS starts/stops so UI can indicate speaking state.</summary>
    public event EventHandler<bool>? SpeakingStateChanged;

    /// <summary>True while an utterance is being synthesized or played.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>
    /// Set by a continuous background reader (Chat Reader, Trail sonar) while it's
    /// running. Hover-to-read checks this and stays silent, so casual mouse
    /// movement never chops up chat that's being read line by line.
    /// </summary>
    public bool BackgroundReaderActive { get; set; }

    /// <summary>When the last utterance finished — used to keep low-priority
    /// hover cues out of the brief gaps between background-reader lines.</summary>
    public DateTime LastSpeechEndedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Respell words the neural voices get wrong, for speech only. Word
    /// boundaries so we don't mangle substrings. "Oracle" was consistently
    /// mispronounced; "Oricle" gives the correct OR-uh-kul.</summary>
    /// <summary>
    /// Say Guild Wars shorthand as words.
    ///
    /// A voice reads "WvW" as three letters, "WTB" as three letters, "SMC" as three
    /// letters - so a chat line that a sighted player takes in at a glance arrives as a
    /// stream of spelled-out consonants. These are the abbreviations that actually turn
    /// up in map and team chat, expanded to what they mean.
    ///
    /// Three-letter forms are matched whatever their case, since chat is typed in a
    /// hurry. Two-letter forms are matched ONLY in capitals: "wp" is a waypoint but "Wp"
    /// at the start of a sentence very often is not, and a wrong expansion is worse than
    /// a spelled-out one.
    /// </summary>
    private static readonly (string Pattern, string Say, bool AnyCase)[] Gw2Shorthand =
    {
        (@"WvW",  "world versus world",   true),
        (@"PvP",  "player versus player", true),
        (@"PvE",  "player versus environment", true),
        (@"WTB",  "want to buy",          true),
        (@"WTS",  "want to sell",         true),
        (@"WTT",  "want to trade",        true),
        (@"LFG",  "looking for group",    true),
        (@"LFM",  "looking for more",     true),
        (@"SMC",  "Stonemist Castle",     true),
        (@"EBG",  "Eternal Battlegrounds", true),
        (@"AFK",  "away from keyboard",   true),
        (@"BRB",  "be right back",        true),
        (@"OMW",  "on my way",            true),
        (@"INC",  "incoming",             true),
        // Any case: chat types these in lower case almost every time, and as standalone
        // words they are not plausibly anything else.
        (@"WP",   "waypoint",             true),
        (@"TP",   "trading post",         true),
        (@"GG",   "good game",            true),
        (@"TY",   "thank you",            true),
        (@"NP",   "no problem",           true),
        // Capitals only: an acronym that is always written that way.
        (@"RI",   "righteous indignation", false),
        // HP is deliberately absent - "hero point" and "health" are both live meanings
        // in Guild Wars, and guessing wrong is worse than spelling two letters.
    };

    private static string FixPronunciation(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bOracle\b", "Oricle",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var (pattern, say, anyCase) in Gw2Shorthand)
        {
            var opts = anyCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                               : System.Text.RegularExpressions.RegexOptions.None;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b" + pattern + @"\b", say, opts);
        }
        return text;
    }

    /// <summary>
    /// Strip characters a neural voice can stall or stutter on — stray Unicode and
    /// symbols OCR leaves behind (@ ~ ¶ box-drawing, etc.). Keeps letters, digits,
    /// whitespace and basic punctuation, then collapses whitespace. Piper is a
    /// subprocess and "garbage in" was a real cause of stutter and hangs, so we
    /// only ever hand it clean, speakable text.
    /// </summary>
    private static string SanitizeForSpeech(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ".,!?;:'\"()/-+%&".IndexOf(ch) >= 0)
                sb.Append(ch);
            else
                sb.Append(' ');   // don't glue words together where a symbol was
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public TtsService(SettingsService settings)
    {
        _settings = settings;
        _piper = new PiperTtsEngine(settings.AppDataDirectory);
        _elevenLabs = new ElevenLabsTtsEngine(() => _settings.Current.ElevenLabsApiKey);
        _sapi = new SapiTtsEngine();
        _winNatural = new WinRtTtsEngine();
    }

    /// <summary>
    /// Speak <paramref name="text"/> using the currently-selected engine.
    /// Any in-progress speech is stopped first (replace-mode).
    /// Returns when playback completes, is cancelled, or fails.
    /// </summary>
    /// <summary>
    /// Speak <paramref name="text"/>. <paramref name="engineOverride"/> forces a
    /// specific engine regardless of the saved preference — the always-on readers
    /// (chat, combat, WvW/nav) pass "sapi" so they use the rock-solid in-process
    /// Windows voice that never spawns a process and never hangs, while on-demand
    /// narration (wiki/item lookups) leaves it null to use the natural Piper voice.
    /// </summary>
    /// <summary>
    /// Speak now, interrupting whatever is playing. This is the lane for anything the
    /// user just ASKED for - a hotkey, a hover read, a menu - where waiting behind a
    /// queue of chat would defeat the point.
    /// </summary>
    /// <summary>What the current utterance is, if the caller labelled it. Lets a caller
    /// stop its OWN speech without silencing something else that happens to be playing.</summary>
    public string? CurrentTag { get; private set; }

    /// <summary>
    /// Stop the utterance in flight IF it carries this tag, leaving the queue intact.
    ///
    /// For "I have moved the pointer, stop telling me about the last thing". A blanket
    /// stop would also throw away queued chat, and StopSpeaking would clear the queue
    /// outright - neither is what moving a mouse should mean.
    /// </summary>
    public void StopIfTagged(string tag)
    {
        if (!string.Equals(CurrentTag, tag, StringComparison.Ordinal)) return;
        CancelCurrentPlayback();
    }

    private async Task SpeakNowAsync(string text, CancellationToken externalCt = default,
                                     string? engineOverride = null, string? voiceOverride = null,
                                     bool background = false, string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // The always-on readers: everything in the background lane, plus the HUD and
        // navigation cues, which stay in the immediate lane (a stale "turn left" is
        // worse than an interrupted one) but are still automatic rather than asked for.
        bool alwaysOn = background || engineOverride == "winnatural";

        // QUIET MODE hushes those while playing. Checked HERE rather than when queued,
        // so going quiet or alt-tabbing drops the backlog instead of reciting it later.
        if (_settings.Current.QuietMode && alwaysOn) return;

        // COEXIST WITH THE SCREEN READER: the always-on readers only speak while
        // Guild Wars 2 is the ACTIVE window. When you alt-tab to navigate your PC,
        // they go silent so they never talk over NVDA — they resume when GW2 is
        // focused again. On-demand reads are unaffected.
        if (alwaysOn && !IsGw2Foreground()) return;

        EchoesUnseen.Services.DiagLog.Log("TTS", text.Length > 200 ? text[..200] + "…" : text);
        text = SanitizeForSpeech(FixPronunciation(text));
        if (string.IsNullOrWhiteSpace(text)) return;   // nothing speakable was left

        // Interrupt any current playback first — but this is NOT a user stop,
        // so it must not bump the epoch (that would make a chunked reader think
        // the user cancelled and abort after the first sentence).
        // Interrupt whatever is playing, then wait our turn on the gate so only
        // one utterance is ever synthesised/played at a time (no orphaned audio
        // handles). The cancel above makes the current holder release promptly.
        CancelCurrentPlayback();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        try { await _speakGate.WaitAsync(cts.Token); }
        catch (OperationCanceledException) { cts.Dispose(); return; }

        lock (_playbackLock) { _currentCts = cts; }

        try
        {
            IsSpeaking = true;
            CurrentTag = tag;
            SpeakingStateChanged?.Invoke(this, true);

            // Screen-reader route: hand the text to NVDA and let it speak with the
            // user's own voice — no separate audio, no conflict. We wait out a rough
            // estimate of the speech length (cancellable) so sequential lines don't
            // clobber each other. Falls through to our own voice if NVDA isn't up.
            if (_settings.Current.SpeakThroughNvda &&
                EchoesUnseen.Services.NvdaOutput.IsRunning() &&
                EchoesUnseen.Services.NvdaOutput.Speak(text, interrupt: true))
            {
                int estMs = Math.Clamp(text.Length * 45, 300, 15000);
                try { await Task.Delay(estMs, cts.Token); } catch (OperationCanceledException) { }
                return;
            }

            var audio = await SynthesizeWithFallbackAsync(text, cts.Token, engineOverride, voiceOverride);
            if (cts.Token.IsCancellationRequested) return;
            await PlayAudioAsync(audio, _settings.Current.Volume, cts.Token);
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            CrashLogger.Log("TtsService.SpeakAsync", ex);
        }
        finally
        {
            IsSpeaking = false;
            CurrentTag = null;
            LastSpeechEndedUtc = DateTime.UtcNow;
            SpeakingStateChanged?.Invoke(this, false);
            _speakGate.Release();
        }
    }

    // ── Two lanes ────────────────────────────────────────────────────────────
    //
    // Every utterance used to interrupt the one before it: SpeakAsync cancelled current
    // playback as its first act. So a long guild recruitment was cut off by the next
    // line, and a WvW callout arriving mid-sentence silenced whatever was speaking. If
    // the interrupting line was then dropped (quiet mode, alt-tab, a repeat) the result
    // was simply silence — which is what "the voice randomly stops" actually was. It was
    // never a crash; there is not one TTS exception in the recent crash log.
    //
    // So there are two lanes now.
    //
    //   Immediate  — you asked for this: a hotkey, a hover read, a menu. Speaks at once,
    //                interrupting whatever is playing, because waiting behind a queue of
    //                chat is exactly the lag that made reading menus useless.
    //
    //   Background — chat, WvW callouts, turn-by-turn. Strict first-in first-out. Each
    //                line finishes before the next begins; nothing cuts anything off.
    //
    // The background queue is CAPPED and drops its oldest entries when it overflows. An
    // uncapped queue in a busy WvW map would leave you listening to callouts about a
    // fight that finished a minute ago.

    public enum SpeechLane { Immediate, Background }

    private sealed record Utterance(string Text, string? Engine, string? Voice);

    private const int BackgroundQueueMax = 8;
    private readonly List<Utterance> _bgQueue = new();
    private readonly SemaphoreSlim _bgSignal = new(0);
    private readonly object _bgLock = new();
    private Task? _bgPump;

    /// <summary>How many lines are waiting to be spoken.</summary>
    public int QueuedCount { get { lock (_bgLock) return _bgQueue.Count; } }

    /// <summary>
    /// Speak. <paramref name="lane"/> decides whether this interrupts (Immediate) or
    /// waits its turn (Background). Background returns as soon as it is queued.
    /// </summary>
    public Task SpeakAsync(string text, CancellationToken externalCt = default,
                           string? engineOverride = null,
                           SpeechLane lane = SpeechLane.Immediate,
                           string? voiceOverride = null,
                           string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        if (lane == SpeechLane.Immediate)
            return SpeakNowAsync(text, externalCt, engineOverride, voiceOverride, tag: tag);

        // End each queued line with a stop. Even with the queue keeping them apart, a
        // voice reads two messages back to back as one run-on sentence without it.
        var t = text.TrimEnd();
        if (t.Length > 0 && !".!?".Contains(t[^1])) t += ".";

        lock (_bgLock)
        {
            _bgQueue.Add(new Utterance(t, engineOverride, voiceOverride));
            while (_bgQueue.Count > BackgroundQueueMax) _bgQueue.RemoveAt(0);   // oldest goes
        }
        _bgSignal.Release();
        EnsurePump();
        return Task.CompletedTask;
    }

    private void EnsurePump()
    {
        if (_bgPump is { IsCompleted: false }) return;
        _bgPump = Task.Run(BackgroundPumpAsync);
    }

    /// <summary>
    /// Speaks the background queue in order, forever.
    ///
    /// Everything inside is wrapped: a single bad utterance — a synthesiser that throws,
    /// a voice model that has gone missing, a string that survives sanitising but that
    /// the engine chokes on — must never be able to kill the pump, because a dead pump
    /// means chat goes quiet for the rest of the session with no sign of why.
    /// </summary>
    private async Task BackgroundPumpAsync()
    {
        while (true)
        {
            try
            {
                await _bgSignal.WaitAsync();

                Utterance? next = null;
                lock (_bgLock)
                {
                    if (_bgQueue.Count > 0) { next = _bgQueue[0]; _bgQueue.RemoveAt(0); }
                }
                if (next is null) continue;

                // Stall guard. If an engine locks up mid-utterance we abandon that line
                // and carry on, rather than the queue wedging behind it forever. The
                // allowance scales with length so a long message is not cut short.
                int budgetMs = Math.Clamp(next.Text.Length * 130, 5000, 45000);
                using var cts = new CancellationTokenSource();
                var speak = SpeakNowAsync(next.Text, cts.Token, next.Engine, next.Voice, background: true);

                if (await Task.WhenAny(speak, Task.Delay(budgetMs)) != speak)
                {
                    cts.Cancel();
                    DiagLog.Log("TTS", $"stalled after {budgetMs} ms, abandoned: " +
                                       (next.Text.Length > 60 ? next.Text[..60] + "…" : next.Text));
                    CancelCurrentPlayback();
                }
                else await speak;      // surface nothing; observe the task
            }
            catch (OperationCanceledException) { /* a stop; keep pumping */ }
            catch (Exception ex)
            {
                CrashLogger.Log("TtsService.BackgroundPump", ex);
                try { await Task.Delay(250); } catch { }   // never spin hot on a fault
            }
        }
    }

    /// <summary>Throw away anything waiting. Used by a user stop — silence should mean
    /// silence, not a pause before the backlog resumes.</summary>
    public void ClearQueue()
    {
        lock (_bgLock) _bgQueue.Clear();
    }

    /// <summary>
    /// Monotonic counter, bumped every time speech is stopped. Multi-utterance
    /// readers (e.g. the Voice Assistant reading an article chunk by chunk)
    /// capture this before they start and bail out the moment it changes — so a
    /// single Stop press halts the WHOLE read, not just the current sentence.
    /// Without it, StopSpeaking only cancelled the sentence in flight and the
    /// reader's loop immediately spoke the next one, so speech never stopped.
    /// </summary>
    public int StopEpoch => System.Threading.Volatile.Read(ref _stopEpoch);
    private int _stopEpoch;

    /// <summary>
    /// User-initiated stop: bumps <see cref="StopEpoch"/> so multi-utterance
    /// readers abort the whole read, then cancels the sentence in flight.
    /// </summary>
    public void StopSpeaking()
    {
        System.Threading.Interlocked.Increment(ref _stopEpoch);
        ClearQueue();          // silence means silence, not "pause then resume the backlog"
        CancelCurrentPlayback();
    }

    /// <summary>
    /// Cancel just the utterance currently playing, WITHOUT bumping the epoch.
    /// Used by SpeakAsync to interrupt the previous sentence before starting the
    /// next — which must not look like a user stop to a chunked reader.
    /// </summary>
    private void CancelCurrentPlayback()
    {
        try { EchoesUnseen.Services.NvdaOutput.Cancel(); } catch { }   // stop NVDA speech too
        lock (_playbackLock)
        {
            try { _currentCts?.Cancel(); } catch { }
            try { _currentOutput?.Stop(); } catch { }
            try { _currentOutput?.Dispose(); } catch { }
            try { _currentStream?.Dispose(); } catch { }
            _currentOutput = null;
            _currentStream = null;
            _currentCts = null;
        }
    }

    /// <summary>Expose engines for the Settings UI to enumerate voices per engine.</summary>
    /// <summary>True if the Piper engine binary is present in either location.</summary>
    public bool IsPiperInstalled => _piper.IsInstalled;

    /// <summary>The Piper engine, for the Settings voice manager (download / preview).</summary>
    public PiperTtsEngine Piper => _piper;

    /// <summary>
    /// Speak a short sample with a SPECIFIC engine + voice, ignoring the saved
    /// selection — used by the Settings "Preview" buttons so the user can hear a
    /// voice before committing to it.
    /// </summary>
    public async Task PreviewAsync(string engine, string voiceId)
    {
        CancelCurrentPlayback();
        var cts = new CancellationTokenSource();
        lock (_playbackLock) { _currentCts = cts; }
        try
        {
            IsSpeaking = true;
            SpeakingStateChanged?.Invoke(this, true);
            var eng = GetEngine(engine);
            var audio = await eng.SynthesizeAsync(
                "Hello, Commander. This is how I sound.", voiceId, _settings.Current.TtsSpeed, cts.Token);
            if (!cts.Token.IsCancellationRequested)
                await PlayAudioAsync(audio, _settings.Current.Volume, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogger.Log("TtsService.PreviewAsync", ex); }
        finally { IsSpeaking = false; SpeakingStateChanged?.Invoke(this, false); }
    }

    /// <summary>Re-resolve Piper's exe path after the auto-downloader runs.</summary>
    public void RefreshPiper() => _piper.RefreshExePath();

    public ITtsEngine GetEngine(string name) => name switch
    {
        "piper"       => _piper,
        "elevenlabs"  => _elevenLabs,
        "sapi"        => _sapi,
        "winnatural"  => _winNatural,
        _             => _piper,
    };

    /// <summary>Enumerate every voice across every engine — used by the Settings
    /// UI to present a single combined dropdown labeled by engine.</summary>
    public async Task<List<VoiceInfo>> GetAllVoicesAsync()
    {
        var all = new List<VoiceInfo>();
        try { all.AddRange(await _piper.GetAvailableVoicesAsync()); }      catch (Exception ex) { CrashLogger.Log("Piper voices", ex); }
        try { all.AddRange(await _winNatural.GetAvailableVoicesAsync()); } catch (Exception ex) { CrashLogger.Log("WinNatural voices", ex); }
        try { all.AddRange(await _elevenLabs.GetAvailableVoicesAsync()); } catch (Exception ex) { CrashLogger.Log("ElevenLabs voices", ex); }
        try { all.AddRange(await _sapi.GetAvailableVoicesAsync()); }       catch (Exception ex) { CrashLogger.Log("SAPI voices", ex); }
        return all;
    }

    /// <summary>Get available voices for the currently-selected engine.</summary>
    public Task<List<VoiceInfo>> GetCurrentEngineVoicesAsync()
        => GetEngine(_settings.Current.VoiceEngine).GetAvailableVoicesAsync();

    // ─── Core: try preferred engine, fall back through the tiers on failure ───

    private async Task<TtsAudio> SynthesizeWithFallbackAsync(string text, CancellationToken ct,
                                                             string? engineOverride = null,
                                                             string? voiceOverride = null)
    {
        var preferred = engineOverride ?? _settings.Current.VoiceEngine;
        var voiceId = engineOverride == "sapi" ? ""
                    : !string.IsNullOrWhiteSpace(voiceOverride) ? voiceOverride!
                    : _settings.Current.VoiceId;
        var speed = _settings.Current.TtsSpeed;

        // Fallback chain per preferred engine.
        // Piper → SAPI:           if Piper isn't installed yet (binaries missing)
        //                         fall through to robotic-but-always-available SAPI.
        // ElevenLabs → Piper → SAPI: if the cloud is unreachable, try local Piper;
        //                            if Piper isn't installed either, SAPI.
        // SAPI → (terminal):      SAPI ships with Windows and effectively always
        //                         works, so it's the safe last resort.
        var chain = preferred switch
        {
            "elevenlabs" => new[] {
                (engine: (ITtsEngine)_elevenLabs, id: voiceId),
                (engine: (ITtsEngine)_piper,      id: "en_US-lessac-high"),
                (engine: (ITtsEngine)_sapi,       id: ""),
            },
            "sapi" => new[] {
                (engine: (ITtsEngine)_sapi, id: voiceId),
            },
            // Windows natural/OneCore voice → robotic SAPI if it somehow fails.
            // Both are in-process, so this whole chain never spawns a process.
            "winnatural" => new[] {
                (engine: (ITtsEngine)_winNatural, id: voiceId),
                (engine: (ITtsEngine)_sapi,       id: ""),
            },
            _ /* piper or default */ => new[] {
                (engine: (ITtsEngine)_piper, id: voiceId),
                (engine: (ITtsEngine)_sapi,  id: ""),
            },
        };

        Exception? lastError = null;
        foreach (var (engine, id) in chain)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await engine.SynthesizeAsync(BuildUtterance(engine, preferred, text), id, speed, ct);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CrashLogger.Log($"TtsService fallback — {engine.EngineName} failed", ex);
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidOperationException("All TTS engines failed.");
    }

    // One-time-per-session notice: when the user expects a natural voice
    // (Piper or ElevenLabs) but we've fallen back to robotic SAPI, explain
    // why ONCE, prepended to the first fallback utterance. Without this, a
    // blind user just hears a suddenly-robotic voice with no explanation.
    private bool _fallbackNoticeSpoken;
    private string BuildUtterance(ITtsEngine engine, string preferred, string text)
    {
        if (engine.EngineName == "sapi" && preferred != "sapi" && !_fallbackNoticeSpoken)
        {
            _fallbackNoticeSpoken = true;
            var reason = preferred == "piper"
                ? "Natural voice is not installed yet, so I'm using the basic Windows voice. Run the install piper script, or see Settings, to enable the natural voice."
                : "The cloud voice is unavailable, so I'm using the basic Windows voice.";
            return reason + " ... " + text;
        }
        return text;
    }

    // ─── Playback via NAudio ─────────────────────────────────────────────────

    private async Task PlayAudioAsync(TtsAudio audio, float volume, CancellationToken ct)
    {
        // Build the appropriate reader for the format.
        var ms = new MemoryStream(audio.Bytes);
        WaveStream reader = audio.Format == "mp3"
            ? new Mp3FileReader(ms)
            : new WaveFileReader(ms);

        var output = new WaveOutEvent();
        try
        {
            output.Init(reader);
            output.Volume = Math.Clamp(volume, 0f, 1f);

            lock (_playbackLock)
            {
                _currentOutput = output;
                _currentStream = reader;
            }

            var tcs = new TaskCompletionSource<bool>();
            output.PlaybackStopped += (_, _) => tcs.TrySetResult(true);
            output.Play();

            // Await either playback-complete or external cancellation.
            using (ct.Register(() => {
                try { output.Stop(); } catch { }
                tcs.TrySetResult(false);
            }))
            {
                await tcs.Task;
            }
        }
        finally
        {
            // ALWAYS dispose this output/reader — a leaked WaveOutEvent handle is
            // exactly what silences all audio after a long session.
            try { output.Dispose(); } catch { }
            try { reader.Dispose(); } catch { }
            try { ms.Dispose(); } catch { }
            lock (_playbackLock)
            {
                if (ReferenceEquals(_currentOutput, output)) { _currentOutput = null; _currentStream = null; }
            }
        }
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

    public void Dispose() => StopSpeaking();
}
