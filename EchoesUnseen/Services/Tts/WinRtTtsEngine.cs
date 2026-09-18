using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Windows OneCore / "Natural" neural voice engine (Windows.Media.SpeechSynthesis).
///
/// This runs entirely IN-PROCESS — like SAPI, there is NO child process to stall
/// or hang — but unlike the classic SAPI voices (robotic Zira/David) it exposes
/// the modern OneCore voices, INCLUDING the Windows 11 "Natural" neural voices a
/// user installs from Settings → Time &amp; Language → Speech → Manage voices.
///
/// That combination — natural-sounding AND reliable — is exactly what the
/// always-on readers (chat, combat, WvW, HUD navigation) want, so they route here
/// instead of to robotic SAPI. It's also selectable as the main voice.
///
/// No AI training and no game data leave the machine: this is Microsoft's local
/// on-device synthesis of text WE generate, same trust model as Piper.
/// </summary>
public sealed class WinRtTtsEngine : ITtsEngine
{
    public string EngineName => "winnatural";
    public bool RequiresInternet => false;

    public async Task<TtsAudio> SynthesizeAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        using var synth = new SpeechSynthesizer();

        // Pick the requested voice; otherwise the system default is used.
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var v = SpeechSynthesizer.AllVoices.FirstOrDefault(x =>
                x.Id == voiceId ||
                string.Equals(x.DisplayName, voiceId, StringComparison.OrdinalIgnoreCase));
            if (v != null) synth.Voice = v;
        }

        // SpeakingRate: 0.5 (half) .. 6.0 (very fast), 1.0 = normal. Our slider is
        // 0.5..4.0, so it maps straight through — great for high-WPM listeners.
        try { synth.Options.SpeakingRate = Math.Clamp(speed, 0.5, 6.0); } catch { }

        // Synthesize to an in-memory WAV stream and copy the bytes out via a
        // DataReader (avoids depending on the WinRT->Stream bridge extension).
        using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        uint size = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask(ct);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return new TtsAudio(bytes, "wav");
    }

    public Task<List<EchoesUnseen.Models.VoiceInfo>> GetAvailableVoicesAsync()
    {
        var result = new List<EchoesUnseen.Models.VoiceInfo>();
        try
        {
            foreach (var v in SpeechSynthesizer.AllVoices)
            {
                bool natural = v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase);
                result.Add(new EchoesUnseen.Models.VoiceInfo
                {
                    Id = v.Id,
                    // Flag the good ones so they're easy to spot in the picker.
                    Name = natural ? $"{v.DisplayName} ⭐ natural" : $"{v.DisplayName} (Windows)",
                    Engine = "winnatural",
                    Language = v.Language,
                    Gender = v.Gender.ToString(),
                    IsDownloaded = true,
                });
            }
        }
        catch (Exception ex) { CrashLogger.Log("WinRtTtsEngine.GetAvailableVoicesAsync", ex); }
        return Task.FromResult(result);
    }
}
