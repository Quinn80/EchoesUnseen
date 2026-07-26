using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;
using EchoesUnseen.Views.Panels;

namespace EchoesUnseen.Views;

/// <summary>
/// Modal host that wraps every panel in a dimmed backdrop with a pink-bordered card.
///
/// Handles:
///   - Click-outside dismiss (Root_MouseLeftButtonDown)
///   - Close button (top-right X)
///   - Escape key (KeyDown on the UserControl)
///   - Panel instantiation by ID
///
/// Panels receive their services (MumbleLink, TTS, hotkeys) via the
/// <see cref="IPanel.AttachServices"/> interface, which every panel
/// UserControl implements.
/// </summary>
public partial class PanelHost : UserControl
{
    public string PanelId { get; private set; } = "";
    public event EventHandler? CloseRequested;

    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    public PanelHost()
    {
        InitializeComponent();

        // Handle Escape to close — use KeyDown on the UserControl itself
        // AND register a top-level handler since the HUD global hotkey for
        // Escape is Stop-Speaking, which is different.
        KeyDown += PanelHost_KeyDown;
        Focusable = true;
        Loaded += (_, _) =>
        {
            Focus();
            StartGlowRotation();
            SpawnEmbers();
            ApplyRememberedLayout(); // v21.3: open where the user last put a panel
        };
    }

