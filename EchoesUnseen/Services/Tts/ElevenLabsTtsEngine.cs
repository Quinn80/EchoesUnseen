using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// ElevenLabs cloud TTS engine — premium, most natural voices available.
/// OPTIONAL: only active if the user has entered their own API key in Settings.
///
/// Uses the v1/text-to-speech endpoint with the turbo_v2_5 model which has a
/// good balance of quality and latency. Free-tier accounts get 10,000
/// characters per month which is plenty for typical overlay usage.
///
/// The API key is passed in per-call rather than stored in the service because
/// the user can change it in Settings at any time.
/// </summary>
public class ElevenLabsTtsEngine : ITtsEngine
{
    private readonly HttpClient _http;
    private readonly Func<string> _getApiKey;

    public string EngineName => "elevenlabs";
    public bool RequiresInternet => true;

    public ElevenLabsTtsEngine(Func<string> getApiKey)
    {
        _getApiKey = getApiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<TtsAudio> SynthesizeAsync(string text, string voiceId, float speed, CancellationToken ct = default)
    {
        var apiKey = _getApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("ElevenLabs API key is not set. Enter one in Settings > API Keys.");

        // ElevenLabs doesn't support speed in the main synthesis call — we'd
        // have to post-process the audio with a time-stretcher. For the MVP we
        // accept that speed is only honored by Piper. ElevenLabs audio always
        // plays at native pace.
        _ = speed; // intentional: documented limitation

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}");
        req.Headers.Add("xi-api-key", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        req.Content = JsonContent.Create(new
        {
            text = text,
            model_id = "eleven_turbo_v2_5",
            voice_settings = new { stability = 0.5f, similarity_boost = 0.75f },
        });

        var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"ElevenLabs {res.StatusCode}: {body}");
        }

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return new TtsAudio(bytes, "mp3");
    }

    public async Task<List<VoiceInfo>> GetAvailableVoicesAsync()
    {
        var apiKey = _getApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return new List<VoiceInfo>();

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
            req.Headers.Add("xi-api-key", apiKey);
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return new List<VoiceInfo>();

            var json = await res.Content.ReadAsStringAsync();
            var parsed = JsonSerializer.Deserialize<ElevenLabsVoicesResponse>(json, JsonOpts);
            return parsed?.Voices?.Select(v => new VoiceInfo
            {
                Id = v.VoiceId ?? "",
                Name = v.Name ?? "(unnamed)",
                Engine = "elevenlabs",
                Gender = v.Labels?.TryGetValue("gender", out var g) == true ? g : null,
                Language = v.Labels?.TryGetValue("accent", out var a) == true ? a : null,
                IsDownloaded = true,
            }).ToList() ?? new List<VoiceInfo>();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ElevenLabs.GetAvailableVoicesAsync", ex);
            return new List<VoiceInfo>();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Deserialization types ────────────────────────────────────────────────
    private class ElevenLabsVoicesResponse
    {
        [JsonPropertyName("voices")]
        public List<ElevenLabsVoice>? Voices { get; set; }
    }

    private class ElevenLabsVoice
    {
        [JsonPropertyName("voice_id")]
        public string? VoiceId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("labels")]
        public Dictionary<string, string>? Labels { get; set; }
    }
}
