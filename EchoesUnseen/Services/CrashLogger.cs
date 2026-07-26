using System.IO;

namespace EchoesUnseen.Services;

/// <summary>
/// Writes unhandled exceptions to %APPDATA%\EchoesUnseen\crash.log.
///
/// Why this exists: WPF applications can silently disappear when an exception
/// escapes the dispatcher or a background thread. Having a durable log file
/// means we can diagnose field issues even when the user can't reproduce them.
///
/// Thread-safe via a static lock — crashes from multiple threads won't corrupt
/// the log file.
/// </summary>
public static class CrashLogger
{
    private static readonly object _lock = new();
    private static readonly string _logPath;

    static CrashLogger()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EchoesUnseen");
        Directory.CreateDirectory(appDataDir);
        _logPath = Path.Combine(appDataDir, "crash.log");
    }

    /// <summary>
    /// Append a labeled exception to the crash log with a UTC timestamp.
    /// Silently no-ops on write errors — we never want the crash handler itself
    /// to throw and cause an infinite loop.
    /// </summary>
    public static void Log(string source, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                var entry = $"[{timestamp}] {source}\n{ex}\n\n";
                File.AppendAllText(_logPath, entry);
            }
        }
        catch
        {
            /* swallow — logging failure shouldn't crash the app */
        }
    }

    /// <summary>
    /// v21.2: append a plain informational breadcrumb (no exception) — used
    /// for diagnostics like "web click received", so problems that leave no
    /// exception still leave evidence. Same never-throws guarantee as above.
    /// </summary>
    public static void Log(string source, string message)
    {
        try
        {
            lock (_lock)
            {
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
                File.AppendAllText(_logPath, $"[{timestamp}] {source}: {message}\n");
            }
        }
        catch
        {
            /* swallow — logging failure shouldn't crash the app */
        }
    }

    /// <summary>Full path to the crash log — surface this in Settings > About.</summary>
    public static string LogPath => _logPath;
}
