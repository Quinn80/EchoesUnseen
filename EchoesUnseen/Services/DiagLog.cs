using System.IO;

namespace EchoesUnseen.Services;

/// <summary>
/// Lightweight diagnostic logger for the screen-reading pipeline. When enabled it
/// records, per read, which engine ran, the region size, the RAW OCR output, and
/// what got filtered vs spoken — so problems (fragmented chat, gibberish map
/// text) can be pinpointed from one shareable file instead of guesswork.
///
/// Writes to %APPDATA%\EchoesUnseen\ocr-diagnostics.log. Best-effort; never
/// throws into the caller. Off = zero overhead.
/// </summary>
public static class DiagLog
{
    private static readonly object _lock = new();

    public static bool Enabled => App.Settings.Current.DiagLogging;

    public static string LogPath =>
        Path.Combine(App.Settings.AppDataDirectory, "ocr-diagnostics.log");

    public static void Log(string category, string message)
    {
        if (!Enabled) return;
        try
        {
            lock (_lock)
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {category}: {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Log an OCR pass: engine, region, and the raw text (quoted, capped).</summary>
    public static void Ocr(string context, string engine, int w, int h, string raw)
    {
        if (!Enabled) return;
        var oneLine = (raw ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
        if (oneLine.Length > 400) oneLine = oneLine[..400] + "…";
        Log("OCR", $"{context} engine={engine} region={w}x{h} out=\"{oneLine}\"");
    }

    /// <summary>Clear the log (e.g. a "start fresh" button).</summary>
    public static void Clear()
    {
        try { lock (_lock) File.WriteAllText(LogPath, $"# Echoes Unseen OCR diagnostics — {DateTime.Now}{Environment.NewLine}"); }
        catch { }
    }
}
