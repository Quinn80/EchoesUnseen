using EchoesUnseen.Models;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Common contract for all TTS engines (Piper, ElevenLabs, SAPI).
/// The unified <see cref="TtsService"/> routes to one of these based on user settings.
/// </summary>
public interface ITtsEngine
{
    /// <summary>Short identifier: "piper", "elevenlabs", or "sapi".</summary>
    string EngineName { get; }

    /// <summary>True if this engine makes network requests (ElevenLabs only).</summary>
    bool RequiresInternet { get; }

    /// <summary>
    /// Synthesize <paramref name="text"/> using <paramref name="voiceId"/> at
    /// <paramref name="speed"/> (1.0 = normal). Returns raw audio bytes along
    /// with the format so the player knows how to decode them.
    ///   Piper → WAV (PCM 22050 Hz mono)
    ///   ElevenLabs → MP3
    ///   SAPI → WAV
    /// </summary>
    Task<TtsAudio> SynthesizeAsync(string text, string voiceId, float speed, CancellationToken ct = default);

    /// <summary>
    /// Enumerate voices this engine can currently offer.
    /// For Piper this means voices present on disk. For ElevenLabs this is the
    /// user's account voices fetched from /v1/voices. For SAPI it's installed voices.
    /// </summary>
    Task<List<VoiceInfo>> GetAvailableVoicesAsync();
}

/// <summary>Audio payload returned by an engine.</summary>
public record TtsAudio(byte[] Bytes, string Format); // Format: "wav" | "mp3"
