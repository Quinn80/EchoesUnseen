using System.Diagnostics;
using System.IO;
using System.Net.Http;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Piper TTS engine — fully offline neural text-to-speech.
///
/// Piper is a C++ binary from the Rhasspy project that runs ONNX neural voice
/// models locally. We ship piper.exe and one default voice (Lessac) bundled
/// with the installer, so the app speaks immediately on first launch with
/// zero internet connectivity or voice downloads required.
///
/// Architecture:
///   - Bundled voice: Resources/Piper/voices/en_US-lessac-high.onnx (+ .json config)
///   - User-downloaded voices: %APPDATA%/EchoesUnseen/voices/
///   - Binary: Resources/Piper/piper.exe (Windows x64)
///
/// Invocation: piper writes raw PCM to stdout. We read those bytes, wrap them
/// in a WAV header (22050 Hz, mono, 16-bit) so NAudio can play them, and return
/// the complete WAV blob.
///
/// Speed control: --length_scale is the INVERSE of perceptual speed. A
/// length_scale of 0.5 makes the voice speak twice as fast; 2.0 makes it
/// speak half-speed. We invert the user's speed slider (1.0 = normal) to get
/// the right scale value.
/// </summary>
public class PiperTtsEngine : ITtsEngine
{
    private string _piperExePath;                 // resolved; can change after auto-download
    private readonly string _appDataPiperExe;     // %APPDATA%\EchoesUnseen\Piper\piper.exe
    private readonly string _bundledPiperExe;     // <app>\Resources\Piper\piper.exe
    private readonly string _appDataVoicesDir;    // %APPDATA%\EchoesUnseen\Piper\voices
    private readonly string _bundledVoicesDir;
    private readonly string _userVoicesDir;

    public string EngineName => "piper";
    public bool RequiresInternet => false;

    /// <summary>
    /// Piper audio parameters. These match the en_US-lessac-high model config.
    /// Most Piper voices are 22050 Hz; some are 16000 Hz. The model's .json
    /// config declares which — we'd read it for full correctness, but for the
    /// MVP we hardcode 22050 which covers all English voices in the default catalog.
    /// </summary>
    private const int SampleRate = 22050;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    public PiperTtsEngine(string appDataDir)
    {
        // Look for piper.exe in TWO places, in priority order:
        //   1. AppData (%APPDATA%\EchoesUnseen\Piper\) — this is where the
        //      first-launch auto-downloader installs it. Always writable, even
        //      when the app itself lives in a read-only folder like Program Files.
        //   2. The app's own Resources\Piper\ folder — used if someone ran the
        //      install-piper.ps1 script the old way, or bundled it manually.
        _appDataPiperExe = Path.Combine(appDataDir, "Piper", "piper.exe");
        _bundledPiperExe = Path.Combine(AppContext.BaseDirectory, "Resources", "Piper", "piper.exe");
        _piperExePath = File.Exists(_appDataPiperExe) ? _appDataPiperExe : _bundledPiperExe;

        _appDataVoicesDir = Path.Combine(appDataDir, "Piper", "voices");
        _bundledVoicesDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Piper", "voices");
        _userVoicesDir = Path.Combine(appDataDir, "voices");
        Directory.CreateDirectory(_userVoicesDir);
    }

    /// <summary>Re-resolve the piper.exe path (call after the auto-downloader runs).</summary>
    public void RefreshExePath()
    {
        _piperExePath = File.Exists(_appDataPiperExe) ? _appDataPiperExe : _bundledPiperExe;
    }

    /// <summary>True if a usable piper.exe exists in either location.</summary>
    public bool IsInstalled => File.Exists(_appDataPiperExe) || File.Exists(_bundledPiperExe);

    /// <summary>True if the given catalog voice's model file is already on disk.</summary>
    public bool IsVoiceDownloaded(string voiceId) => ResolveVoicePath(voiceId) != null;

    /// <summary>
    /// Download a catalog voice (model + config) into the user voices folder,
    /// reporting short human-readable progress. Returns true on success. Safe to
    /// call for an already-downloaded voice (returns true immediately).
    /// </summary>
    public async Task<bool> DownloadVoiceAsync(string voiceId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsVoiceDownloaded(voiceId)) return true;

        var voice = PiperVoiceCatalog.Find(voiceId);
        if (voice == null)
        {
            CrashLogger.Log("PiperTtsEngine.DownloadVoiceAsync", new Exception($"Unknown voice '{voiceId}'."));
            return false;
        }

