using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace EchoesUnseen.Services;

/// <summary>
/// Downloads the Piper TTS engine and its default Lessac voice on first launch,
/// so non-technical beta testers never have to run a PowerShell script.
///
/// WHERE FILES GO:
///   Everything installs under %APPDATA%\EchoesUnseen\Piper\ — a folder the user
///   always has permission to write to, even when the app itself is installed in
///   a read-only location like C:\Program Files. (PiperTtsEngine checks this
///   location first, then falls back to the app's bundled Resources\Piper.)
///
///   Layout after install:
///     %APPDATA%\EchoesUnseen\Piper\piper.exe          (+ its DLLs / espeak data)
///     %APPDATA%\EchoesUnseen\Piper\voices\en_US-lessac-high.onnx
///     %APPDATA%\EchoesUnseen\Piper\voices\en_US-lessac-high.onnx.json
///
/// URLS: identical to the ones in install-piper.ps1 (Piper 2023.11.14-2 release
/// on GitHub, Lessac voice from the official rhasspy/piper-voices on HuggingFace).
///
/// PROGRESS: a <see cref="Progress"/> event reports human-readable status lines
/// (e.g. "Downloading voice, 40 percent"). MainWindow speaks these aloud so a
/// blind user hears exactly what's happening during the one-time setup.
///
/// This is a plain file download + unzip. No AI, no telemetry. The files being
/// fetched are the same neural TTS assets we'd otherwise ask the user to install
/// by hand.
/// </summary>
public class PiperInstaller
{
    private const string PiperZipUrl =
        "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string VoiceOnnxUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx";
    private const string VoiceJsonUrl =
        "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/high/en_US-lessac-high.onnx.json";

    private readonly string _piperDir;
    private readonly string _voicesDir;
    private readonly string _piperExe;
    private readonly string _voiceOnnx;
    private readonly string _voiceJson;

    /// <summary>Reports human-readable progress lines suitable for TTS.</summary>
    public event EventHandler<string>? Progress;

    public PiperInstaller(string appDataDir)
    {
        _piperDir = Path.Combine(appDataDir, "Piper");
        _voicesDir = Path.Combine(_piperDir, "voices");
        _piperExe = Path.Combine(_piperDir, "piper.exe");
        _voiceOnnx = Path.Combine(_voicesDir, "en_US-lessac-high.onnx");
        _voiceJson = Path.Combine(_voicesDir, "en_US-lessac-high.onnx.json");
    }

    /// <summary>True if both the engine and its default voice are already present.</summary>
    public bool IsInstalled => File.Exists(_piperExe) && File.Exists(_voiceOnnx) && File.Exists(_voiceJson);

    /// <summary>
    /// Download and install anything that's missing. Safe to call when already
    /// installed (it just returns true). Returns false if any step failed — the
    /// app stays fully usable either way, because TTS falls back to Windows SAPI.
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled)
            return true;

        try
        {
            Directory.CreateDirectory(_piperDir);
            Directory.CreateDirectory(_voicesDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            // ── Step 1: the Piper engine (~25 MB zip) ──
            if (!File.Exists(_piperExe))
            {
                Report("Downloading the natural voice engine. This is a one time setup of about 25 megabytes.");
                var zipPath = Path.Combine(Path.GetTempPath(), "piper_windows_amd64.zip");
                await DownloadFileAsync(http, PiperZipUrl, zipPath, "voice engine", ct);

                Report("Extracting the voice engine.");
                var extractDir = Path.Combine(Path.GetTempPath(), "piper_extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);

                // The zip contains a top-level "piper" folder; copy its contents up.
                var inner = Path.Combine(extractDir, "piper");
                var src = Directory.Exists(inner) ? inner : extractDir;
                CopyDirectory(src, _piperDir);

                TryDelete(zipPath);
                try { Directory.Delete(extractDir, true); } catch { /* temp cleanup best-effort */ }

                if (!File.Exists(_piperExe))
                {
                    Report("The voice engine could not be installed. The app will use the basic Windows voice instead.");
                    return false;
                }
            }

            // ── Step 2: the Lessac voice model (~60 MB) ──
            if (!File.Exists(_voiceOnnx) || !File.Exists(_voiceJson))
            {
                Report("Downloading the natural voice. This is about 60 megabytes and may take a minute.");
                if (!File.Exists(_voiceOnnx))
                    await DownloadFileAsync(http, VoiceOnnxUrl, _voiceOnnx, "voice", ct);
                if (!File.Exists(_voiceJson))
                    await DownloadFileAsync(http, VoiceJsonUrl, _voiceJson, "voice settings", ct);
            }

            if (IsInstalled)
            {
                Report("Natural voice setup complete.");
                return true;
            }

            Report("Voice setup did not finish. The app will use the basic Windows voice for now.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("PiperInstaller.EnsureInstalledAsync", ex);
            Report("The natural voice could not be downloaded, possibly due to the internet connection. The app will use the basic Windows voice. You can try again later from settings.");
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private async Task DownloadFileAsync(HttpClient http, string url, string destPath,
        string label, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long readTotal = 0;
        int lastPctReported = -1;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            readTotal += read;
            if (total > 0)
            {
                var pct = (int)(readTotal * 100 / total);
                // Announce every 20% so we don't spam the screen reader.
                if (pct >= lastPctReported + 20 && pct < 100)
                {
                    lastPctReported = pct;
                    Report($"Downloading {label}, {pct} percent.");
                }
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private void Report(string message) => Progress?.Invoke(this, message);
}
