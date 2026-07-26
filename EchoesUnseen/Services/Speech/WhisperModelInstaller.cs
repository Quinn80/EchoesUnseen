using System.IO;
using System.Net.Http;

namespace EchoesUnseen.Services.Speech;

/// <summary>
/// Downloads a Whisper GGML model on first use, with spoken progress — the same
/// pattern as the Piper voice installer, so a blind user always hears what is
/// happening during the one-time setup.
///
/// Models are fetched from the canonical whisper.cpp model repository on Hugging
/// Face (the same source Whisper.net's own downloader uses). Downloading a model
/// file over HTTPS is version-proof, unlike the library's downloader API which
/// has changed between releases.
///
/// PRIVACY: this is the only network call the speech feature ever makes, and it
/// happens exactly once per model. After the file is on disk, all recognition is
/// 100% local — no audio, text, or telemetry ever leaves the machine.
/// </summary>
internal sealed class WhisperModelInstaller
{
    /// <summary>Known models: key → (file name, download URL, human size).</summary>
    public static readonly IReadOnlyDictionary<string, (string File, string Url, string Size)> Models =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny.en"]  = ("ggml-tiny.en.bin",
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin",
                "about 75 megabytes"),
            ["base.en"]  = ("ggml-base.en.bin",
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin",
                "about 140 megabytes"),
            ["small.en"] = ("ggml-small.en.bin",
                "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin",
                "about 470 megabytes"),
        };

    private readonly string _modelDir;

    public WhisperModelInstaller(string appDataDir)
    {
        _modelDir = Path.Combine(appDataDir, "Whisper");
        Directory.CreateDirectory(_modelDir);
    }

    /// <summary>Absolute path where the given model's file lives (present or not).</summary>
    public string PathFor(string modelKey)
    {
        var file = Models.TryGetValue(modelKey, out var m) ? m.File : "ggml-base.en.bin";
        return Path.Combine(_modelDir, file);
    }

    public bool IsInstalled(string modelKey) => File.Exists(PathFor(modelKey));

    /// <summary>Raised with short human-readable progress lines, meant to be spoken.</summary>
    public event EventHandler<string>? Progress;

    /// <summary>
    /// Ensure the model file exists locally, downloading it if needed. Returns the
    /// path on success, or null on failure (already logged). Safe to call every
    /// time; it no-ops when the file is already present.
    /// </summary>
    public async Task<string?> EnsureAsync(string modelKey, CancellationToken ct = default)
    {
        try
        {
            if (!Models.TryGetValue(modelKey, out var m))
            {
                CrashLogger.Log("WhisperModelInstaller", $"Unknown model '{modelKey}'.");
                return null;
            }

            var dest = Path.Combine(_modelDir, m.File);
            if (File.Exists(dest)) return dest;

            Progress?.Invoke(this,
                $"Downloading the {modelKey} voice recognition model, {m.Size}. " +
                "This happens only once. I'll let you know when it's ready.");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            var tmp = dest + ".part";

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 20]; // 1 MiB
                long read = 0;
                int lastPct = -1, n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                    {
                        // Announce at 25 / 50 / 75% so the user isn't left in silence.
                        var pct = (int)(read * 100 / total);
                        if (pct >= lastPct + 25 && pct < 100)
                        {
                            lastPct = pct - pct % 25;
                            Progress?.Invoke(this, $"{lastPct} percent.");
                        }
                    }
                }
            }

            // Atomic-ish rename so a half-written file is never treated as valid.
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(tmp, dest);

            Progress?.Invoke(this, "Voice recognition model ready.");
            return dest;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WhisperModelInstaller.EnsureAsync", ex);
            Progress?.Invoke(this,
                "The voice recognition model could not be downloaded. " +
                "Check your internet connection, or switch to Windows speech in Settings.");
            return null;
        }
    }
}
