using System.Windows;
using System.Windows.Threading;
using EchoesUnseen.Services;

namespace EchoesUnseen;

/// <summary>
/// Application bootstrap.
///
/// Responsibilities:
///   1. Load persisted settings before the main window is created (so the HUD can
///      restore its saved position, theme, and user preferences immediately).
///   2. Install a process-wide exception handler that writes crashes to
///      %APPDATA%\EchoesUnseen\crash.log — without this, WPF silently swallows
///      background-thread exceptions and the window just vanishes.
///   3. Provide a static accessor for the settings service so every ViewModel
///      and service can read/write settings without a full DI container.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Global settings singleton. Created on startup, written on change.</summary>
    public static SettingsService Settings { get; private set; } = null!;

    /// <summary>True only in the DISTRIBUTION build (published with -p:Distribution=true).
    /// Gates end-user-facing bits like the "Send feedback" button, which the dev
    /// build hides.</summary>
#if DISTRIBUTION
    public const bool IsDistribution = true;
#else
    public const bool IsDistribution = false;
#endif

    /// <summary>
    /// When this executable was built.
    ///
    /// Two rounds of analysis have now been done against source that was not in the exe
    /// that produced the log - once because a copy failed silently, once because a
    /// desktop shortcut pointed at a Debug build from seven weeks earlier. Nothing in the
    /// log said so. One line does.
    /// </summary>
    internal static string BuildStamp()
    {
        try
        {
            var attr = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (attr.Length > 0)
            {
                var v = ((System.Reflection.AssemblyInformationalVersionAttribute)attr[0]).InformationalVersion;
                var i = v.IndexOf("build", StringComparison.Ordinal);
                if (i >= 0) return v[i..];
            }
        }
        catch { }
        return "build unknown";
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // SINGLE INSTANCE. The overlay registers global hotkeys and plays audio, so
        // two copies fight: you hear the greeting/sonar twice ("double voice"), and
        // whichever started first owns Ctrl+Shift+Q so the other can't be quit. When
        // a new copy launches (e.g. a fresh build), close any older ones first.
        try
        {
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }
        catch { /* best effort — never block startup on this */ }

        // Load settings BEFORE the window initializes so theme colors, HUD position,
        // and voice preferences are available when XAML binds.
        Settings = new SettingsService();
        Settings.Load();

        // Session header in the diagnostics log (when enabled) so a review has
        // context: version, chosen engines, and which features are on.
        var s = Settings.Current;
        // Bring the screen reader up in the background before it is needed.
        Services.Ocr.RapidOcrService.WarmUp();
        if (s.HoverTargetingFusion)
            _ = Task.Run(() => Services.Ocr.RapidOcrService.GetMatOcrAsync(CancellationToken.None));

        Services.DiagLog.Log("APP", $"=== session start · v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} {BuildStamp()} " +
            $"· hover-ocr={Services.Ocr.RapidOcrService.ModeDescription} chat-ocr={s.OcrEngine} voice={s.VoiceEngine} theme={s.ThemeId} " +
            $"hoverGame={s.HoverReadGame} hoverTargeting={(s.HoverTargetingFusion ? "opencv+rapidocr" : "classic")}{(s.HoverTargetingChosen ? "" : "(build default)")} " +
            $"combat={s.CombatAlertMode} wvw={s.WvwEnabled} ===");

        // Apply the user's saved theme, font size and high-contrast state
        // immediately so the main window renders with them on first paint
        // (no flash of default colors). v21: re-apply live whenever ANY
        // setting changes, so Settings-panel changes take effect instantly.
        Services.ThemeService.ApplyCurrent(Settings.Current);
        Settings.Changed += (_, s) =>
            Current?.Dispatcher.BeginInvoke(() => Services.ThemeService.ApplyCurrent(s));

        // Tooltips carry the wheel's labels and every hint in Settings, and WPF
        // hides them again after five seconds — too quick for anyone reading with
        // a magnifier or a screen reader. Thirty seconds, everywhere. This has to
        // be metadata rather than a style: the duration belongs to the control
        // that OWNS the tooltip, and no single implicit style reaches them all.
        try
        {
            System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(
                typeof(DependencyObject), new FrameworkPropertyMetadata(30000));
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject), new FrameworkPropertyMetadata(350));
        }
        catch (Exception ex) { CrashLogger.Log("Tooltip duration", ex); }

        // Hook crash logging so we never silently disappear.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Log("UI thread", e.Exception);
        // Mark as handled so the app doesn't die from a single bad click.
        // If it's a truly fatal error we'll crash again and OnAppDomainUnhandledException catches it.
        e.Handled = true;
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nSee crash.log for details.",
            "Echoes Unseen",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            CrashLogger.Log("background thread", ex);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Persist any pending settings before shutdown.
        Settings?.Save();
        base.OnExit(e);
    }
}
