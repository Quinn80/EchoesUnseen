namespace EchoesUnseen.Models;

/// <summary>
/// Unified voice descriptor returned by every TTS engine.
///
/// <see cref="Id"/> is engine-specific:
///   Piper: model filename without extension (e.g. "en_US-lessac-high")
///   ElevenLabs: voice ID from the /v1/voices endpoint
///   SAPI: installed Windows voice name (e.g. "Microsoft Zira Desktop")
///
/// <see cref="IsDownloaded"/> only matters for Piper voices. For ElevenLabs
/// and SAPI it's always true (voices are remote or OS-installed respectively).
/// </summary>
public class VoiceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Engine { get; set; } = ""; // "piper" | "elevenlabs" | "sapi"
    public string? Language { get; set; }
    public string? Gender { get; set; }
    public bool IsDownloaded { get; set; } = true;
    public long SizeBytes { get; set; } = 0;

    /// <summary>URL for downloading (Piper voices only).</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Display label combining name and engine, e.g. "Lessac (Piper)".</summary>
    public string DisplayLabel => $"{Name} ({EngineDisplayName})";

    public string EngineDisplayName => Engine switch
    {
        "piper"      => "Piper",
        "elevenlabs" => "ElevenLabs",
        "sapi"       => "Windows",
        _            => Engine
    };
}
