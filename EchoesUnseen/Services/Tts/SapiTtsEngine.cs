using System.IO;
using System.Speech.Synthesis;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Windows SAPI (System.Speech.Synthesis) TTS engine.
///
/// THIS IS THE FALLBACK ENGINE. Piper is the default (natural-sounding local
/// neural TTS), but if Piper isn't installed yet (binaries missing) or fails
/// to launch, SAPI takes over. SAPI ships with every Windows install and is
/// always available, so we never run out of speech output.
///
/// The classical SAPI voices (Zira, David, Mark) sound robotic — they're
/// based on 1990s concatenative synthesis, not neural networks. The trade-off
/// is they involve zero AI/ML.
///
/// IMPORTANT: System.Speech defines its own type named "VoiceInfo" which
/// collides with our own EchoesUnseen.Models.VoiceInfo. We use fully
/// qualified names below to keep the compiler unambiguous.
/// </summary>
public class SapiTtsEngine : ITtsEngine
{
    public string EngineName => "sapi";
    public bool RequiresInternet => false;

    public async Task<TtsAudio> SynthesizeAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        // SAPI runs synchronously; offload to thread pool so UI stays responsive.
        return await Task.Run(() =>
        {
            using var synth = new SpeechSynthesizer();

            // Select the voice (fallback to default if the named voice isn't installed)
            if (!string.IsNullOrWhiteSpace(voiceId))
            {
                try { synth.SelectVoice(voiceId); }
                catch { /* fall through to default voice */ }
            }

            // SAPI rate is -10 (very slow) to +10 (very fast).
            // Map 1.0 -> 0 (normal), 0.5 -> -5 (half speed), 2.0 -> +10 (max fast).
            synth.Rate = Math.Clamp((int)Math.Round((speed - 1.0f) * 10f), -10, 10);
            synth.Volume = 100;

            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);

            return new TtsAudio(ms.ToArray(), "wav");
        }, ct);
    }

    public async Task<List<EchoesUnseen.Models.VoiceInfo>> GetAvailableVoicesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                using var synth = new SpeechSynthesizer();
                var result = new List<EchoesUnseen.Models.VoiceInfo>();
                foreach (var installed in synth.GetInstalledVoices())
                {
                    if (!installed.Enabled) continue;
                    var info = installed.VoiceInfo; // System.Speech.Synthesis.VoiceInfo
                    result.Add(new EchoesUnseen.Models.VoiceInfo
                    {
                        Id = info.Name,
                        Name = info.Name,
                        Engine = "sapi",
                        Language = info.Culture?.Name,
                        Gender = info.Gender.ToString(),
                        IsDownloaded = true,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("SapiTtsEngine.GetAvailableVoicesAsync", ex);
                return new List<EchoesUnseen.Models.VoiceInfo>();
            }
        });
    }
}
