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

    /// <summary>
    /// Speak through the running screen reader (NVDA) instead of the app's own
    /// voice. This is the fix for NVDA conflicts — all speech comes out of ONE
    /// engine (the user's NVDA, with their voice/rate/settings), so there is no
    /// double-talk, no audio ducking, and no separate audio pipeline to drop out.
    /// When on but NVDA isn't running, the app falls back to its own voice.
    /// </summary>
    public bool SpeakThroughNvda { get; set; } = false;

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

    // ── WvW / PvP awareness ─────────────────────────────────────────────────
    /// <summary>How to alert when you enter combat: "off", "sound", "voice", "both".</summary>
    public string CombatAlertMode { get; set; } = "both";
    /// <summary>Announce WvW objective flips, status and lord-invulnerability timers.</summary>
    public bool WvwEnabled { get; set; } = true;
    /// <summary>Your WvW team colour: "auto", "red", "blue", "green". World
    /// Restructuring means the API can't always tell your real team, so this lets
    /// you set it. "auto" uses the API's best guess.</summary>
    public string WvwTeam { get; set; } = "auto";

    /// <summary>Speak a live Righteous Indignation countdown at the objective you're
    /// standing at — milestones (3 min, 2, 1, 30s, 10s) as the lord's invulnerability
    /// ticks down, so you know exactly when an enemy lord becomes killable (or how
    /// long your freshly-captured objective is protected).</summary>
    public bool WvwLordCountdown { get; set; } = true;

    /// <summary>Play a distinct shield cue when Righteous Indignation is up, and an
    /// "opening" cue the moment it drops — a wordless heads-up alongside the voice.</summary>
    public bool WvwLordCue { get; set; } = true;

    /// <summary>Announce when you're running up on a camp/tower/keep and who holds it —
    /// an early-warning so you don't blunder into an enemy fortress while disoriented.</summary>
    public bool WvwApproachAlerts { get; set; } = true;

    // ── Meta-event / world-boss alarm ───────────────────────────────────────
    /// <summary>Chime and announce big world bosses / meta events a few minutes before
    /// they start, off GW2's fixed daily clock — no screen reading needed.</summary>
    public bool MetaAlertsEnabled { get; set; } = true;

    /// <summary>How many minutes before an event starts to sound the alarm (1–30).</summary>
    public int MetaLeadMinutes { get; set; } = 5;

    /// <summary>Hide trail segments whose height differs from yours by more than this
    /// many metres. Without it, a multi-storey place like Divinity's Reach draws the
    /// floors above and below you on top of each other — the "spaghetti". We have no
    /// access to the game's depth buffer, so we can't do true wall occlusion; filtering
    /// by height removes most of the mess for none of the cost.</summary>
    public double TrailElevationToleranceM { get; set; } = 20.0;

    /// <summary>
    /// Elevation tolerance SCALES WITH DISTANCE, which a single flat number cannot do.
    /// Right beside you the route should be at your feet, so anything overhead is a
    /// different storey and must be hidden. Far away a legitimate staircase or bridge is
    /// genuinely much higher, and hiding it would chop the route off mid-flight.
    ///   tolerance(d) = Base + d * PerMetre, capped at Max.
    /// Exposed as settings so they can be tuned after in-game testing.
    /// </summary>
    public double TrailElevBaseM { get; set; } = 3.5;
    public double TrailElevPerMetre { get; set; } = 0.25;
    public double TrailElevMaxM { get; set; } = 30.0;

    /// <summary>How much of the route to paint ahead of you, in metres. The renderer
    /// follows the path forward from where you're standing and stops there, so this is
    /// a length of ROUTE rather than a straight-line radius — 80 m of a winding city
    /// street is a lot less sky than 80 m as the crow flies.
    ///
    /// Came down from 150 m after testing in Divinity's Reach: at that reach most of the
    /// far half of the ribbon was crossing rooftops and courtyards you could not walk to
    /// from here, which reads as clutter. 80 m shows the next couple of turns.</summary>
    public double TrailDrawAheadM { get; set; } = 80.0;

    /// <summary>Draw the trail as real 3-D geometry (Viewport3D) rather than flat
    /// polygons projected by hand onto a canvas.
    ///
    /// The hand-projected version has to reject any point behind the camera and clamp
    /// wildly off-screen ones, which is where the giant diagonal wedges and the pop-in
    /// while turning came from. Handing WPF actual 3-D lets it clip at the near plane
    /// properly and depth-sort the ribbon against itself. Kept as a switch so the flat
    /// renderer stays available if the 3-D one misbehaves on some hardware.</summary>
    public bool TrailUse3D { get; set; } = true;

    /// <summary>User width multiplier for the drawn trail, on top of the pack's own
    /// trailScale. TacO's default trail is 1.016 m WIDE IN THE WORLD, so width is a
    /// world measurement that shrinks with distance like anything else — not a fixed
    /// number of screen pixels.</summary>
    public double TrailWidthMultiplier { get; set; } = 1.0;


    // ── Camera-vs-body "audio puck" ─────────────────────────────────────────

    /// <summary>Announce wheel navigation with the app's OWN voice. Turn off if a
    /// screen reader (NVDA) already reads the buttons, to avoid hearing them twice.</summary>
    public bool SpeakHudNav { get; set; } = true;

    /// <summary>Auto-read game content when the mouse rests over it: hover a
    /// trading-post row, inventory item or tooltip and it's OCR'd and spoken.</summary>
    public bool HoverReadGame { get; set; } = false;

    /// <summary>OCR engine: "windows" (fast, built-in) or "tesseract" (higher
    /// accuracy on small game text; downloads a model on first use).</summary>
    public string OcrEngine { get; set; } = "windows";

    /// <summary>Diagnostic logging: write raw OCR output, regions and filter
    /// decisions to ocr-diagnostics.log so reading problems can be pinpointed.</summary>
    public bool DiagLogging { get; set; } = true;
    /// <summary>How long (ms) the cursor must rest before an auto hover-read
    /// fires. 300..3000.</summary>
    public int HoverReadDwellMs { get; set; } = 750;

    /// <summary>Hover targeting: the enhanced reader - OpenCV finds the object under the pointer,
    /// RapidOCR reads it - instead of the classic reader.
    ///
    /// ON by default since b1.5, in every build. It is what the release introduces, it is measured
    /// against the recorded corpus before every build, and the classic reader stays one switch away
    /// for anyone who prefers it.</summary>
    public bool HoverTargetingFusion { get; set; } = HoverTargetingDefault;

    /// <summary>True once the player has flipped the targeting switch themselves. Until then the
    /// build's default applies on every launch, so a settings file written by an older build
    /// (where the default was classic) cannot pin classic without anyone choosing it.</summary>
    public bool HoverTargetingChosen { get; set; }

    public const bool HoverTargetingDefault = true;

    // ── Trail Navigator colors ──────────────────────────────────────────────
    public TrailColors TrailColors { get; set; } = new();
    public float SonarVolume { get; set; } = 0.3f;

    /// <summary>Which sonar ping timbre to use (see SonarService.Profiles).</summary>
    public string SonarSound { get; set; } = "soft-sine";

    /// <summary>Gentle heartbeat mode: a soft, slow, steady pulse that doesn't
    /// speed up as you approach — calmer than the reactive ping.</summary>
    public bool SonarHeartbeat { get; set; } = false;

    // ── Spoken turn-by-turn guide ───────────────────────────────────────────
    /// <summary>Speak turn-by-turn steering to the guided target — "turn left,
    /// straight ahead, go forward" — using the camera facing as your heading. This
    /// is the sightless navigation primitive: you never look at anything, the voice
    /// turns you. On by default; toggle with the Voice-guide hotkey.</summary>
    public bool VoiceGuideEnabled { get; set; } = true;

    /// <summary>Left/right can come out mirrored depending on the map's coordinate
    /// handedness. If "turn right" sends you left, flip this once and it's fixed for
    /// good. Only swaps the spoken side — distance and clock hour are unaffected.</summary>
    public bool NavGuideFlipTurns { get; set; } = false;

    // ── Interactive-item proximity alerts ───────────────────────────────────


    // ── Visible marker overlay (from imported marker packs) ─────────────────
    /// <summary>Draw imported marker-pack points on screen as big, high-contrast,
    /// animated markers — the accessible visual overlay TacO/Blish don't do well.</summary>
    public bool MarkerVisualEnabled { get; set; } = false;
    /// <summary>Which marker category to render on screen (empty = the one you're guiding to).</summary>
    public string MarkerVisualCategory { get; set; } = "";
    /// <summary>Chosen colour (index into MarkerOverlay.Colors) and animation
    /// (index into MarkerOverlay.Animations), and overall marker size in pixels.</summary>
    public int MarkerColorIndex { get; set; } = 0;
    public int MarkerAnimIndex { get; set; } = 0;
    public double MarkerSize { get; set; } = 48;
    /// <summary>Also draw a connecting trail line through the markers.</summary>
    public bool MarkerTrailLine { get; set; } = true;
    /// <summary>Markers you've reached ("mapId|x|z|category") — hidden once unlocked.</summary>
    public List<string> VisitedMarkers { get; set; } = new();

    /// <summary>Objectives you've REACHED while using Echoes Unseen ("mapId|type|name").
    /// The GW2 API doesn't expose which waypoints/POIs an account has unlocked, so
    /// this is a local "been there" record — used to skip found objectives so the
    /// sonar guides you to new ones, and to show your progress per map.</summary>
    public List<string> VisitedObjectives { get; set; } = new();

    // ── Navigation compass (on-screen animated arrow) ───────────────────────

    // ── Lead-line trail (ground chevrons pointing to the target) ─────────────

    // ── Guitar-Hero guide overlay ───────────────────────────────────────────
    /// <summary>"timed" (notes fall on their own) or "step" (waits for you to
    /// press the correct key). Default is "step" — it never rushes ahead; the next
    /// note waits at the line until you play the right key, which is what a
    /// learner (and a low-vision player) actually wants.</summary>
    public string GuideMode { get; set; } = "step";
    /// <summary>Lead time in ms: how long a note takes to fall to the line in
    /// timed mode (bigger = slower/easier). 700..4000.</summary>
    public int GuideLeadMs { get; set; } = 2000;
    /// <summary>Play a soft tone as each note hits the line, so you can follow by ear.</summary>
    public bool GuideTones { get; set; } = true;
    /// <summary>Note colour (#RRGGBB); empty = theme accent.</summary>
    public string GuideColor { get; set; } = "";
    /// <summary>Overall guide scale, 0.6..1.8.</summary>
    public double GuideSize { get; set; } = 1.0;

    // ── API keys (encrypted at rest, see SettingsService) ───────────────────
    /// <summary>Offer the bug recorder at all. On by default: the whole point is that
    /// a report can be made the moment something goes wrong, without first going to
    /// find a setting.</summary>
    /// <summary>Say who spoke, as well as what they said.
    ///
    /// Names are the hardest thing on the screen to read: they are proper nouns with
    /// deliberately odd spellings, in a small stylised font, so a reader that gets the
    /// message right still mangles the name - and an unpronounceable mangle gets SPELLED
    /// OUT letter by letter, which is worse than silence. Off, you hear what was said
    /// and not a garbled name.</summary>
    public bool ChatSpeakNames { get; set; } = true;

    /// <summary>A DIFFERENT voice for chat, so you can tell at once whether you are
    /// hearing another player or the app talking to you.
    ///
    /// Everything shared one voice, which meant a WvW callout, an inventory read and a
    /// stranger in map chat all sounded identical - you had to parse the words before
    /// you knew what kind of thing you were listening to. Empty means "same as the main
    /// voice"; set it to any installed Piper voice id.</summary>
    public string ChatVoiceId { get; set; } = "";

    /// <summary>Which engine the chat voice belongs to.
    ///
    /// A voice id means nothing without its engine. Chat was hard-coded to the Windows
    /// engine, so handing it a Piper voice id changed nothing at all - the id did not
    /// match any Windows voice, so it fell back to the default one and chat carried on
    /// sounding exactly like everything else.</summary>
    public string ChatVoiceEngine { get; set; } = "";

    public bool BugRecorderEnabled { get; set; } = true;

    /// <summary>A random id for this installation, put at the top of every report.
    ///
    /// NOT a fingerprint: a fresh GUID made on first use, tied to nothing about the
    /// machine or the person. Its only job is to let one abusive reporter be blocked
    /// without having to tear down the feedback channel for everyone honest. Deleting
    /// settings.json gives you a new one.</summary>
    public string ReporterId { get; set; } = "";

    /// <summary>Sending history, for rate limiting. Stored rather than kept in memory so
    /// restarting the app is not a way around the limit.</summary>
    public DateTime FeedbackLastSentUtc { get; set; } = DateTime.MinValue;
    public string FeedbackSentOn { get; set; } = "";
    public int FeedbackSentCount { get; set; }

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
    /// <summary>Pause reading chat while you're in combat — one less thing in your
    /// ears while you're fighting to stay alive. Resumes automatically after.</summary>
    public bool ChatPauseInCombat { get; set; } = true;

    /// <summary>The chat box rectangle, in screen pixels. Zero width means unset.
    ///
    /// This used to live only in memory, so it was lost on every restart and the chat
    /// reader did nothing until the region was drag-selected again. Dragging a rectangle
    /// around a small area of the screen is close to the worst thing you can ask of the
    /// person this app is for, and it was being asked EVERY session.</summary>
    public double ChatRegionX { get; set; }
    public double ChatRegionY { get; set; }
    public double ChatRegionW { get; set; }
    public double ChatRegionH { get; set; }

    /// <summary>"Quiet mode": hush the always-on readers (chat, WvW, HUD nav) while
    /// you're actively playing, without turning any feature off. Toggled by hotkey.
    /// Manual reads (read-under-cursor) still speak.</summary>
    public bool QuietMode { get; set; } = false;

    // ── Local AI (optional, opt-in) ─────────────────────────────────────────
    /// <summary>Vision model for reading/describing the screen. llama3.2-vision or
    /// minicpm-v are far more accurate than the tiny moondream for game text.</summary>
    /// <summary>Text model for chat/quest summaries (e.g. "llama3.2", "qwen2.5:3b").</summary>
    /// <summary>Use the local AI VISION model to READ the chat box instead of OCR.
    /// Much more accurate on stylised game text, but each scan runs a model inference
    /// (needs Ollama + a capable GPU) so it lags a second or two. Off by default.</summary>
}

/// <summary>HUD position persisted across sessions.</summary>
public class HudPosition
{
    public double X { get; set; } = -1; // -1 = not set yet, will default to screen center
    public double Y { get; set; } = -1;
}
