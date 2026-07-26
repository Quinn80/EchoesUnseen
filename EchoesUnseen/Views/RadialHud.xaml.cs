using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views;

/// <summary>
/// The radial HUD: 12 buttons arranged around a central emblem.
///
/// OPACITY STATES (matches the blueprint):
///   - Idle (not hovered, no panel open, not dragging): 8% opacity.
///     Barely visible so it doesn't obscure gameplay.
///   - Full opacity (100%):
///       - While hovered
///       - While being dragged
///       - For 3 seconds after drag release (the "justDragged" grace period,
///         so the user can see where the HUD landed)
///       - While a panel is open
///
/// DRAG BEHAVIOR:
///   - Shift+LeftDrag or MiddleDrag moves the HUD.
///   - Normal clicks activate buttons, no accidental drags.
///   - STRICT CLAMPING: the HUD center stays at least 190px from every screen
///     edge (190 = HUD radius). This means the full 380px ring is ALWAYS
///     visible, no matter what the user tries to do. Previous builds had
///     loose margin clamping that let the HUD drift half off-screen.
///   - Ctrl+Shift+H rescue shortcut snaps to screen center (registered globally
///     in MainWindow so it works even if GW2 has focus).
///
/// POSITION PERSISTENCE:
///   Saved to settings on drag release. On startup, if the saved position
///   would place any part of the ring off-screen (monitor change, resolution
///   change), we recenter instead of restoring a bad position.
/// </summary>
public partial class RadialHud : UserControl
{
    // BASE sizes describe the HUD at scale 1.0. The user's HudScale setting
    // (0.75–1.5) is applied as a single LayoutTransform on RingContainer, so
    // the artwork, the drawn gems and the buttons all scale together and can
    // never drift out of alignment. Only the values used for hit-testing and
    // screen clamping need to know about the scale.
    // The design is authored on a 760px canvas; we render at 480 (factor 0.632),
    // close to the effective on-screen size the old WebView2 host produced.
    private const double BaseHudSize = 480;

    private double _hudScale = 1.0;
    private double HudSize   => BaseHudSize * _hudScale;
    private double HudRadius => HudSize / 2; // the clamp distance from each edge

    /// <summary>
    /// The wheel's bounding square in window coordinates — used by MainWindow
    /// to place panels in the free space beside the wheel.
    /// </summary>
    public System.Windows.Rect GetWheelRectInWindow() =>
        new(Canvas.GetLeft(RingContainer), Canvas.GetTop(RingContainer), HudSize, HudSize);
    // Ring geometry, converted from the design at factor 0.632:
    //   button ring   R 250 → 158
    //   button size   132   →  83
    //   icon          58    →  36
    // Orbs and icons share this radius and are placed by exact math
    // (i*30 - 90), so every icon centres perfectly on its own orb.
    private const double ButtonPlacementRadius = 158;

    /// <summary>Per-icon fine-tuning kept for flexibility, but with the geometric
    /// ring everything is even by default so all entries are zero.</summary>
    private static readonly (double AngleNudge, double RadiusNudge)[] IconNudges =
    {
        (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0),
        (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0),
    };
    private const double ButtonSize = 83;
    private const double IconSize   = 36;

    /// <summary>The 12 HUD buttons in ring order (preserve from blueprint — do not reorder).</summary>
    // Public so the Settings panel builds its button-management list from the
    // SAME source of truth (ids, labels, ring order).
    public static readonly (string Id, string Label, string Icon)[] ButtonDefs =
    {
        ("screen-reader", "Screen Reader",   "👁"),
        ("heart-quest",   "Heart Quests",    "❤"),
        ("trail-nav",     "Trail Navigator", "🧭"),
        ("chat-reader",   "Chat Reader",     "💬"),
        ("voice-chat",    "Voice to Chat",   "🎙"),
        ("music",         "Music Player",    "🎵"),
        ("assistant",     "Oracle",          "🤖"),
        ("account",       "Account Search",  "🔍"),
        ("trading",       "Trading Post",    "📈"),
        ("build",         "Build & Gear",    "🛡"),
        ("map",           "Map Completion",  "🗺"),
        ("settings",      "Settings",        "⚙"),
    };

    // ── State ────────────────────────────────────────────────────────────────
    private bool _isDragging;

    /// <summary>
    /// True while the user is dragging the wheel. MainWindow's cursor poll must
    /// check this: if the cursor briefly outruns the wheel mid-drag, re-enabling
    /// click-through would drop the mouse capture and abort the drag.
    /// </summary>
    public bool IsDragging => _isDragging;
    private bool _justDragged;
    private Point _dragStart;
    private Point _hudStartPos;
    private DispatcherTimer? _justDraggedTimer;

    // ── Events the MainWindow listens for ────────────────────────────────────
    public event EventHandler? MouseCaptureRequested;
    public event EventHandler? MouseCaptureReleased;
    public event EventHandler<string>? PanelRequested;

    // Services are attached by MainWindow after construction.
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private EarconService? _earcons;

