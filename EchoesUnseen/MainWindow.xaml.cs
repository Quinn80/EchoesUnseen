using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen;

/// <summary>
/// The main overlay window. Always full-screen, always transparent, always
/// click-through by default. The RadialHud and whatever panel is currently
/// open live inside this single window.
///
/// CLICK-THROUGH STRATEGY:
///   The window is initially WS_EX_LAYERED | WS_EX_TRANSPARENT so every mouse
///   event passes through to whatever window is underneath (Guild Wars 2).
///   When the cursor enters the HUD ring or a panel, we remove WS_EX_TRANSPARENT
///   so clicks register. When it leaves, we restore it. This is the equivalent
///   of Electron's setIgnoreMouseEvents() but done properly via Win32.
///
/// WINDOW ACTIVATION:
///   We set Focusable="False" and use WS_EX_NOACTIVATE so clicking on the HUD
///   doesn't steal focus from Guild Wars 2 — the game keeps its keyboard focus
///   even while the user interacts with our overlay.
///
/// SHUTDOWN:
///   Services are disposed in reverse order of creation: hotkeys first so they
///   can't fire during teardown, then TTS so it doesn't try to play during
///   service disposal, then everything else.
/// </summary>
public partial class MainWindow : Window
{
    // ── Win32 interop for transparent click-through ──────────────────────────
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int newLong);

    // Used by the polling timer to find the cursor's screen position even when
    // our window is in click-through mode (which suppresses WPF mouse events).
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    // ── Services owned by the main window ────────────────────────────────────
    public MumbleLinkReader MumbleLink { get; private set; } = null!;
    public TtsService Tts { get; private set; } = null!;
    public GlobalHotkeyService Hotkeys { get; private set; } = null!;
    public Gw2ApiService Gw2Api { get; private set; } = null!;
    public EarconService Earcons { get; private set; } = null!;
    public KeyWatcher KeyWatch { get; private set; } = null!;
    private WvwService? _wvw;
    private MetaEventService? _meta;
    private HoverReader? _hoverReader;
    private CursorReader? _cursorReader;
    /// <summary>Local-AI service (Ollama) for panels that want it (e.g. chat summary).</summary>

    /// <summary>Re-read markers.json into the on-screen marker overlay (after import).</summary>
    public void ReloadMarkerOverlay() => Markers?.ReloadMarkers();
    private bool _loadedOnce;

    /// <summary>
    /// Panels are created once and kept, so a feature the user switched on stays
    /// on after its window is closed. Closed hosts live in BackgroundPanels.
    /// </summary>
    private readonly Dictionary<string, Views.PanelHost> _hosts = new();

    // Keyboard navigation mode (Ctrl+Shift+K): lets blind users drive the HUD
    // with arrow keys instead of the mouse. While active, the window is
    // activatable and holds keyboard focus; on exit, focus returns to the
    // window that had it before (usually Guild Wars 2).
    private bool _keyboardMode;
    private IntPtr _prevForeground = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private IntPtr _hwnd;
    private bool _clickThroughOn = true;
    private bool _panelOpen = false;
    private DispatcherTimer? _cursorPollTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Wire up the HUD's panel-open event.
        // NOTE: We do NOT wire MouseCaptureRequested/Released here. Those events
        // depend on WPF MouseEnter/MouseLeave which don't fire while the window
        // is in click-through mode (chicken-and-egg). Instead, we start a polling
        // timer below that uses Win32 GetCursorPos to do the hit-test ourselves.
        Loaded += (_, _) =>
        {
            // WPF's Loaded can fire more than once (re-parenting) — guard so we
            // don't subscribe PanelRequested twice (which double-fires panel opens
            // and their announcements) or start two cursor-poll timers.
            if (_loadedOnce) return;
            _loadedOnce = true;
            RadialHud.PanelRequested += OnPanelRequested;
            StartCursorPolling();
        };

        Closed += OnClosed;
    }

    /// <summary>
    /// Apply the extended window styles — must happen after the HWND exists.
    /// This is also where we instantiate services that need the HWND (hotkeys).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Start fully transparent and click-through; the HUD will ask us to
        // capture the cursor when it's hovered.
        var exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        // Now that the HWND exists, create services that need it.
        try
        {
            // 1. MumbleLink reader (safe even if GW2 isn't running yet).
            MumbleLink = new MumbleLinkReader();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("MainWindow MumbleLinkReader init", ex);
            MessageBox.Show(
                "Could not initialize the MumbleLink reader. Position-dependent features (Trail Navigator, Map Completion, Heart Quests) will not work. See crash.log.",
                "Echoes Unseen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 2. TTS service — initializes Piper/ElevenLabs/SAPI engines lazily.
        Tts = new TtsService(App.Settings);

        // 2a. Earcons — short audio cues confirming panel open/close.
        Earcons = new EarconService(App.Settings);
        EarconService.Shared = Earcons;   // so the nav's interactive-item chime can play

        // 2b. GW2 REST API client (cached HttpClient + per-map caching)
        Gw2Api = new Gw2ApiService(App.Settings);

        // 2c. Hover-to-read: speak whatever the pointer rests on (low-vision aid).
        _hoverReader = new HoverReader(this, Tts, App.Settings);

        // 3. Global hotkeys — F1–F12 for panels, Ctrl+Shift+H for HUD rescue, Escape to stop speaking.
        Hotkeys = new GlobalHotkeyService(this);
        RegisterHotkeys();

        // Hand services down to the HUD so panels can use them.
        RadialHud.AttachServices(MumbleLink, Tts, Hotkeys, Earcons);
        RadialHud.KeyboardModeExited += (_, _) => ExitKeyboardMode();

        Markers.AttachServices(MumbleLink);

        // Guitar-Hero guide overlay + its read-only key watcher (step mode).
        KeyWatch = new KeyWatcher();
        Guide.AttachServices(Tts, KeyWatch);

        // WvW awareness. (Combat alert was removed — it randomly played at full
        // volume regardless of the setting; not worth the risk in an a11y app.)
        _wvw = new WvwService(MumbleLink, Tts, Gw2Api);

        // Meta-event / world-boss audio alarm (fixed daily clock; no game data needed).
        _meta = new MetaEventService(Tts);


        // 4. Startup flourish + greeting — EVERY launch. The soft rising chime
        // plays first (a warm "the world opens" cue), then Nova greets the
        // Commander, so a blind user immediately knows the app is alive. The
        // detailed key-by-key orientation below is spoken only on the very first
        // run, so returning users get the greeting without the long tutorial.
        Earcons?.StartupChime();
        _ = SpeakStartupAsync();

        // 5. Auto-download the natural voice engine (Piper) if it isn't present.
        // Runs in the background so the HUD is usable immediately; TTS falls back
        // to Windows SAPI until the download finishes. Progress is spoken aloud.
        _ = EnsurePiperInstalledAsync();
    }

    /// <summary>
    /// One-time background download of the Piper engine + default voice.
    /// Speaks progress through whatever engine is currently working (SAPI at
    /// first), then switches Piper on once the files land.
    /// </summary>
    private async Task EnsurePiperInstalledAsync()
    {
        try
        {
            if (Tts.IsPiperInstalled) { _ = FetchStarterVoicesAsync(); return; }

            var installer = new PiperInstaller(App.Settings.AppDataDirectory);
            // Speak each progress line. Serialize so lines don't overlap.
            installer.Progress += (_, msg) => Dispatcher.Invoke(() => _ = Tts.SpeakAsync(msg));

            var ok = await installer.EnsureInstalledAsync();
            if (ok)
            {
                // Point the engine at the freshly downloaded binary and make
                // Piper the active voice so the user immediately hears it.
                Tts.RefreshPiper();
                if (App.Settings.Current.VoiceEngine == "sapi")
                {
                    App.Settings.Current.VoiceEngine = "piper";
                    App.Settings.Current.VoiceId = "en_US-lessac-high";
                    App.Settings.NotifyChanged();
                }
                await Tts.SpeakAsync("The natural voice is ready.");
                _ = FetchStarterVoicesAsync();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("EnsurePiperInstalledAsync", ex);
        }
    }

    /// <summary>
    /// Quietly fetch the six starter voices in the background so a new user has
    /// a real choice in Settings without downloading each one by hand.
    ///
    /// Deliberately undemanding: it runs AFTER the app is already usable and
    /// speaking, downloads one at a time so it never saturates the connection,
    /// skips anything already present, and stays silent apart from a single
    /// line when the whole set has landed. Failures are ignored — the default
    /// voice already works, so extra voices are a bonus, never a blocker.
    /// </summary>
    private async Task FetchStarterVoicesAsync()
    {
        try
        {
            var missing = Services.Tts.PiperVoiceCatalog.StarterPack
                .Where(id => !Tts.Piper.IsVoiceDownloaded(id))
                .ToList();
            if (missing.Count == 0) return;

            foreach (var id in missing)
                await Tts.Piper.DownloadVoiceAsync(id);   // silent; no progress spam

            var got = Services.Tts.PiperVoiceCatalog.StarterPack
                .Count(id => Tts.Piper.IsVoiceDownloaded(id));
            if (got > 0)
                await Tts.SpeakAsync(
                    $"{got} extra voices are ready. You can hear them in Settings, under Voice.");
        }
        catch (Exception ex) { CrashLogger.Log("FetchStarterVoicesAsync", ex); }
    }

    /// <summary>
    /// Speak the greeting on every launch, then (first run only) the full
    /// key-by-key orientation. A short delay lets the startup chime breathe
    /// before Nova speaks over it.
    /// </summary>
    private async Task SpeakStartupAsync()
    {
        try
        {
            await Task.Delay(650); // let the chime swell first
            await Tts.SpeakAsync("Hello, Commander. Echoes Unseen is ready.");
#if !DISTRIBUTION
            // A test build says which hover reader it is running, so a test can never again
            // exercise the wrong one without anyone hearing it.
            await Tts.SpeakAsync(App.Settings.Current.HoverTargetingFusion
                ? "Hover targeting: enhanced, OpenCV plus RapidOCR."
                : "Hover targeting: classic.");
#endif

            // First launch → open the Welcome card. It lists the starter controls
            // (built from the live keybinds, so it can't go stale) and reads them
            // aloud itself, which is friendlier than one long unbroken speech the
            // user can't pause or replay.
            if (!App.Settings.Current.FirstRunIntroSpoken)
            {
                App.Settings.Current.FirstRunIntroSpoken = true;
                App.Settings.NotifyChanged();
                OpenPanel("welcome");
            }

            _ = CheckForUpdatesAsync();
            PrewarmNavigation();
        }
        catch (Exception ex) { CrashLogger.Log("SpeakStartupAsync", ex); }
    }

    /// <summary>
    /// Create the Trail Navigator in the background at launch and park it, so its
    /// nav poll runs from the moment the app starts. That's what feeds the
    /// always-on compass — without this, the compass stays blank until the user
    /// opens Trail Navigator once. The panel is never shown here; it just lives
    /// in the parked layer doing its background nav work like a closed panel.
    /// </summary>
    private void PrewarmNavigation()
    {
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_hosts.ContainsKey("trailnav")) return;
                var host = new Views.PanelHost();
                host.AttachServices(MumbleLink, Tts, Hotkeys, Gw2Api);
                host.OpenPanel("trailnav");
                host.CloseRequested += (_, _) => ClosePanel();
                _hosts["trailnav"] = host;
                if (!BackgroundPanels.Children.Contains(host))
                    BackgroundPanels.Children.Add(host);
            }
            catch (Exception ex) { CrashLogger.Log("PrewarmNavigation", ex); }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Best-effort update check a few seconds after launch. Announces a
    /// newer release aloud (accessible) and once only; silent if up to date or
    /// offline. Never blocks startup.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            await Task.Delay(6000); // stay out of the way of the greeting
            var info = await UpdateService.CheckAsync();
            if (info == null) return;
            await Tts.SpeakAsync(
                $"An update is available: {info.LatestLabel}. Visit the Echoes Unseen download page to get the latest version.");
        }
        catch (Exception ex) { CrashLogger.Log("CheckForUpdatesAsync", ex); }
    }

    /// <summary>
    /// Re-read keybinds from settings and register global hotkeys.
    /// Called on startup and whenever the user changes keybinds in Settings.
    /// </summary>
    public void RegisterHotkeys()
    {
        Hotkeys.UnregisterAll();
        var kb = App.Settings.Current.Keybinds;

        // ── Wheel navigation: Alt + arrows, Alt+Enter ────────────────────────
        // One simple scheme replaces the old twelve Ctrl+Shift+F# panel keys AND
        // the separate keyboard-nav mode. Alt+Left/Right/Up/Down step around the
        // wheel (Nova speaks each panel); Alt+Enter opens the selected one. These
        // are global, so they work while Guild Wars 2 has focus, and they don't
        // steal focus the way the old mode did.
        //
        // Trade-off: as global hotkeys these claim Alt+arrows system-wide while
        // the app runs, so Alt+Left/Right won't do browser "back/forward" and
        // Alt+Up won't do Explorer "up a folder" until Echoes Unseen is closed.
        Hotkeys.Register("Alt+Right", () => Dispatcher.Invoke(() => RadialHud.NavMove(+1)));
        Hotkeys.Register("Alt+Down",  () => Dispatcher.Invoke(() => RadialHud.NavMove(+1)));
        Hotkeys.Register("Alt+Left",  () => Dispatcher.Invoke(() => RadialHud.NavMove(-1)));
        Hotkeys.Register("Alt+Up",    () => Dispatcher.Invoke(() => RadialHud.NavMove(-1)));

        // Ctrl+Shift+arrows → move the wheel itself, without needing the mouse.
        Hotkeys.Register("Ctrl+Shift+Left",  () => Dispatcher.Invoke(() => RadialHud.NudgePosition(-1, 0)));
        Hotkeys.Register("Ctrl+Shift+Right", () => Dispatcher.Invoke(() => RadialHud.NudgePosition(+1, 0)));
        Hotkeys.Register("Ctrl+Shift+Up",    () => Dispatcher.Invoke(() => RadialHud.NudgePosition(0, -1)));
        Hotkeys.Register("Ctrl+Shift+Down",  () => Dispatcher.Invoke(() => RadialHud.NudgePosition(0, +1)));

        // ── Rebindable actions (Settings → Keybinds) ─────────────────────────
        Hotkeys.Register(kb.OpenSelected,    () => Dispatcher.Invoke(() => RadialHud.NavActivate()));
        Hotkeys.Register(kb.ReadUnderCursor, () => Dispatcher.Invoke(() =>
        {
            _cursorReader ??= new CursorReader(Tts);
            // When local AI is on, the manual read DESCRIBES the region with the
            // vision model; otherwise it OCRs as before.
            _ = _cursorReader.ReadAsync();
        }));
        Hotkeys.Register(kb.StopSpeaking,    () => Dispatcher.Invoke(() => Tts.StopSpeaking()));
        Hotkeys.Register(kb.ToggleHoverRead, () => Dispatcher.Invoke(ToggleHoverRead));
        Hotkeys.Register(kb.QuietMode,       () => Dispatcher.Invoke(ToggleQuietMode));
        Hotkeys.Register(kb.ReadObjective,   () => Dispatcher.Invoke(() =>
        {
            _cursorReader ??= new CursorReader(Tts);
            _ = _cursorReader.ReadObjectiveAsync();
        }));
        // Spoken navigation: "which way?" clock check, and turn-by-turn on/off. Both
        // reach the (pre-warmed) Trail Navigator, which owns the world-space target.
        Hotkeys.Register(kb.GuideDirection,  () => Dispatcher.Invoke(() =>
        {
            var nav = Views.Panels.TrailNavigatorPanel.Current;
            if (nav != null) nav.SpeakGuideDirection();
            else _ = Tts.SpeakAsync("Navigation is still starting up.");
        }));
        Hotkeys.Register(kb.ToggleVoiceGuide, () => Dispatcher.Invoke(() =>
        {
            var nav = Views.Panels.TrailNavigatorPanel.Current;
            if (nav != null) nav.ToggleVoiceGuide();
            else _ = Tts.SpeakAsync("Navigation is still starting up.");
        }));
        Hotkeys.Register(kb.CopyWaypoint,    () => Dispatcher.Invoke(() =>
        {
            var nav = Views.Panels.TrailNavigatorPanel.Current;
            if (nav != null) nav.CopyNearestWaypoint();
            else _ = Tts.SpeakAsync("Navigation is still starting up.");
        }));
        Hotkeys.Register(kb.NextEvents,      () => Dispatcher.Invoke(() =>
            MetaEventService.Shared?.AnnounceNext(3)));
        Hotkeys.Register(kb.ReadBags,        () => Dispatcher.Invoke(() => _ = ReadBagSummaryAsync()));
        Hotkeys.Register(kb.ReadWallet,      () => Dispatcher.Invoke(() => _ = ReadWalletAsync()));
        Hotkeys.Register(kb.RecordBug,       () => Dispatcher.Invoke(ToggleBugRecording));
        Hotkeys.Register(kb.GuideSelfCheck,  () => Dispatcher.Invoke(() =>
        {
            var nav = Views.Panels.TrailNavigatorPanel.Current;
            if (nav != null) nav.SpeakSelfCheck();
            else _ = Tts.SpeakAsync("Navigation is still starting up.");
        }));
        Hotkeys.Register(kb.RecenterHud,     () => RadialHud.ResetPosition());
        Hotkeys.Register(kb.Quit,            () => Dispatcher.Invoke(QuitApp));

        // Say once, out loud, which shortcuts Windows would not give us. Silently
        // dropping a hotkey and letting the user press it forever is the worst option.
        if (GlobalHotkeyService.Failed.Count > 0)
        {
            var lost = string.Join(", ", GlobalHotkeyService.Failed);
            DiagLog.Log("HOTKEY", "unavailable this session: " + lost);
            _ = Tts.SpeakAsync($"Note: {GlobalHotkeyService.Failed.Count} shortcut" +
                (GlobalHotkeyService.Failed.Count == 1 ? " is" : "s are") +
                " already used by another program and won't work: " + lost +
                ". You can change them in Settings, Keybinds.");
        }
    }

    /// <summary>Speak a quick birds-eye summary of the current character's bag space and
    /// contents by rarity — an audible "how full am I?" without squinting at icons.</summary>
    /// <summary>
    /// Say how much money the account has, from the account rather than from the screen.
    ///
    /// Quinn asked for the gold in her inventory to be readable, and it went through OCR
    /// because that is where the number is drawn. It half worked: the coin icons between
    /// the figures come back as letters, the amount runs off the left of the capture box
    /// if the pointer is on it, and in the end she zoomed the whole screen in so she
    /// could aim at it. That is a lot of machinery for a number the API already knows to
    /// the copper.
    ///
    /// Currency 1 is Coin, held as a total number of copper.
    /// </summary>
    private async Task ReadWalletAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey))
            {
                await Tts.SpeakAsync("I need a Guild Wars 2 API key to read your money. "
                                   + "You can add one in Settings, under API Keys.");
                return;
            }

            var wallet = await Gw2Api.GetWalletAsync();
            var coin = wallet?.FirstOrDefault(c => c.Id == 1);
            if (coin == null)
            {
                await Tts.SpeakAsync("I couldn't read your money just now.");
                return;
            }

            var total = coin.Value;
            var gold = total / 10000;
            var silver = total / 100 % 100;
            var copper = total % 100;

            // Say only the parts that are there - "3 silver, 5 copper" rather than
            // "0 gold, 3 silver, 5 copper".
            var parts = new List<string>();
            if (gold > 0) parts.Add($"{gold} gold");
            if (silver > 0) parts.Add($"{silver} silver");
            if (copper > 0 || parts.Count == 0) parts.Add($"{copper} copper");

            DiagLog.Log("WALLET", $"{gold}g {silver}s {copper}c");
            await Tts.SpeakAsync(string.Join(", ", parts) + ".");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("MainWindow.ReadWalletAsync", ex);
            await Tts.SpeakAsync("Something went wrong reading your money.");
        }
    }

    private async Task ReadBagSummaryAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey))
            { _ = Tts.SpeakAsync("Set a Guild Wars 2 API key in Settings to read your bags."); return; }

            var name = MumbleLink.Read()?.CharacterName;
            if (string.IsNullOrWhiteSpace(name))
            { _ = Tts.SpeakAsync("I can't tell which character you're on. Log into a character and try again."); return; }

            _ = Tts.SpeakAsync($"Checking {name}'s bags.");
            var inv = await Gw2Api.GetCharacterInventoryAsync(name);
            if (inv?.Bags == null) { _ = Tts.SpeakAsync("Couldn't read your bags right now."); return; }

            int total = 0, empty = 0;
            var ids = new List<int>();
            var counts = new Dictionary<int, int>();   // itemId -> stack count total
            foreach (var bag in inv.Bags)
            {
                if (bag?.Inventory == null) continue;
                foreach (var slot in bag.Inventory)
                {
                    total++;
                    if (slot == null) { empty++; continue; }
                    ids.Add(slot.Id);
                    counts[slot.Id] = counts.GetValueOrDefault(slot.Id) + Math.Max(1, slot.Count);
                }
            }
            if (total == 0) { _ = Tts.SpeakAsync($"{name} has no bags equipped."); return; }

            var items = await Gw2Api.GetItemsAsync(ids.Distinct());
            var rarity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var r = it.Rarity ?? "Basic";
                rarity[r] = rarity.GetValueOrDefault(r) + 1;   // count distinct item TYPES per rarity
            }

            // Lead with space, then call out the rarities that matter most.
            string Part(string r) => rarity.TryGetValue(r, out var c) && c > 0 ? $"{c} {r.ToLower()}" : "";
            var notable = new[] { "Legendary", "Ascended", "Exotic", "Rare", "Masterwork" }
                .Select(Part).Where(p => p.Length > 0).ToList();
            string rar = notable.Count > 0 ? " Item types: " + string.Join(", ", notable) + "." : "";

            _ = Tts.SpeakAsync($"{name}: {empty} of {total} slots free.{rar}");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ReadBagSummary", ex);
            _ = Tts.SpeakAsync("Something went wrong reading your bags.");
        }
    }


    /// <summary>
    /// Start or stop a bug recording, speaking each transition.
    ///
    /// Spoken rather than shown, because the moment worth recording is usually the
    /// moment you can least afford to go looking at a panel — and because there is no
    /// point offering a blind user a recorder whose only "on" indicator is a red dot.
    /// </summary>
    private void ToggleBugRecording()
    {
        if (!App.Settings.Current.BugRecorderEnabled)
        {
            _ = Tts.SpeakAsync("Bug recording is switched off in Settings.");
            return;
        }

        if (BugRecorderService.IsRecording)
        {
            var zip = BugRecorderService.Stop();
            _ = Tts.SpeakAsync(zip == null
                ? "Recording stopped, but the report could not be saved."
                : "Recording stopped. Bug report saved to your Downloads folder, as a zip file starting Echoes Unseen bug.");
            return;
        }

        BugRecorderService.AutoStopped -= OnBugRecordingAutoStopped;
        BugRecorderService.AutoStopped += OnBugRecordingAutoStopped;

        var dir = BugRecorderService.Start();
        _ = Tts.SpeakAsync(dir == null
            ? "Could not start recording."
            : $"Bug recording started. Show me the problem, then press the same keys again to stop. It stops on its own after {BugRecorderService.MaxSeconds / 60} minutes.");
    }

    /// <summary>The two minutes ran out. Say so, so a recording that ended on its own is
    /// never mistaken for one still running.</summary>
    private void OnBugRecordingAutoStopped(string? zip)
    {
        Dispatcher.Invoke(() => _ = Tts.SpeakAsync(zip == null
            ? "Recording reached its two minute limit, but the report could not be saved."
            : "Recording finished at the two minute limit. Bug report saved to your Downloads folder."));
    }

    /// <summary>Flip hover-to-read and announce the new state.</summary>
    private void ToggleHoverRead()
    {
        var on = !App.Settings.Current.HoverToRead;
        App.Settings.Current.HoverToRead = on;
        App.Settings.NotifyChanged();
        Tts.StopSpeaking();
        _ = Tts.SpeakAsync(on ? "Hover to read on." : "Hover to read off.");
    }

    /// <summary>Hush the always-on readers (chat, WvW, nav) while playing, without
    /// turning anything off. The confirmation uses the default engine so it's heard
    /// even while quiet mode is on.</summary>
    private void ToggleQuietMode()
    {
        var quiet = !App.Settings.Current.QuietMode;
        App.Settings.Current.QuietMode = quiet;
        App.Settings.NotifyChanged();
        Tts.StopSpeaking();
        _ = Tts.SpeakAsync(quiet ? "Quiet mode on." : "Quiet mode off.");
    }

    /// <summary>
    /// Quit. A transparent, no-activate, not-in-taskbar overlay can't be closed
    /// with Alt+F4 (that goes to the game underneath), so this hotkey is the
    /// reliable exit. Speaks a short goodbye first so a blind user hears it go.
    /// </summary>
    private void QuitApp()
    {
        // Fire the goodbye, but NEVER let speech block the quit. A short timer
        // force-closes the app no matter what — so Ctrl+Shift+Q always works.
        try { Tts.StopSpeaking(); _ = Tts.SpeakAsync("Closing Echoes Unseen. Goodbye."); }
        catch (Exception ex) { CrashLogger.Log("Quit hotkey", ex); }

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { System.Windows.Application.Current.Shutdown(); } catch { }
            try { Environment.Exit(0); } catch { }
        };
        t.Start();
    }

    // ── Keyboard navigation mode ─────────────────────────────────────────────
    private void ToggleKeyboardMode()
    {
        Dispatcher.Invoke(() =>
        {
            if (_keyboardMode) ExitKeyboardMode();
            else               EnterKeyboardMode();
        });
    }

    private void EnterKeyboardMode()
    {
        if (_keyboardMode) return;
        _keyboardMode = true;

        // Remember who had focus (usually GW2) so we can hand it back on exit.
        _prevForeground = GetForegroundWindow();

        // Make our window activatable and clickable, then take focus.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex &= ~WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        SetClickThrough(false);
        Activate();

        RadialHud.EnterKeyboardMode();
    }

    private void ExitKeyboardMode()
    {
        if (!_keyboardMode) return;
        _keyboardMode = false;

        RadialHud.ExitKeyboardModeVisuals();

        // Restore the no-activate style and give focus back to the game.
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        if (_prevForeground != IntPtr.Zero)
            SetForegroundWindow(_prevForeground);
    }

    private void OnPanelRequested(object? sender, string panelId) => OpenPanel(panelId);

    /// <summary>
    /// Open the panel with the given ID. If a different panel is already open,
    /// close it first. If the SAME panel is already open, close it (toggle behavior).
    /// </summary>
    public void OpenPanel(string panelId)
    {
        DiagLog.Log("PANEL", $"open {panelId}");
        Dispatcher.Invoke(() =>
        {
            // Toggle: clicking the same panel that's already open closes it.
            if (PanelHost.Content is Views.PanelHost current && current.PanelId == panelId)
            {
                ClosePanel();
                return;
            }

            // Reuse a parked host if this panel has been opened before, so any
            // background work it was doing (chat scanning, sonar, playback) is
            // uninterrupted and its state is exactly where the user left it.
            if (!_hosts.TryGetValue(panelId, out var host))
            {
                host = new Views.PanelHost();
                host.AttachServices(MumbleLink, Tts, Hotkeys, Gw2Api);
                host.OpenPanel(panelId);
                host.CloseRequested += (_, _) => ClosePanel();
                _hosts[panelId] = host;
            }
            else
            {
                BackgroundPanels.Children.Remove(host);
            }
            PanelHost.Content = host;

            // A panel is open → capture cursor so buttons inside it work.
            _panelOpen = true;
            SetClickThrough(false);

            // The wheel STAYS VISIBLE while a panel is open (user's preference),
            // and the panel auto-places in the free space beside it. With the
            // native wheel this is now a layout choice rather than a constraint:
            // both live in the same WPF visual tree, so a panel CAN legitimately
            // overlap the wheel if we ever want it to.
            host.SetDefaultPlacement(RadialHud.GetWheelRectInWindow(),
                                     new System.Windows.Size(ActualWidth, ActualHeight));

            // Make the window activatable and take focus so the panel actually
            // receives keystrokes — this is what lets Escape close the panel and
            // lets text boxes inside panels accept typing. Without dropping
            // WS_EX_NOACTIVATE the panel can never get keyboard focus. We
            // remember the previous foreground window (usually GW2) to restore
            // it when the panel closes.
            if (!_keyboardMode)
                _prevForeground = GetForegroundWindow();
            var exOpen = GetWindowLong(_hwnd, GWL_EXSTYLE);
            exOpen &= ~WS_EX_NOACTIVATE;
            SetWindowLong(_hwnd, GWL_EXSTYLE, exOpen);
            Activate();
            // Focus the host so its Escape/KeyDown handler is live immediately.
            // (The v21.5 SetFocus workaround is gone: there is no browser child
            // window to steal Win32 keyboard focus any more.)
            host.Loaded += (_, _) => host.Focus();
            Earcons?.PanelOpened();
        });
    }

    public void ClosePanel()
    {
        // Park the panel instead of destroying it, so anything it runs in the
        // background keeps running with the window closed.
        if (PanelHost.Content is Views.PanelHost open)
        {
            PanelHost.Content = null;
            if (!BackgroundPanels.Children.Contains(open))
                BackgroundPanels.Children.Add(open);
        }
        _panelOpen = false;

        // Closing a panel silences whatever it was reading. Without this, Nova
        // kept talking after the panel vanished, with no obvious way to stop
        // her. The close earcon uses a separate audio path, so it still plays.
        try { Tts?.StopSpeaking(); } catch (Exception ex) { CrashLogger.Log("ClosePanel StopSpeaking", ex); }

        Services.CrashLogger.Log("MainWindow", "panel closed → close earcon queued");
        Earcons?.PanelClosed();

        // Restore the no-activate style so the overlay stops stealing focus
        // from the game, and hand focus back to whatever had it before the
        // panel opened (usually Guild Wars 2). Skip the focus handback if
        // keyboard-navigation mode is active — that mode manages focus itself.
        if (!_keyboardMode)
        {
            var exClose = GetWindowLong(_hwnd, GWL_EXSTYLE);
            exClose |= WS_EX_NOACTIVATE;
            SetWindowLong(_hwnd, GWL_EXSTYLE, exClose);
            if (_prevForeground != IntPtr.Zero)
                SetForegroundWindow(_prevForeground);
        }
        // The next poll-tick will restore click-through if the cursor is outside the HUD.
    }

    /// <summary>
    /// Toggle WS_EX_TRANSPARENT to pass/capture cursor events.
    /// Called by the polling timer when the cursor enters/leaves the HUD,
    /// and by OpenPanel/ClosePanel.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (_clickThroughOn == clickThrough) return;
        _clickThroughOn = clickThrough;

        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = clickThrough ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

        // The top-level window's WS_EX_TRANSPARENT makes the entire window
        // invisible to mouse hit-testing in one flag — no per-child handling
        // needed now that the wheel is native WPF rather than a child HWND.
    }

    /// <summary>
    /// Start the cursor-position polling timer.
    ///
    /// Why polling? When the window is in click-through mode, WPF MouseEnter/
    /// MouseLeave events never fire on the HUD — so we can't use them to know
    /// when to disable click-through. Instead, this timer ticks every 60ms,
    /// asks Win32 directly where the cursor is, and toggles click-through
    /// based on whether it's over the HUD circle.
    ///
    /// Cost: ~16 polls/sec, one Win32 call each, plus simple math. Negligible.
    /// </summary>
    private void StartCursorPolling()
    {
        _cursorPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _cursorPollTimer.Tick += (_, _) => PollCursor();
        _cursorPollTimer.Start();
    }

    private void PollCursor()
    {
        try
        {
            // If a panel is open, click-through is always off — no need to
            // check the cursor every tick. Still tick the visual hover state
            // so the HUD knows whether to brighten/dim if the panel closes.
            if (!GetCursorPos(out var pt)) return;
            var screenPoint = new Point(pt.X, pt.Y);

            var cursorOverHud = RadialHud.IsScreenPointOverHud(screenPoint);

            // While a panel is open, only its CARD should capture clicks — the
            // rest of the screen must pass clicks through to Guild Wars 2, so the
            // player can still click the game (e.g. to give it focus for
            // auto-play, or just to play). Previously an open panel forced the
            // whole overlay to capture every click, freezing interaction with the
            // game underneath.
            bool overCard = false;
            if (_panelOpen && PanelHost.Content is Views.PanelHost ph)
                overCard = ph.GetCardScreenRect().Contains(screenPoint);

            RadialHud.UpdateCursorOver(cursorOverHud || _panelOpen);

            // Capture clicks only over the wheel or the open panel's card;
            // everything else falls through to the game. Keyboard-nav mode and an
            // active drag still force capture on.
            var shouldBeClickThrough =
                !_keyboardMode && !RadialHud.IsDragging && !cursorOverHud && !overCard;
            SetClickThrough(shouldBeClickThrough);

            // Auto hover-read of game content (opt-in): when the cursor rests over
            // the game (not our HUD/panel) it OCRs and speaks what's under it.
            MaybeAutoHoverRead(screenPoint, cursorOverHud || overCard);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("PollCursor", ex);
        }
    }

    // ── Auto hover-to-read (game content) ────────────────────────────────────
    private Point _hoverLastPt;
    private DateTime _hoverRestStart = DateTime.MinValue;
    private bool _hoverReadDone;

    /// <summary>When enabled and the cursor rests still over the game for the dwell
    /// time, OCR and speak what's under it — once per rest. Skipped over our own
    /// UI and while the voice is busy.</summary>
    private void MaybeAutoHoverRead(Point p, bool overOwnUi)
    {
        var s = App.Settings.Current;
        // Only while Guild Wars 2 is the active window (you asked for in-game only),
        // and NOT while Quiet Mode is on — so hushing the app also stops hover
        // reading the 3D scene while you play.
        if (!s.HoverReadGame || overOwnUi || s.QuietMode || !IsGw2Foreground())
        {
            _hoverRestStart = DateTime.MinValue;
            _hoverReadDone = false;
            return;
        }
        // Cursor moved enough → restart the dwell, and STOP TALKING ABOUT THE LAST THING.
        //
        // Moving off an item, or onto a different one, means you are done with what you
        // were told about the old one. Carrying on reading it is the behaviour of a
        // reader that has not noticed you moved. Only hover speech is stopped: queued
        // chat and callouts keep their place.
        if (Math.Abs(p.X - _hoverLastPt.X) > 6 || Math.Abs(p.Y - _hoverLastPt.Y) > 6)
        {
            if (_hoverReadDone) Tts.StopIfTagged(CursorReader.HoverTag);
            _hoverLastPt = p;
            _hoverRestStart = DateTime.UtcNow;
            _hoverReadDone = false;

            // WHAT THE SCREEN LOOKED LIKE BEFORE SHE STOPPED.
            //
            // Taken while the pointer is still moving, because the point of it is to
            // catch the moment BEFORE the game has any reason to draw a tooltip. A few
            // milliseconds on a thumbnail, and it turns "where is the text" into "what
            // appeared" - which is the only question with a reliable answer when the
            // game draws an item's name three hundred pixels from the item.
            // Moving again, so the baseline may start following the screen once more.
            // Without this the freeze latches for the rest of the session.
            Services.Hover.TooltipFinder.Thaw();
            Services.Hover.TooltipFinder.Remember(
                Services.ScreenMetrics.MonitorAt((int)p.X, (int)p.Y));
            return;
        }
        // The pointer has stopped. Whatever the baseline is now, it is from before the
        // game had any reason to draw a tooltip - so hold it there.
        Services.Hover.TooltipFinder.Freeze();

        if (_hoverReadDone || _hoverRestStart == DateTime.MinValue) return;

        int dwell = Math.Clamp(s.HoverReadDwellMs, 300, 3000);
        if ((DateTime.UtcNow - _hoverRestStart).TotalMilliseconds < dwell) return;

        // A HOVER READ NO LONGER WAITS FOR CHAT.
        //
        // These two guards used to hold a hover read back until the voice was idle, and
        // until chat had been quiet for a second and a half. That was right when every
        // utterance interrupted the one before it, because gaps were constant. It became
        // wrong the moment chat got a queue: chat now speaks continuously in a busy map,
        // so both guards were true almost always and hovering an inventory slot did
        // nothing at all. That is what "hover is not working in inventory" was.
        //
        // Hover belongs to the immediate lane. It interrupts, reads, and the queued chat
        // carries on afterwards - which is the whole reason the lanes exist.

        // DON'T HOVER-READ THE CHAT BOX.
        //
        // The chat reader already owns that rectangle and reads it properly - grouped
        // into messages, de-duplicated, names settled. Hovering over it made the cursor
        // reader OCR the same pixels raw and speak the lot as one run-on utterance,
        // channel tags and all: "Dewdman: or not. Inbis: unreal. Inbis: never rezzing
        // anyone again. Finnbarr Maruun: durios open..." That was not a chat bug, which
        // is where it looked like it was coming from. It was this.
        if (OverChatRegion(p)) { _hoverRestStart = DateTime.MinValue; return; }

        _hoverReadDone = true;
        _cursorReader ??= new CursorReader(Tts);
        // When local AI is on, describe the hovered thing with the vision model
        // (much better than OCR); otherwise OCR + wiki lookup.
        _ = _cursorReader.ReadAsync(enhance: true);
    }

    /// <summary>
    /// Would a hover read here pick up the chat box the chat reader already owns?
    ///
    /// Two things were wrong with asking this the obvious way. The region is chosen
    /// with a WPF overlay so it is stored in DIPs, while the pointer arrives from
    /// GetCursorPos in physical pixels - at 125% the guarded area was a quarter
    /// smaller than the real chat panel and sat up and to the left of it, so pointing
    /// at the lower part of the chat walked straight past the guard.
    ///
    /// And the test was on the POINTER when the thing that matters is the BOX: it
    /// reaches well below and to the right of the cursor, so it can be sitting over
    /// the chat while the pointer is nowhere near it. Overlap is the question, not
    /// containment.
    /// </summary>
    private static bool OverChatRegion(Point p)
    {
        var s = App.Settings.Current;
        if (s.ChatRegionW < 4 || s.ChatRegionH < 4) return false;

        var chat = Services.ScreenMetrics.DipToPhysical(
            new Rect(s.ChatRegionX, s.ChatRegionY, s.ChatRegionW, s.ChatRegionH));

        var (bw, bh, ix, iy) = CursorReader.BoxFor((int)p.X, (int)p.Y);
        var box = new Rect(p.X - ix, p.Y - iy, bw, bh);

        return box.IntersectsWith(chat);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _cursorPollTimer?.Stop(); } catch { }

        // Background panels no longer stop themselves on unload, so shutdown is
        // where their timers and audio actually get cut. Without this the app
        // could linger with sonar or music still running.
        try
        {
            foreach (var host in _hosts.Values) host.ShutdownPanel();
            _hosts.Clear();
        }
        catch (Exception ex) { CrashLogger.Log("Shutdown background panels", ex); }
        try { Hotkeys?.Dispose(); }    catch (Exception ex) { CrashLogger.Log("Dispose Hotkeys", ex); }
        try { Tts?.Dispose(); }        catch (Exception ex) { CrashLogger.Log("Dispose Tts", ex); }
        try { MumbleLink?.Dispose(); } catch (Exception ex) { CrashLogger.Log("Dispose MumbleLink", ex); }
    }
}