        try
        {
            Directory.CreateDirectory(_userVoicesDir);
            var onnx = Path.Combine(_userVoicesDir, $"{voiceId}.onnx");
            var json = Path.Combine(_userVoicesDir, $"{voiceId}.onnx.json");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            progress?.Report($"Downloading the {voice.Name} voice, {voice.SizeLabel}. One moment.");
            await DownloadWithProgressAsync(http, voice.OnnxUrl, onnx, progress, ct);
            await DownloadWithProgressAsync(http, voice.JsonUrl, json, progress: null, ct);

            if (File.Exists(onnx) && File.Exists(json))
            {
                progress?.Report($"The {voice.Name} voice is ready.");
                return true;
            }
            progress?.Report($"The {voice.Name} voice could not be downloaded.");
            return false;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            CrashLogger.Log("PiperTtsEngine.DownloadVoiceAsync", ex);
            progress?.Report("The voice download failed. Check your internet connection.");
            return false;
        }
    }

    private static async Task DownloadWithProgressAsync(HttpClient http, string url, string dest,
        IProgress<string>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;
        var tmp = dest + ".part";

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[1 << 20];
            long read = 0; int lastPct = -1, n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0 && progress != null)
                {
                    var pct = (int)(read * 100 / total);
                    if (pct >= lastPct + 25 && pct < 100) { lastPct = pct - pct % 25; progress.Report($"{lastPct} percent."); }
                }
            }
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        var modelPath = ResolveVoicePath(voiceId);
        if (modelPath == null)
            throw new FileNotFoundException(
                $"Piper voice '{voiceId}' not found. Expected at '{_bundledVoicesDir}' or '{_userVoicesDir}'.");

        // length_scale is inverse of speed: 0.5 = 2x faster, 2.0 = 0.5x speed
        var lengthScale = (1.0f / Math.Max(0.25f, Math.Min(4.0f, speed))).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = _piperExePath,
            Arguments = $"--model \"{modelPath}\" --output-raw --length_scale {lengthScale}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start piper.exe");

        // Pipe text in...
        await proc.StandardInput.WriteAsync(text);
        proc.StandardInput.Close();

        // ...and read raw PCM bytes out.
        using var pcmBuf = new MemoryStream();
        await proc.StandardOutput.BaseStream.CopyToAsync(pcmBuf, ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Piper exited with code {proc.ExitCode}: {err}");
        }

        // Wrap raw PCM in a WAV header so NAudio can play it.
        var wav = WrapPcmAsWav(pcmBuf.ToArray(), SampleRate, BitsPerSample, Channels);
        return new TtsAudio(wav, "wav");
    }

    public Task<List<VoiceInfo>> GetAvailableVoicesAsync()
    {
        var voices = new List<VoiceInfo>();

        // Bundled voices (always available even on first launch)
        AddOnnxVoicesFromDir(voices, _bundledVoicesDir);

        // Voices installed by the first-launch auto-downloader.
        AddOnnxVoicesFromDir(voices, _appDataVoicesDir);

        // User-downloaded voices (may be empty)
        AddOnnxVoicesFromDir(voices, _userVoicesDir);

        return Task.FromResult(voices);
    }

    /// <summary>Look for the voice file in user voices, then AppData Piper, then bundled.</summary>
    private string? ResolveVoicePath(string voiceId)
    {
        var userPath = Path.Combine(_userVoicesDir, $"{voiceId}.onnx");
        if (File.Exists(userPath)) return userPath;

        // Voices installed by the first-launch auto-downloader.
        var appDataPath = Path.Combine(_appDataVoicesDir, $"{voiceId}.onnx");
        if (File.Exists(appDataPath)) return appDataPath;

        var bundledPath = Path.Combine(_bundledVoicesDir, $"{voiceId}.onnx");
        if (File.Exists(bundledPath)) return bundledPath;

        return null;
    }

    private static void AddOnnxVoicesFromDir(List<VoiceInfo> list, string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.onnx"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            // id format: "en_US-lessac-high" → language "en_US", name "lessac", quality "high"
            var parts = id.Split('-');
            // Prefer the catalog's polished label; fall back to the filename.
            // The quality is included because several voices ship in BOTH medium
            // and high — without it the dropdown would show two identical names.
            var catalog = PiperVoiceCatalog.Find(id);
            string displayName;
            if (catalog != null)
            {
                displayName = $"{catalog.PrettyName} ({catalog.Quality})";
            }
            else if (parts.Length >= 3)
            {
                displayName = $"{char.ToUpper(parts[1][0]) + parts[1][1..]} ({parts[2]})";
            }
            else
            {
                displayName = parts.Length >= 2 ? char.ToUpper(parts[1][0]) + parts[1][1..] : id;
            }
            list.Add(new VoiceInfo
            {
                Id = id,
                Name = displayName,
                Engine = "piper",
                Language = parts.Length >= 1 ? parts[0] : null,
                IsDownloaded = true,
                SizeBytes = new FileInfo(file).Length,
            });
        }
    }

    /// <summary>
    /// Wraps raw signed-16-bit PCM bytes in a minimal WAV RIFF header so NAudio's
    /// WaveFileReader can play them. Without this, NAudio sees raw PCM and errors.
    /// </summary>
    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int bitsPerSample, int channels)
    {
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                 // PCM fmt chunk size
        w.Write((short)1);           // PCM format
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);

        return ms.ToArray();
    }
}