    /// <summary>Slowly rotate the fire-glow behind the card (matches the HUD's
    /// rotating aura in the Claude Design build). 14s per turn, forever.</summary>
    private void StartGlowRotation()
    {
        var spin = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0, To = 360,
            Duration = TimeSpan.FromSeconds(14),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        PanelGlowRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    /// <summary>
    /// v21.2: floating fire-and-ice embers inside the panel card, matching the
    /// new Claude Design. Fourteen tiny glowing sparks (warm orange, every
    /// fourth one ice-blue) rise from the bottom edge, drift sideways, and
    /// fade out — then loop forever with staggered starts. Purely decorative,
    /// non-interactive, and wrapped in try/catch so it can never break a panel.
    /// </summary>
    private void SpawnEmbers()
    {
        try
        {
            if (EmberLayer.Children.Count > 0) return; // already spawned

            var rng = new Random(7); // fixed seed = same pleasing layout every open
            for (int i = 0; i < 14; i++)
            {
                bool warm = i % 4 != 0;
                double size = 2 + (i % 3);

                var ember = new System.Windows.Shapes.Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                            .ConvertFromString(warm ? "#FFFF711C" : "#FF00E4FF")),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                            .ConvertFromString(warm ? "#FFFF711C" : "#FF00E4FF"),
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Opacity = 0.95,
                    },
                    Opacity = 0,
                    RenderTransform = new System.Windows.Media.TranslateTransform(),
                };

                // Horizontal spot: pseudo-even spread like the design's (5 + i*39 % 90)%
                double xFrac = (5 + i * 39 % 90) / 100.0;
                EmberLayer.Children.Add(ember);

                // Position now and on every resize (canvas has no % positioning)
                void Place()
                {
                    System.Windows.Controls.Canvas.SetLeft(ember, EmberLayer.ActualWidth * xFrac);
                    System.Windows.Controls.Canvas.SetTop(ember, EmberLayer.ActualHeight + 4);
                }
                Place();
                EmberLayer.SizeChanged += (_, _) => Place();

                double seconds = 4 + i % 5;
                var delay = TimeSpan.FromSeconds(i * 0.34);
                var tt = (System.Windows.Media.TranslateTransform)ember.RenderTransform;

                // Rise: 0 → -(220..260)px, ease-in, forever
                var rise = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = -(220 + rng.Next(40)),
                    Duration = TimeSpan.FromSeconds(seconds),
                    BeginTime = delay,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn },
                };
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, rise);

                // Sideways drift, alternating direction like the design
                var driftX = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = i % 2 == 0 ? 24 : -30,
                    Duration = TimeSpan.FromSeconds(seconds),
                    BeginTime = delay,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                };
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, driftX);

                // Fade in fast, glow, fade out at the top (0 → 1 → 0.9 → 0)
                var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
                {
                    Duration = TimeSpan.FromSeconds(seconds),
                    BeginTime = delay,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                };
                fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0,
                    System.Windows.Media.Animation.KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1,
                    System.Windows.Media.Animation.KeyTime.FromPercent(0.12)));
                fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.9,
                    System.Windows.Media.Animation.KeyTime.FromPercent(0.88)));
                fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0,
                    System.Windows.Media.Animation.KeyTime.FromPercent(1)));
                ember.BeginAnimation(OpacityProperty, fade);
            }
        }
        catch (Exception ex)
        {
            // Decoration must never take down a panel.
            Services.CrashLogger.Log("PanelHost.SpawnEmbers", ex);
        }
    }

    /// <summary>Services are passed down to each panel when it's instantiated.</summary>
    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    /// <summary>
    /// Load the panel UserControl for the given ID and inject it into the card.
    /// Titles and content are set here so each panel doesn't have to rebuild its title bar.
    /// </summary>
    public void OpenPanel(string panelId)
    {
        PanelId = panelId;
        (string title, UserControl control) panel;
        try
        {
            panel = CreatePanel(panelId);
        }
        catch (Exception ex)
        {
            // v21.5: a panel that dies during construction must NEVER look
            // like a dead gem again. Log the culprit and show an error card.
            Services.CrashLogger.Log($"PanelHost.CreatePanel[{panelId}]", ex);
            panel = ("Panel Error", new PlaceholderPanel("Panel Error",
                $"This panel hit an error while opening:\n{ex.GetType().Name}: {ex.Message}\n\n" +
                "Details were written to crash.log (path shown in Settings → About)."));
        }
        var (title, control) = panel;
        TitleText.Text = title;

        // ── Screen-reader identity ──────────────────────────────────────────
        // MainWindow focuses this UserControl as soon as it loads. Without an
        // automation name there is nothing for UI Automation to report, so
        // NVDA announced the focused element as "unknown". Naming both the
        // host and the injected panel gives the whole card an identity, and
        // the help text tells the user how to get out again.
        System.Windows.Automation.AutomationProperties.SetName(this, $"{title} panel");
        System.Windows.Automation.AutomationProperties.SetHelpText(this,
            "Press Escape to close this panel. Use Tab and Shift+Tab to move between controls.");
        System.Windows.Automation.AutomationProperties.SetName(control, $"{title} panel content");

        // Fill the header icon badge with the same fire-glow vector icon used
        // on the HUD gem for this panel, so the panel visually matches the
        // button that opened it.
        try
        {
            var fire = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 1),
                EndPoint = new System.Windows.Point(0.5, 0),
            };
            fire.GradientStops.Add(new System.Windows.Media.GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFE01B00"), 0.0));
            fire.GradientStops.Add(new System.Windows.Media.GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFF6A00"), 0.5));
            fire.GradientStops.Add(new System.Windows.Media.GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFC030"), 0.85));
            fire.GradientStops.Add(new System.Windows.Media.GradientStop(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFF0B0"), 1.0));
            IconBadge.Content = HudIcons.Build(panelId, 30, fire);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("PanelHost icon badge", ex);
        }

        // Give the panel its services
        if (control is IPanel p)
            p.AttachServices(_mumble, _tts, _hotkeys, _gw2Api);

        // The Welcome card's "Got it" button closes the whole panel.
        if (control is WelcomePanel welcome)
            welcome.Dismissed += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        PanelContent.Content = control;
    }

    /// <summary>
    /// Map panel ID → (title, UserControl instance).
    /// For the MVP, any panel that hasn't been built yet returns a
    /// placeholder UserControl with a "coming soon" message.
    /// </summary>
    private static (string Title, UserControl Control) CreatePanel(string id) => id switch
    {
        "screen-reader" => ("Screen Reader",   new ScreenReaderPanel()),
        "heart-quest"   => ("Heart Quests",    new HeartQuestPanel()),
        "trail-nav"     => ("Trail Navigator", new TrailNavigatorPanel()),
        "chat-reader"   => ("Chat Reader",     new ChatReaderPanel()),
        "voice-chat"    => ("Voice to Chat",   new VoiceToChatPanel()),
        "music"         => ("Music Player",    new MusicPlayerPanel()),
        "assistant"     => ("Oracle", new VoiceAssistantPanel()),
        "welcome"       => ("Welcome", new WelcomePanel()),
        "account"       => ("Account Search",  new AccountSearchPanel()),
        "trading"       => ("Trading Post",    new TradingPostPanel()),
        "build"         => ("Build & Gear",    new BuildGearPanel()),
        "map"           => ("Map Completion",  new MapCompletionPanel()),
        "settings"      => ("Settings",        new SettingsPanel()),
        _               => ($"Unknown: {id}",  new PlaceholderPanel("Unknown", $"No panel registered for id '{id}'.")),
    };

    // ── Dismiss handling ─────────────────────────────────────────────────────

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Click on the backdrop (not the card) dismisses.
        if (e.OriginalSource == sender)
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks inside the card should NOT bubble up to the root (otherwise
        // any click inside the card would dismiss the panel). Mark as handled.
        e.Handled = true;
    }

    // ── v21.3: movable + resizable panels ────────────────────────────────────
    // The user can drag the top bar (or the header) to move the panel, and
    // drag the bottom-right grip to resize it. The chosen position and size
    // are remembered for the rest of the session (every panel opens where the
    // user last put one). Double-clicking the drag bar resets both.
    // These are mouse conveniences only — keyboard/screen-reader users get
    // the same centered, fully accessible panel as always.
    private static double _sessMoveX, _sessMoveY;
    private static double _sessW = double.NaN, _sessH = double.NaN;
    private static bool _sessPlaced;      // true once the user drags a panel
    private double _defaultOffsetX;       // beside-the-wheel placement (v21.5)
    private double _defaultOffsetY;       // above/below fallback when no side fits

    private bool _movingCard;
    private System.Windows.Point _moveStart;
    private double _moveOrigX, _moveOrigY;

    private bool _sizingCard;
    private System.Windows.Point _sizeStart;
    private double _sizeOrigW, _sizeOrigH;

    /// <summary>
    /// v21.5: choose which side of the wheel the panel should open on.
    /// Called by MainWindow before the panel is shown. Picks the side with
    /// more free space; if neither side has room (small screens / huge HUD
    /// scale), stays centered — the wheel will overlap and win, but the
    /// panel remains draggable out from under it.
    /// </summary>
    /// <summary>
    /// Stop whatever the hosted panel is running. Called on app shutdown, because
    /// background panels intentionally no longer tear themselves down when their
    /// window closes — otherwise sonar or music could outlive the app.
    /// </summary>
    public void ShutdownPanel()
    {
        try
        {
            if (PanelContent.Content is IBackgroundPanel bg) bg.StopBackgroundWork();
            if (PanelContent.Content is IDisposable d) d.Dispose();
            PanelContent.Content = null;
        }
        catch (Exception ex) { CrashLogger.Log("PanelHost.ShutdownPanel", ex); }
    }

    public void SetDefaultPlacement(System.Windows.Rect wheelRect, System.Windows.Size windowSize)
    {
        try
        {
            const double panelWidth = 620;   // card's MaxWidth default
            const double panelHeight = 460;  // typical card height
            const double margin = 24;

            _defaultOffsetX = 0;
            _defaultOffsetY = 0;

            double freeLeft   = wheelRect.Left;
            double freeRight  = windowSize.Width  - wheelRect.Right;
            double freeTop    = wheelRect.Top;
            double freeBottom = windowSize.Height - wheelRect.Bottom;

            // Prefer whichever SIDE has room — the wheel can be dragged anywhere,
            // so the panel has to follow it and open into open space rather than
            // sitting under it.
            if (Math.Max(freeLeft, freeRight) >= panelWidth + margin * 2)
            {
                double targetCenterX = freeRight >= freeLeft
                    ? wheelRect.Right + freeRight / 2
                    : freeLeft / 2;
                _defaultOffsetX = targetCenterX - windowSize.Width / 2;
                return;
            }

            // No side fits (wheel near the middle of a narrow screen) — fall back
            // to above or below, whichever is roomier.
            if (Math.Max(freeTop, freeBottom) >= panelHeight * 0.6)
            {
                double targetCenterY = freeBottom >= freeTop
                    ? wheelRect.Bottom + freeBottom / 2
                    : freeTop / 2;
                _defaultOffsetY = targetCenterY - windowSize.Height / 2;
            }
            // else: nowhere clear — leave centred, which is still fully usable.
        }
        catch { _defaultOffsetX = 0; _defaultOffsetY = 0; }
    }

    /// <summary>Re-apply the session's remembered position/size (clamped).</summary>
    private void ApplyRememberedLayout()
    {
        if (!double.IsNaN(_sessW))
        {
            CardWrapper.MaxWidth = double.PositiveInfinity;
            CardWrapper.MaxHeight = double.PositiveInfinity;
            CardWrapper.Width = _sessW;
            CardWrapper.Height = _sessH;
        }
        if (_sessPlaced)
        {
            CardMove.X = _sessMoveX;
            CardMove.Y = _sessMoveY;
        }
        else
        {
            // No user-chosen spot yet → open beside the wheel (or above/below it
            // when neither side has room), never underneath it.
            CardMove.X = _defaultOffsetX;
            CardMove.Y = _defaultOffsetY;
        }
        ClampCardOnScreen();
    }

    /// <summary>Keep at least a healthy chunk of the card reachable on screen.</summary>
    private void ClampCardOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double w = CardWrapper.ActualWidth > 0 ? CardWrapper.ActualWidth : 620;
        double h = CardWrapper.ActualHeight > 0 ? CardWrapper.ActualHeight : 700;
        double maxX = Math.Max(0, (ActualWidth + w) / 2 - 120);
        double maxY = Math.Max(0, (ActualHeight + h) / 2 - 120);
        CardMove.X = Math.Clamp(CardMove.X, -maxX, maxX);
        CardMove.Y = Math.Clamp(CardMove.Y, -maxY, maxY);
    }

    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click the bar → reset position AND size to defaults.
        if (e.ClickCount == 2)
        {
            CardMove.X = CardMove.Y = 0;
            CardWrapper.Width = double.NaN;   // back to size-to-content
            CardWrapper.Height = double.NaN;
            CardWrapper.MaxWidth = 620;
            CardWrapper.MaxHeight = 700;
            _sessMoveX = _sessMoveY = 0;
            _sessW = _sessH = double.NaN;
            _sessPlaced = false;               // back to auto-placement
            e.Handled = true;
            return;
        }

        _movingCard = true;
        _moveStart = e.GetPosition(this);      // 'this' never moves — safe frame
        _moveOrigX = CardMove.X;
        _moveOrigY = CardMove.Y;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true; // don't let the card treat this as a content click
    }

    private void DragGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_movingCard) return;
        var p = e.GetPosition(this);
        CardMove.X = _moveOrigX + (p.X - _moveStart.X);
        CardMove.Y = _moveOrigY + (p.Y - _moveStart.Y);
        ClampCardOnScreen();
    }

    private void DragGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_movingCard) return;
        _movingCard = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _sessMoveX = CardMove.X;               // remember for this session
        _sessMoveY = CardMove.Y;
        _sessPlaced = true;                    // user's spot now wins (v21.5)
        e.Handled = true;
    }

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _sizingCard = true;
        _sizeStart = e.GetPosition(this);
        _sizeOrigW = CardWrapper.ActualWidth;
        _sizeOrigH = CardWrapper.ActualHeight;
        CardWrapper.MaxWidth = double.PositiveInfinity;  // let the user go bigger
        CardWrapper.MaxHeight = double.PositiveInfinity; // than the old defaults
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_sizingCard) return;
        var p = e.GetPosition(this);
        // Floor keeps every panel usable; ceiling keeps it on screen.
        CardWrapper.Width = Math.Clamp(_sizeOrigW + (p.X - _sizeStart.X),
                                        380, Math.Max(420, ActualWidth - 30));
        CardWrapper.Height = Math.Clamp(_sizeOrigH + (p.Y - _sizeStart.Y),
                                        320, Math.Max(360, ActualHeight - 30));
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_sizingCard) return;
        _sizingCard = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _sessW = CardWrapper.Width;            // remember for this session
        _sessH = CardWrapper.Height;
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PanelHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Contract for every panel UserControl so services can be injected.</summary>
public interface IPanel
{
    void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api);
}

/// <summary>
/// Implemented by panels whose work continues after their window is closed —
/// Chat Reader's scanning, Trail Navigator's sonar, Music Player's playback.
/// They no longer stop on Unloaded (closing a window shouldn't switch a feature
/// off), so this is how the app stops them cleanly on exit.
/// </summary>
public interface IBackgroundPanel
{
    void StopBackgroundWork();
}

// ── Placeholder panel used until each real panel is built ────────────────────
public class PlaceholderPanel : UserControl, IPanel
{
    public PlaceholderPanel(string title, string message)
    {
        var tb = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(16),
        };
        Content = tb;
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api) { }
}
