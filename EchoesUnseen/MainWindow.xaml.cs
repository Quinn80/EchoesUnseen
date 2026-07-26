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
    private HoverReader? _hoverReader;
    private CursorReader? _cursorReader;

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
        }
        catch (Exception ex) { CrashLogger.Log("SpeakStartupAsync", ex); }
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
            _ = _cursorReader.ReadAsync();
        }));
        Hotkeys.Register(kb.StopSpeaking,    () => Dispatcher.Invoke(() => Tts.StopSpeaking()));
        Hotkeys.Register(kb.ToggleHoverRead, () => Dispatcher.Invoke(ToggleHoverRead));
        Hotkeys.Register(kb.RecenterHud,     () => RadialHud.ResetPosition());
        Hotkeys.Register(kb.Quit,            () => Dispatcher.Invoke(QuitApp));
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

    /// <summary>
    /// Quit. A transparent, no-activate, not-in-taskbar overlay can't be closed
    /// with Alt+F4 (that goes to the game underneath), so this hotkey is the
    /// reliable exit. Speaks a short goodbye first so a blind user hears it go.
    /// </summary>
    private async void QuitApp()
    {
        try
        {
            Tts.StopSpeaking();
            await Tts.SpeakAsync("Closing Echoes Unseen. Goodbye.");
        }
        catch (Exception ex) { CrashLogger.Log("Quit hotkey", ex); }
        finally { System.Windows.Application.Current.Shutdown(); }
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

            // Tell the HUD to update its visual hover state (opacity, drag hint).
            // While a panel is open the wheel stays fully lit (v21.5) so the
            // user can see and click other gems to switch panels.
            RadialHud.UpdateCursorOver(cursorOverHud || _panelOpen);

            // Decide click-through. Force it OFF while a panel is open, while
            // keyboard-navigation mode is active, or while the wheel is being
            // dragged — otherwise follow the cursor.
            //
            // The drag case matters: a fast drag lets the cursor briefly outrun
            // the wheel and leave the hit circle. Without this check the very
            // next poll re-enabled WS_EX_TRANSPARENT, which drops the mouse
            // capture and aborts the drag — the wheel would stick after a few
            // pixels of movement.
            var shouldBeClickThrough =
                !_panelOpen && !_keyboardMode && !RadialHud.IsDragging && !cursorOverHud;
            SetClickThrough(shouldBeClickThrough);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("PollCursor", ex);
        }
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