    public RadialHud()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys,
        EarconService? earcons = null)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _earcons = earcons;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Read the saved scale BEFORE anything is positioned so the transform,
        // hit-testing and clamping all agree from frame one.
        ApplyHudScale(App.Settings.Current.HudScale, reposition: false);

        // Static ring furniture first, then the interactive layer on top.
        BuildGlyphs();
        BuildDataNodes();
        BuildBlips();
        BuildButtons();

        RestoreSavedPosition();
        SetOpacity(IdleOpacity);
        StartSwirlAnimation();
        ApplyMinimiseSetting();

        // Live settings: HUD scale and hidden buttons take effect immediately.
        // Themes need no bridge any more — the artwork uses DynamicResource, so
        // WPF re-renders it on a theme swap by itself.
        App.Settings.Changed += OnAppSettingsChanged;
        Unloaded += (_, _) => App.Settings.Changed -= OnAppSettingsChanged;

        // NOTE: We do NOT wire MouseEnter/MouseLeave here. WPF mouse events
        // don't fire while the window is in click-through mode (WS_EX_TRANSPARENT),
        // creating a chicken-and-egg problem. Instead, MainWindow polls the
        // cursor position via Win32 GetCursorPos and calls UpdateCursorOver(true/false)
        // here whenever the state changes. This bypasses the WPF event system
        // entirely.
        RingContainer.MouseLeftButtonDown += OnMouseLeftButtonDown;
        RingContainer.MouseMove += OnMouseMove;
        RingContainer.MouseLeftButtonUp += OnMouseLeftButtonUp;
        RingContainer.MouseDown += OnMouseDown; // middle-click

        // Re-clamp on window resize (monitor change, resolution change)
        if (Window.GetWindow(this) is { } win)
            win.SizeChanged += (_, _) => ReclampToScreen();
    }

    // ── Live settings ────────────────────────────────────────────────────────
    private string _lastHiddenKey = "";

    private void OnAppSettingsChanged(object? sender, Models.AppSettings s)
    {
        // Settings can be saved from any panel; marshal to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            var newScale = Math.Clamp(s.HudScale, 0.75, 1.5);
            if (Math.Abs(newScale - _hudScale) > 0.001)
                ApplyHudScale(newScale, reposition: true);

            // Rebuild the ring only when the hidden set actually changed.
            var hiddenKey = string.Join(",", s.HiddenButtons.OrderBy(x => x));
            if (hiddenKey != _lastHiddenKey)
            {
                _lastHiddenKey = hiddenKey;
                BuildButtons();
            }

            ApplyMinimiseSetting();
        });
    }

    /// <summary>
    /// Resize the whole HUD by scaling RingContainer. One transform drives the
    /// artwork, the drawn gems and the buttons together, so nothing can drift
    /// out of alignment. When <paramref name="reposition"/> is true the wheel
    /// keeps its CENTER fixed (so it doesn't jump) and re-clamps to the screen.
    /// </summary>
    private void ApplyHudScale(double newScale, bool reposition)
    {
        newScale = Math.Clamp(newScale, 0.75, 1.5);

        var oldSize = HudSize;
        var centerX = Canvas.GetLeft(RingContainer) + oldSize / 2;
        var centerY = Canvas.GetTop(RingContainer) + oldSize / 2;

        _hudScale = newScale;
        RingContainer.LayoutTransform =
            Math.Abs(newScale - 1.0) < 0.001 ? null : new ScaleTransform(newScale, newScale);

        if (!reposition) return;

        var clamped = ClampToScreen(centerX - HudSize / 2, centerY - HudSize / 2);
        Canvas.SetLeft(RingContainer, clamped.X);
        Canvas.SetTop(RingContainer, clamped.Y);
    }

    /// <summary>
    /// Returns true if the given screen-coordinate point is inside the HUD ring's
    /// bounding circle. Called by MainWindow's polling timer to decide whether
    /// to enable or disable click-through.
    /// </summary>
    public bool IsScreenPointOverHud(Point screenPoint)
    {
        try
        {
            // Convert the cursor into RingContainer's OWN coordinate space and
            // test there.
            //
            // The previous version did the opposite — projected the ring's
            // top-left out to screen coordinates, then added HudSize/2. That
            // mixed units: PointToScreen and GetCursorPos both return PHYSICAL
            // device pixels, but HudSize is in WPF device-independent units.
            // The app declares PerMonitorV2 DPI awareness, so on any display
            // scaled above 100% the computed centre landed short of the real
            // one and the radius was too small — leaving a dead zone down the
            // RIGHT side of the wheel that got worse the higher the scaling.
            //
            // PointFromScreen walks the full visual transform chain, so it
            // handles DPI *and* the HudScale LayoutTransform for free. Local
            // coordinates are always the untransformed 0..BaseHudSize box.
            var local = RingContainer.PointFromScreen(screenPoint);

            const double c = BaseHudSize / 2;
            var dx = local.X - c;
            var dy = local.Y - c;
            return (dx * dx + dy * dy) <= (c * c);   // squared distance — no sqrt
        }
        catch
        {
            // PointFromScreen throws if the visual isn't connected yet (teardown).
            return false;
        }
    }

    /// <summary>
    /// Called by MainWindow's polling timer when the cursor transitions
    /// into or out of the HUD's circle. Drives the visual hover state
    /// (opacity, drag hint) and fires MouseCaptureRequested/Released.
    /// </summary>
    private bool _wasCursorOver;
    public void UpdateCursorOver(bool isOver)
    {
        if (_wasCursorOver == isOver) return;
        _wasCursorOver = isOver;
        if (isOver) OnCursorEnteredHud();
        else        OnCursorLeftHud();
    }

    // ── Keyboard navigation mode (Ctrl+Shift+K) ─────────────────────────────
    // Lets blind and motor-impaired users drive the wheel entirely from the
    // keyboard: Left/Right (or Up/Down) cycles through the gems, Enter or
    // Space opens the focused panel, Escape exits the mode. Every focus move
    // is announced through TTS *and* through UI Automation (so NVDA / JAWS /
    // Narrator each announce it natively too).
    private bool _keyboardMode;
    private int _kbIndex;

    /// <summary>Raised when the user presses Escape to leave keyboard mode.</summary>
    public event EventHandler? KeyboardModeExited;

    public void EnterKeyboardMode()
    {
        _keyboardMode = true;
        _kbIndex = 0;
        SetOpacity(ActiveOpacity);
        Focusable = true;
        PreviewKeyDown -= OnKeyboardModeKeyDown;
        PreviewKeyDown += OnKeyboardModeKeyDown;

        FocusButtonAt(_kbIndex, announce: false);
        _tts?.SpeakAsync(
            "Keyboard navigation on. Use the arrow keys to move around the wheel, " +
            "Enter to open, Escape to exit. " + CurrentButtonLabel());
    }

    /// <summary>Visual/state cleanup when MainWindow ends keyboard mode.</summary>
    public void ExitKeyboardModeVisuals()
    {
        _keyboardMode = false;
        PreviewKeyDown -= OnKeyboardModeKeyDown;
        SetOpacity(IdleOpacity);
        _tts?.SpeakAsync("Keyboard navigation off.");
    }

    // ── Alt + arrow global navigation ────────────────────────────────────────
    // The primary way to drive the wheel: Alt+Left/Right/Up/Down move the
    // selection and Nova speaks each panel; Alt+Enter opens the selected one.
    // These run from GLOBAL hotkeys, so they work even while Guild Wars 2 has
    // focus and — unlike the old keyboard mode — never steal focus from the
    // game. Selection is shown by enlarging the chosen gem; the wheel brightens
    // while navigating and dims again after a few seconds of stillness.
    private int _navIndex = -1;
    private DispatcherTimer? _navDimTimer;

    /// <summary>Move the wheel selection by <paramref name="dir"/> (+1 / -1) and speak it.</summary>
    public void NavMove(int dir)
    {
        var count = ButtonsCanvas.Children.Count;
        if (count == 0) return;

        _navIndex = _navIndex < 0
            ? (dir >= 0 ? 0 : count - 1)
            : (_navIndex + dir + count) % count;

        // Unfold while navigating by keyboard, so the wheel is visible for anyone
        // with partial sight following along — it re-collapses when nav goes idle.
        SetMinimised(false);

        HighlightNav();
        SetOpacity(ActiveOpacity);
        RestartNavDimTimer();
        _tts?.SpeakAsync(NavLabel());
    }

    /// <summary>Open the currently selected panel (Alt+Enter).</summary>
    public void NavActivate()
    {
        if (_navIndex < 0 || _navIndex >= ButtonsCanvas.Children.Count)
        {
            // Nothing selected yet — select the first gem so a lone Alt+Enter
            // still gives the user a foothold rather than doing nothing.
            NavMove(+1);
            return;
        }
        if (ButtonsCanvas.Children[_navIndex] is Button b && b.Tag is string id)
        {
            _tts?.SpeakAsync($"Opening {NavLabel()}.");
            PanelRequested?.Invoke(this, id);
        }
    }

    private void HighlightNav()
    {
        for (int i = 0; i < ButtonsCanvas.Children.Count; i++)
            if (ButtonsCanvas.Children[i] is Button b)
                AnimateButtonScale(b, i == _navIndex ? 1.25 : 1.0);
    }

    private string NavLabel()
    {
        if (_navIndex >= 0 && _navIndex < ButtonsCanvas.Children.Count &&
            ButtonsCanvas.Children[_navIndex] is Button b)
            return System.Windows.Automation.AutomationProperties.GetName(b);
        return string.Empty;
    }

    private void RestartNavDimTimer()
    {
        _navDimTimer?.Stop();
        _navDimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _navDimTimer.Tick += (_, _) =>
        {
            _navDimTimer?.Stop();
            _navIndex = -1;
            HighlightNav();          // reset all gems to normal size
            if (!IsMouseOver)
            {
                SetOpacity(IdleOpacity);
                if (App.Settings.Current.MinimiseHud && !_wasCursorOver) SetMinimised(true);
            }
        };
        _navDimTimer.Start();
    }

    private void OnKeyboardModeKeyDown(object sender, KeyEventArgs e)
    {
        if (!_keyboardMode) return;
        var count = ButtonsCanvas.Children.Count;
        if (count == 0) return;

        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
                _kbIndex = (_kbIndex + 1) % count;
                FocusButtonAt(_kbIndex, announce: true);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
                _kbIndex = (_kbIndex - 1 + count) % count;
                FocusButtonAt(_kbIndex, announce: true);
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Space:
                if (ButtonsCanvas.Children[_kbIndex] is Button b && b.Tag is string id)
                {
                    _tts?.SpeakAsync($"Opening {CurrentButtonLabel()}.");
                    PanelRequested?.Invoke(this, id);
                }
                e.Handled = true;
                break;

            case Key.Escape:
                KeyboardModeExited?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void FocusButtonAt(int index, bool announce)
    {
        if (ButtonsCanvas.Children.Count == 0) return;
        index = Math.Clamp(index, 0, ButtonsCanvas.Children.Count - 1);
        if (ButtonsCanvas.Children[index] is Button btn)
        {
            btn.Focus(); // moves WPF keyboard focus → UIA focus event → screen reader announces
            if (announce) _tts?.SpeakAsync(CurrentButtonLabel());
        }
    }

    private string CurrentButtonLabel()
    {
        if (_kbIndex >= 0 && _kbIndex < ButtonsCanvas.Children.Count &&
            ButtonsCanvas.Children[_kbIndex] is Button b)
        {
            return System.Windows.Automation.AutomationProperties.GetName(b);
        }
        return string.Empty;
    }

    // Bumped from 0.08 in the previous build — the new gem artwork has more
    // visual presence and 8% made it borderline invisible against busy
    // backgrounds. 30% reads as a faint but findable glow.
    private const double IdleOpacity = 0.30;
    private const double ActiveOpacity = 1.0;

    // ── Button layout ────────────────────────────────────────────────────────
    private void BuildButtons()
    {
        ButtonsCanvas.Children.Clear();
        GemsCanvas.Children.Clear();
        var hidden = App.Settings.Current.HiddenButtons;
        _lastHiddenKey = string.Join(",", hidden.OrderBy(x => x));
        var visible = ButtonDefs.Where(b => !hidden.Contains(b.Id)).ToArray();
        if (visible.Length == 0) return;

        double angleStep = 360.0 / visible.Length;
        for (int i = 0; i < visible.Length; i++)
        {
            var def = visible[i];
            var baseAngle = (i * angleStep) - 90; // start at top
            // Apply per-icon fine-tuning (only if the nudge table covers this index).
            var nudge = i < IconNudges.Length ? IconNudges[i] : (0.0, 0.0);
            var angleDeg = baseAngle + nudge.Item1;
            var radius = ButtonPlacementRadius + nudge.Item2;
            var angleRad = angleDeg * Math.PI / 180.0;
            var cx = HudSize / 2 + radius * Math.Cos(angleRad);
            var cy = HudSize / 2 + radius * Math.Sin(angleRad);

            // Each button carries its own neon hue, cycling pink → blue around
            // the ring exactly as the design's neon(i) helper does.
            var neon = Neon(i);

            // Draw the button's frame, glow, hollow and node gem behind the
            // icon at the SAME center point, so the icon always lands dead
            // centre on its orb.
            DrawGem(cx, cy, neon);

            // Icon: solid bright neon stroke. The design strokes its 24-grid
            // line icons with the button's `bright` colour and pulses a
            // drop-shadow glow (icon-glow, 2.4s) rather than tinting the
            // stroke — so the icon keeps full contrast at all times.
            var iconVisual = HudIcons.Build(def.Id, IconSize, new SolidColorBrush(neon.Bright));

            var btn = new Button
            {
                Width = ButtonSize,
                Height = ButtonSize,
                Content = iconVisual,
                // ── Icon visibility (accessibility) ──
                // The design's icon-glow: a coloured bloom in the button's own
                // neon hue. Zero shadow depth keeps the halo even on all sides,
                // and because the hollow beneath the icon is near-black, the
                // bright stroke stays high-contrast for low-vision players.
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = neon.Core,
                    ShadowDepth = 0,      // 0 depth = even halo on all sides
                    BlurRadius = 12,
                    Opacity = 0.9,
                },
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                ToolTip = def.Label,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent,
                // Scale from the center so the zoom effect stays on the gem.
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.0, 1.0),
            };

            // Pulse the icon's neon bloom (design: icon-glow, 2.4s), staggered
            // per button so the ring shimmers rather than throbbing in unison.
            AnimateIconGlow((System.Windows.Media.Effects.DropShadowEffect)btn.Effect, i);

            // Hover + keyboard-focus zoom: the active gem grows ~25% so it's
            // unmistakable which one is about to be activated. Works for both
            // mouse users (MouseEnter) and keyboard-mode users (GotKeyboardFocus).
            btn.MouseEnter += (_, _) => AnimateButtonScale(btn, 1.25);
            btn.MouseLeave += (_, _) => AnimateButtonScale(btn, 1.0);
            btn.GotKeyboardFocus += (_, _) => AnimateButtonScale(btn, 1.25);
            btn.LostKeyboardFocus += (_, _) => AnimateButtonScale(btn, 1.0);
            // Screen-reader announcement: NVDA / JAWS / Narrator will read out
            // the button's purpose instead of just the emoji glyph.
            System.Windows.Automation.AutomationProperties.SetName(btn, def.Label);
            System.Windows.Automation.AutomationProperties.SetHelpText(btn,
                $"Open the {def.Label} panel.");
            // (Foreground is solid white with a dark halo — set above. The old
            // animated gradient brush was removed: it reduced icon contrast.)
            // Transparent rounded hit area — the gem in the artwork IS the
            // visual; the button is only a click target sitting on top of it.
            btn.Template = CreateRoundButtonTemplate();

            btn.Tag = def.Id;
            btn.Click += (_, _) => PanelRequested?.Invoke(this, def.Id);

            Canvas.SetLeft(btn, cx - ButtonSize / 2);
            Canvas.SetTop(btn, cy - ButtonSize / 2);
            ButtonsCanvas.Children.Add(btn);
        }
        // (Icon shimmer animation removed — icons are now solid white for contrast.)
    }

    /// <summary>
    /// Smoothly scale a gem button (hover / keyboard focus). 120 ms with a
    /// gentle ease-out — fast enough to feel instant, slow enough not to pop.
    /// </summary>
    private static void AnimateButtonScale(Button btn, double target)
    {
        if (btn.RenderTransform is not ScaleTransform st) return;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>
    /// Breathe an icon's neon bloom (design keyframe: icon-glow, 2.4s). Each
    /// button is phase-staggered via <paramref name="index"/> so the ring
    /// shimmers organically instead of pulsing in unison.
    /// </summary>
    private static void AnimateIconGlow(System.Windows.Media.Effects.DropShadowEffect fx, int index)
    {
        var anim = new DoubleAnimation
        {
            From = 8, To = 20,
            Duration = TimeSpan.FromSeconds(1.2 + (index % 4) * 0.15),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(index * 90),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, anim);
    }

    /// <summary>
    /// Draw one button orb into GemsCanvas at (cx, cy), in the design's layer
    /// order: outer neon glow (ringA) → dark frame with neon border → hollow
    /// centre → node gem at the top. The interactive Button is added separately
    /// at the same centre, so the artwork and the hit target can never drift.
    /// </summary>
    private void DrawGem(double cx, double cy, NeonColor neon)
    {
        double r = ButtonSize / 2;

        // ringA — the soft coloured bloom around the orb (design: inset -12px,
        // blur 12). A zero-depth drop shadow on the frame reproduces this more
        // cheaply than a second blurred ellipse, which matters on an overlay
        // composited over a running game.
        var frame = new System.Windows.Shapes.Ellipse
        {
            Width = ButtonSize,
            Height = ButtonSize,
            IsHitTestVisible = false,
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(WithAlpha(neon.Core, 0.55)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = neon.Core, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.45,
            },
        };
        var frameBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0), EndPoint = new Point(0.85, 1),
        };
        frameBrush.GradientStops.Add(new GradientStop(Hex("#FF2A0F40"), 0.0));
        frameBrush.GradientStops.Add(new GradientStop(Hex("#FF090119"), 0.45));
        frameBrush.GradientStops.Add(new GradientStop(Hex("#FF010004"), 0.78));
        frameBrush.GradientStops.Add(new GradientStop(Hex("#FF000001"), 1.0));
        frame.Fill = frameBrush;
        Canvas.SetLeft(frame, cx - r);
        Canvas.SetTop(frame, cy - r);
        GemsCanvas.Children.Add(frame);

        // hollow — 62% of the frame, near-black violet so the icon reads.
        double hr = ButtonSize * 0.62 / 2;
        var hollow = new System.Windows.Shapes.Ellipse
        {
            Width = hr * 2, Height = hr * 2, IsHitTestVisible = false,
        };
        var hollowBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.4), Center = new Point(0.5, 0.5),
            RadiusX = 0.5, RadiusY = 0.5,
        };
        hollowBrush.GradientStops.Add(new GradientStop(Hex("#FF1E0B2F"), 0.0));
        hollowBrush.GradientStops.Add(new GradientStop(Hex("#FF050011"), 0.45));
        hollowBrush.GradientStops.Add(new GradientStop(Hex("#FF000001"), 1.0));
        hollow.Fill = hollowBrush;
        Canvas.SetLeft(hollow, cx - hr);
        Canvas.SetTop(hollow, cy - hr);
        GemsCanvas.Children.Add(hollow);

        // node gem — a small bright diamond pinned at the top of the orb.
        GemsCanvas.Children.Add(MakeNode(cx, cy - r, 10, neon, breatheDelayMs: 0));
    }

    /// <summary>
    /// A rotated square "data node" — the design's recurring gem motif, used on
    /// the button tops and around the outer spoke ring.
    /// </summary>
    private static System.Windows.Shapes.Rectangle MakeNode(
        double cx, double cy, double size, NeonColor neon, int breatheDelayMs)
    {
        var node = new System.Windows.Shapes.Rectangle
        {
            Width = size, Height = size,
            RadiusX = size * 0.22, RadiusY = size * 0.22,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(45),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = neon.Core, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.95,
            },
        };
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
        };
        g.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        g.GradientStops.Add(new GradientStop(neon.Bright, 0.34));
        g.GradientStops.Add(new GradientStop(neon.Deep, 1.0));
        node.Fill = g;
        Canvas.SetLeft(node, cx - size / 2);
        Canvas.SetTop(node, cy - size / 2);

        // bracket-breathe, 3s
        node.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.55, To = 1.0,
            Duration = TimeSpan.FromSeconds(3),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(breatheDelayMs),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        return node;
    }

    private ControlTemplate CreateRoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(ButtonSize / 2));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        template.VisualTree = border;
        return template;
    }

    // ── The design's neon palette ────────────────────────────────────────────
    // Twelve hues cycling hot-pink → electric-blue around the ring, exactly as
    // the design's neon(i) helper produces. Values are sRGB conversions of the
    // design's OKLCH triples: core = oklch(0.74 0.27 h), bright = (0.85 0.24 h),
    // deep = (0.5 0.28 h).
    internal readonly record struct NeonColor(Color Core, Color Bright, Color Deep);

    private static readonly (string Core, string Bright, string Deep)[] NeonHex =
    {
        ("#FFFF42C2", "#FFFF7EE4", "#FFC50079"), // h 350
        ("#FF409CFF", "#FF72C6FF", "#FF0043FE"), // h 262
        ("#FFFF4FEC", "#FFFF86FF", "#FFB6009F"), // h 335
        ("#FF00A4FF", "#FF4ACDFF", "#FF004DFB"), // h 255
        ("#FFFF46D1", "#FFFF80F1", "#FFC00086"), // h 345
        ("#FF6495FF", "#FF8BC0FF", "#FF2A39FF"), // h 268
        ("#FFFF3EB3", "#FFFF7CD6", "#FFC9006B"), // h 355
        ("#FF00AAFF", "#FF04D2FF", "#FF0054F8"), // h 250
        ("#FFFF54F9", "#FFFF89FF", "#FFB000AB"), // h 330
        ("#FF0DA1FF", "#FF5DCAFF", "#FF0049FD"), // h 258
        ("#FFFF44C8", "#FFFF7FE9", "#FFC3007E"), // h 348
        ("#FF5499FF", "#FF7FC3FF", "#FF133EFF"), // h 265
    };

    private static NeonColor Neon(int i)
    {
        var (c, b, d) = NeonHex[((i % NeonHex.Length) + NeonHex.Length) % NeonHex.Length];
        return new NeonColor(Hex(c), Hex(b), Hex(d));
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    private static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    // ── Procedural ring layers ───────────────────────────────────────────────

    /// <summary>
    /// 44 orbiting code glyphs at r = 217 (design: runeCount 44, runeR 344 on
    /// the 760 canvas). Each flickers on its own schedule — the design's
    /// glyph-flicker keyframe, which uses steps(3) for a hard digital stutter.
    /// </summary>
    private void BuildGlyphs()
    {
        const string glyphChars = "01ｱｲｳｴｵｶｷｸ日ﾊﾋﾌﾍﾎABCDEF10110<>{}[]#$%";
        const int count = 44;
        const double glyphR = 217;
        var centre = BaseHudSize / 2;

        GlyphsCanvas.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            var ang = (i / (double)count) * 360.0;
            var rad = (ang - 90) * Math.PI / 180.0;
            var neon = Neon(i);

            var tb = new TextBlock
            {
                Text = glyphChars[(i * 3) % glyphChars.Length].ToString(),
                FontFamily = new System.Windows.Media.FontFamily("Share Tech Mono, Consolas, Courier New"),
                FontSize = 16,
                Foreground = new SolidColorBrush(neon.Core),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = neon.Core, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9,
                },
            };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = tb.DesiredSize.Width;
            var h = tb.DesiredSize.Height;

            Canvas.SetLeft(tb, centre + glyphR * Math.Cos(rad) - w / 2);
            Canvas.SetTop(tb, centre + glyphR * Math.Sin(rad) - h / 2);

            tb.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.35, To = 1.0,
                Duration = TimeSpan.FromSeconds(2.4 + (i % 5) * 0.5),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 90),
            });
            GlyphsCanvas.Children.Add(tb);
        }
    }

    /// <summary>12 outer spoke data-nodes at r = 231 (design: gemR 366).</summary>
    private void BuildDataNodes()
    {
        const double nodeR = 231;
        var centre = BaseHudSize / 2;

        NodesCanvas.Children.Clear();
        for (int i = 0; i < 12; i++)
        {
            var rad = (-90 + i * 30) * Math.PI / 180.0;
            NodesCanvas.Children.Add(MakeNode(
                centre + nodeR * Math.Cos(rad),
                centre + nodeR * Math.Sin(rad),
                12, Neon(i), breatheDelayMs: i * 240));
        }
    }

    /// <summary>
    /// 24 rising data blips (design: ember-float, translateY -240px over 4–10s).
    /// Scaled to the 480 canvas, so they rise ~152px before fading out.
    /// </summary>
    private void BuildBlips()
    {
        BlipsCanvas.Children.Clear();
        for (int i = 0; i < 24; i++)
        {
            var neon = Neon(i);
            var size = 2 + i % 3;
            var dur = TimeSpan.FromSeconds(4 + (i % 7));

            var blip = new System.Windows.Shapes.Rectangle
            {
                Width = size, Height = size,
                RadiusX = 1, RadiusY = 1,
                Fill = new SolidColorBrush(neon.Bright),
                IsHitTestVisible = false,
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = neon.Core, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.95,
                },
            };
            Canvas.SetLeft(blip, BaseHudSize * (0.06 + (i * 41 % 88) / 100.0 * 0.88));
            Canvas.SetTop(blip, BaseHudSize * (0.70 + (i % 25) / 100.0));

            var begin = TimeSpan.FromMilliseconds(i * 300);
            ((TranslateTransform)blip.RenderTransform).BeginAnimation(
                TranslateTransform.YProperty, new DoubleAnimation
                {
                    From = 0, To = -152,
                    Duration = dur,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = begin,
                });

            // Fade in quickly, hold, then fade out at the top of the rise.
            var fade = new DoubleAnimationUsingKeyFrames
            {
                Duration = dur,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin,
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.12)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.88)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
            blip.BeginAnimation(OpacityProperty, fade);

            BlipsCanvas.Children.Add(blip);
        }
    }

    /// <summary>
    /// Start every ambient loop: the two counter-rotating outer sweeps, the
    /// medallion scan arc, both matrix-rain streams, the void rain, the
    /// scanline sweep and the outer glow's neon pulse.
    ///
    /// Deliberately modest: this composites over a running game, so the budget
    /// is a handful of GPU-cheap transform and opacity animations rather than
    /// the design's full stack of blurred, blended conic layers.
    /// </summary>
    private void StartSwirlAnimation()
    {
        try
        {
            static void Spin(RotateTransform t, double seconds, bool reverse = false) =>
                t.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
                {
                    From = reverse ? 360 : 0,
                    To = reverse ? 0 : 360,
                    Duration = TimeSpan.FromSeconds(seconds),
                    RepeatBehavior = RepeatBehavior.Forever,
                });

            Spin(SweepARotate, 22);
            Spin(SweepBRotate, 17, reverse: true);
            Spin(ScanArcRotate, 8);

            // Matrix rain: scroll each tiled brush by exactly one tile height so
            // the loop is seamless.
            static void Rain(TranslateTransform t, double tileHeight, double seconds) =>
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
                {
                    From = 0, To = tileHeight,
                    Duration = TimeSpan.FromSeconds(seconds),
                    RepeatBehavior = RepeatBehavior.Forever,
                });

            Rain(RainPinkShift, 34, 4.0);
            Rain(RainBlueShift, 44, 6.5);
            Rain(VoidRainShift, 40, 4.5);

            // Scanline sweep down the centre void (design: scanline, 4s).
            ScanlineShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = -49, To = 189,
                Duration = TimeSpan.FromSeconds(4),
                RepeatBehavior = RepeatBehavior.Forever,
            });

            // neon-pulse on the outer glow (opacity 0.6 → 1, 5s).
            GlowPink.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0.6, To = 1.0,
                Duration = TimeSpan.FromSeconds(5),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        }
        catch (Exception ex)
        {
            // Decorative only — the HUD must still work if a layer is missing.
            CrashLogger.Log("RadialHud.StartSwirlAnimation", ex);
        }
    }

    // ── Opacity / visibility ─────────────────────────────────────────────────
    private void SetOpacity(double target)
    {
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new QuadraticEase(),
        };
        RingContainer.BeginAnimation(OpacityProperty, anim);
    }

    // ── Minimise to logo ─────────────────────────────────────────────────────
    // Collapsed, only the centre medallion shows — a small logo instead of a
    // 480px ring. Because the shrink is a RenderTransform on RingContainer, the
    // hit area shrinks with it automatically (PointFromScreen walks the whole
    // transform chain), so the logo is exactly as clickable as it looks.
    private const double MinimisedScale = 0.38;
    private bool _minimised;

    private void SetMinimised(bool on, bool animate = true)
    {
        if (_minimised == on) return;
        _minimised = on;

        var target = on ? MinimisedScale : 1.0;
        var dur = TimeSpan.FromMilliseconds(animate ? 220 : 0);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        MinimiseScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, dur) { EasingFunction = ease });
        MinimiseScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, dur) { EasingFunction = ease });

        var layerOpacity = on ? 0.0 : 1.0;
        WheelLayers.BeginAnimation(OpacityProperty, new DoubleAnimation(layerOpacity, dur));
        InteractiveLayers.BeginAnimation(OpacityProperty, new DoubleAnimation(layerOpacity, dur));
        // Gems must not be clickable while they're invisible.
        InteractiveLayers.IsHitTestVisible = !on;
    }

    /// <summary>Apply the minimise setting immediately (used on load and on change).</summary>
    private void ApplyMinimiseSetting()
    {
        if (!App.Settings.Current.MinimiseHud) SetMinimised(false);
        else if (!_wasCursorOver && !_isDragging && !_justDragged) SetMinimised(true, animate: false);
    }

    private void OnCursorEnteredHud()
    {
        SetMinimised(false);   // unfold the wheel as the pointer arrives
        SetOpacity(ActiveOpacity);
        AnimateDragHint(1);
        // Soft fade-in cue as the pointer arrives on the wheel — a gentle audio
        // "you're here" that replaces the old repeated spoken "Radial HUD…"
        // announcement (which the hover reader now deliberately skips).
        _earcons?.HoverEnter();
        // Optional spoken cue (off by default; Settings → HUD tab).
        if (App.Settings.Current.AnnounceHudActivation)
            _tts?.SpeakAsync("HUD active.");
        MouseCaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCursorLeftHud()
    {
        if (_isDragging || _justDragged) return;
        SetOpacity(IdleOpacity);
        AnimateDragHint(0);
        _earcons?.HoverLeave(); // soft fade-out cue as the pointer leaves the wheel
        if (App.Settings.Current.MinimiseHud) SetMinimised(true);
        MouseCaptureReleased?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateDragHint(double target)
    {
        // Keep the hint pinned just below wherever the ring currently sits.
        if (target > 0)
        {
            Canvas.SetLeft(DragHint, Canvas.GetLeft(RingContainer) + HudSize / 2 - 115);
            Canvas.SetTop(DragHint, Canvas.GetTop(RingContainer) + HudSize + 6);
        }
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(250),
        };
        DragHint.BeginAnimation(OpacityProperty, anim);
    }

    // ── Dragging ─────────────────────────────────────────────────────────────
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click also initiates drag
        if (e.ChangedButton == MouseButton.Middle)
            StartDrag(e.GetPosition(Window.GetWindow(this)));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Shift + LeftClick initiates drag. Normal clicks pass through to buttons.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            StartDrag(e.GetPosition(Window.GetWindow(this)));
            e.Handled = true;
        }
    }

    private void StartDrag(Point mouseWindowPos)
    {
        _isDragging = true;
        _dragStart = mouseWindowPos;
        _hudStartPos = new Point(Canvas.GetLeft(RingContainer), Canvas.GetTop(RingContainer));
        Mouse.Capture(RingContainer);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var cur = e.GetPosition(Window.GetWindow(this));
        var dx = cur.X - _dragStart.X;
        var dy = cur.Y - _dragStart.Y;
        var newLeft = _hudStartPos.X + dx;
        var newTop  = _hudStartPos.Y + dy;
        var clamped = ClampToScreen(newLeft, newTop);
        Canvas.SetLeft(RingContainer, clamped.X);
        Canvas.SetTop(RingContainer, clamped.Y);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        Mouse.Capture(null);

        // Save new position
        App.Settings.Current.HudPosition.X = Canvas.GetLeft(RingContainer);
        App.Settings.Current.HudPosition.Y = Canvas.GetTop(RingContainer);
        App.Settings.NotifyChanged();

        // Start the 3-second justDragged grace period so the user sees where it landed.
        StartJustDraggedTimer();
    }

    private void StartJustDraggedTimer()
    {
        _justDragged = true;
        SetOpacity(ActiveOpacity);
        _justDraggedTimer?.Stop();
        _justDraggedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _justDraggedTimer.Tick += (_, _) =>
        {
            _justDraggedTimer?.Stop();
            _justDragged = false;
            if (!IsMouseOver) SetOpacity(IdleOpacity);
        };
        _justDraggedTimer.Start();
    }

    // ── Position management ──────────────────────────────────────────────────
    /// <summary>
    /// Clamp a proposed Canvas.Left/Canvas.Top position so the HUD ring's
    /// BOUNDING BOX stays fully on-screen. Because RingContainer is positioned
    /// by its top-left corner (not its center), we clamp the top-left to
    /// [0, screenWidth - HudSize] x [0, screenHeight - HudSize].
    /// </summary>
    private Point ClampToScreen(double left, double top)
    {
        var win = Window.GetWindow(this);
        if (win == null) return new Point(left, top);

        // The window can still measure 0 when this runs during load. Clamping
        // against 0 would force the wheel to the top-left and destroy a saved
        // position, so fall back to the primary screen size until layout is real.
        var w = win.ActualWidth  > 0 ? win.ActualWidth  : SystemParameters.PrimaryScreenWidth;
        var h = win.ActualHeight > 0 ? win.ActualHeight : SystemParameters.PrimaryScreenHeight;

        var maxLeft = Math.Max(0, w - HudSize);
        var maxTop  = Math.Max(0, h - HudSize);
        return new Point(
            Math.Clamp(left, 0, maxLeft),
            Math.Clamp(top,  0, maxTop));
    }

    /// <summary>Restore position from settings, or center if invalid.</summary>
    private void RestoreSavedPosition()
    {
        var saved = App.Settings.Current.HudPosition;
        Point pos;
        if (saved.X < 0 || saved.Y < 0)
        {
            pos = GetCenterPosition();
        }
        else
        {
            // Just clamp the saved spot onto the screen and keep it. The old code
            // re-centred whenever clamping moved the position at all, which threw
            // the user's placement away on every launch where layout wasn't ready
            // yet. Only a position that lands completely off-screen is discarded.
            pos = ClampToScreen(saved.X, saved.Y);
        }
        Canvas.SetLeft(RingContainer, pos.X);
        Canvas.SetTop(RingContainer, pos.Y);
    }

    private Point GetCenterPosition()
    {
        var win = Window.GetWindow(this);
        if (win == null) return new Point(100, 100);
        return new Point(
            (win.ActualWidth  - HudSize) / 2,
            (win.ActualHeight - HudSize) / 2);
    }

    /// <summary>
    /// Move the wheel by a step with the keyboard (Ctrl+Shift+arrows). Dragging
    /// a wheel with a mouse is a poor ask for someone who can't see it, so this
    /// gives an aim-free way to reposition it. Each step plays a soft tick so
    /// the move is audible, and the new spot is saved like a drag would be.
    /// </summary>
    public void NudgePosition(double dx, double dy)
    {
        const double step = 40;
        var left = Canvas.GetLeft(RingContainer) + dx * step;
        var top  = Canvas.GetTop(RingContainer)  + dy * step;

        var clamped = ClampToScreen(left, top);
        Canvas.SetLeft(RingContainer, clamped.X);
        Canvas.SetTop(RingContainer, clamped.Y);

        App.Settings.Current.HudPosition.X = clamped.X;
        App.Settings.Current.HudPosition.Y = clamped.Y;
        App.Settings.NotifyChanged();

        SetOpacity(ActiveOpacity);
        RestartNavDimTimer();     // brighten while moving, dim again after a pause

        // Stay SILENT while the wheel is being moved — a tone on every keypress
        // was relentless. Instead a single soft cue plays once movement settles,
        // so the user hears "it's placed" rather than a machine-gun of ticks.
        _placedTimer?.Stop();
        _placedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _placedTimer.Tick -= OnPlacedSettled;
        _placedTimer.Tick += OnPlacedSettled;
        _placedTimer.Start();
    }

    private DispatcherTimer? _placedTimer;

    private void OnPlacedSettled(object? sender, EventArgs e)
    {
        _placedTimer?.Stop();
        _earcons?.HudPlaced();
    }

    /// <summary>Public API for the Ctrl+Shift+H rescue hotkey.</summary>
    public void ResetPosition()
    {
        var center = GetCenterPosition();
        Canvas.SetLeft(RingContainer, center.X);
        Canvas.SetTop(RingContainer, center.Y);
        App.Settings.Current.HudPosition.X = center.X;
        App.Settings.Current.HudPosition.Y = center.Y;
        App.Settings.NotifyChanged();
        StartJustDraggedTimer(); // make it visible so user sees the rescue worked
    }

    /// <summary>Re-clamp current position against new window size (monitor change).</summary>
    public void ReclampToScreen()
    {
        var left = Canvas.GetLeft(RingContainer);
        var top  = Canvas.GetTop(RingContainer);
        var clamped = ClampToScreen(left, top);
        Canvas.SetLeft(RingContainer, clamped.X);
        Canvas.SetTop(RingContainer, clamped.Y);
    }
}
