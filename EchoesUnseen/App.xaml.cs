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

    protected override void OnStartup(StartupEventArgs e)
    {
        // Load settings BEFORE the window initializes so theme colors, HUD position,
        // and voice preferences are available when XAML binds.
        Settings = new SettingsService();
        Settings.Load();

        // Apply the user's saved theme, font size and high-contrast state
        // immediately so the main window renders with them on first paint
        // (no flash of default colors). v21: re-apply live whenever ANY
        // setting changes, so Settings-panel changes take effect instantly.
        Services.ThemeService.ApplyCurrent(Settings.Current);
        Settings.Changed += (_, s) =>
            Current?.Dispatcher.BeginInvoke(() => Services.ThemeService.ApplyCurrent(s));

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
