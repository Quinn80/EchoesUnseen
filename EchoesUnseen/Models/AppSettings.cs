namespace EchoesUnseen.Models;

/// <summary>
/// User-configurable application settings.
///
/// Persisted to %APPDATA%\EchoesUnseen\settings.json on every change.
/// API keys are additionally encrypted at rest via DPAPI in SettingsService.
///
/// CRITICAL: Do NOT rename <see cref="AccessMode"/> — its value ("vip" or "standard")
/// is a persistence-critical key that existing users' settings files expect.
/// </summary>
public class AppSettings
{
    // ── Display / accessibility ─────────────────────────────────────────────
    public int FontSize { get; set; } = 22;
    public bool HighContrast { get; set; } = true;
    public string AccessMode { get; set; } = "vip"; // "vip" | "standard"
    public float HudScale { get; set; } = 1.0f;

    /// <summary>Theme ID — one of the entries in ThemeService.BuiltInThemes.</summary>
    public string ThemeId { get; set; } = "hot-pink";

    // ── TTS Engine (three-tier) ────────────────────────────────────────────
    /// <summary>
    /// "piper" (default — local neural TTS, natural-sounding, offline) |
    /// "elevenlabs" (cloud premium — natural-sounding, needs API key) |
    /// "sapi" (Windows OS classical TTS, robotic but always available).
    ///
    /// Piper and ElevenLabs are neural TTS engines. They only synthesize text
    /// WE generate (wiki articles, transcripts, panel readouts) — they never
    /// receive Guild Wars 2 game data. The product website discloses this
    /// trade-off so users understand the choice.
    /// </summary>
    public string VoiceEngine { get; set; } = "piper";

    /// <summary>
    /// Engine-specific voice identifier.
    ///   Piper: model filename without extension (e.g. "en_US-lessac-high")
    ///   ElevenLabs: voice ID from /v1/voices endpoint
    ///   SAPI: voice name as reported by SpeechSynthesizer.GetInstalledVoices() (e.g. "Microsoft Zira Desktop")
    ///   ElevenLabs: voice ID, e.g. "21m00Tcm4TlvDq8ikWAM" for Rachel
    ///   SAPI: installed voice name
    /// </summary>
    public string VoiceId { get; set; } = "en_US-lessac-high";

    /// <summary>Human-readable name shown in the Settings UI.</summary>
    public string VoiceName { get; set; } = "Lessac";

    public float Volume { get; set; } = 1.0f;
    public float TtsSpeed { get; set; } = 1.0f;
    public float TtsPitch { get; set; } = 1.0f;

    // ── Keybinds ────────────────────────────────────────────────────────────
    public KeyBindings Keybinds { get; set; } = new();

    // ── HUD customization ───────────────────────────────────────────────────
    public List<string> HiddenButtons { get; set; } = new();
    public HudPosition HudPosition { get; set; } = new();

    // ── Accessibility ───────────────────────────────────────────────────────
    /// <summary>True once the first-launch spoken orientation has played.</summary>
    public bool FirstRunIntroSpoken { get; set; } = false;
    /// <summary>Speak "HUD active" when the cursor enters the ring (off by default;
    /// keyboard-mode users get announcements through focus instead).</summary>
    public bool AnnounceHudActivation { get; set; } = false;
    /// <summary>Play short audio earcons when panels open/close.</summary>
    public bool PanelEarcons { get; set; } = true;

    /// <summary>
    /// Shrink the HUD to just its centre logo when the pointer moves away, and
    /// unfold the full wheel again on hover. Keeps the overlay out of the way
    /// during play without giving up any functionality — every tool is still
    /// reachable from the keyboard while minimised.
    /// </summary>
    public bool MinimiseHud { get; set; } = true;

    /// <summary>
    /// Speak whatever the mouse pointer rests on inside an open panel (buttons,
    /// labels, results, field values). A hover-to-hear layer for low-vision
    /// users. Toggle live with Ctrl+Shift+R.
    /// </summary>
    public bool HoverToRead { get; set; } = true;

    // ── Trail Navigator colors ──────────────────────────────────────────────
    public TrailColors TrailColors { get; set; } = new();
    public float SonarVolume { get; set; } = 0.5f;

    // ── API keys (encrypted at rest, see SettingsService) ───────────────────
    public string Gw2ApiKey { get; set; } = "";
    public string ElevenLabsApiKey { get; set; } = "";

    // ── Voice to Chat ───────────────────────────────────────────────────────
    /// <summary>Microphone identified by DEVICE NAME not index (names are stable across reboots).</summary>
    public string SelectedMicName { get; set; } = "";

    /// <summary>
    /// Speech-to-text engine for dictation.
    ///   "windows" (default) — the built-in Windows Speech Recognition HMM. No
    ///     AI, no download, always available; weaker on open-vocabulary text.
    ///   "whisper" — OpenAI Whisper running fully locally (inference only) for
    ///     much higher accuracy. Downloads a model file once on first use.
    /// The trade-off and the local-only guarantee are disclosed in the app docs
    /// and on the website; users opt in deliberately.
    /// </summary>
    public string SttEngine { get; set; } = "windows";

    /// <summary>
    /// Which Whisper model to use when SttEngine is "whisper":
    ///   "tiny.en"  — fastest, lowest accuracy (~75 MB)
    ///   "base.en"  — recommended balance (~140 MB)
    ///   "small.en" — most accurate, slower (~470 MB)
    /// English-only models; they are smaller and more accurate than the
    /// multilingual variants for English dictation.
    /// </summary>
    public string WhisperModel { get; set; } = "base.en";

    // ── Chat Reader ─────────────────────────────────────────────────────────
    public int ChatReaderInterval { get; set; } = 3500;
    public int ChatReaderMessageCount { get; set; } = 3;
}

/// <summary>HUD position persisted across sessions.</summary>
public class HudPosition
{
    public double X { get; set; } = -1; // -1 = not set yet, will default to screen center
    public double Y { get; set; } = -1;
}
