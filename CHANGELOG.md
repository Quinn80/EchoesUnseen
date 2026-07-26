# Changelog

All notable changes to Echoes Unseen. The current public release is **B1.1**.

---

## B1.1 — Public beta

The first public beta of the native Windows rebuild. Highlights:

### Navigation & HUD
- Radial wheel driven by `Alt` + arrow keys, `Alt+Enter` to open — works while Guild Wars 2 has focus, and never steals focus from the game.
- Wheel minimises to just its logo while you play and unfolds on hover.
- Reposition the wheel with `Ctrl+Shift` + arrows; it remembers where you left it between sessions.
- A spoken first-run welcome screen listing the starter controls, replayable from Settings.

### Voice
- Natural local text-to-speech via Piper, with 22 English voices (US and British, male and female) to preview and download; six arrive automatically on first run.
- Voice engine and voice are chosen separately, so picking a voice can't switch engines by accident.
- ElevenLabs (optional cloud) stays disabled until you add your own API key.

### Reading the game
- **Chat Reader** — continuous on-device OCR of the chat region, with improved line splitting and de-duplication so messages aren't re-read.
- **Read under the pointer** (`Ctrl+Shift+Space`) — speaks item tooltips, menu buttons and list rows.
- **Item finder** — searches your bank, every character's bags and material storage through the official GW2 API, and tells you exactly where an item is and how to surface it.
- Screen OCR upscales small game text before recognition for sharper results.

### Speech-to-text
- Dictate into chat with local speech-to-text: Whisper (local AI) or the built-in Windows recognizer. Audio never leaves the PC.

### Personalisation
- Seven themes, each retuning the app's audio cues as well as its colours.
- A Features tab to toggle any optional behaviour, and a Keybinds tab to view and rebind every shortcut by pressing the keys you want.
- Add your own sheet music to the Music Player (number notation, ABC letters, or a file).
- Background features (Chat Reader, Trail Navigator sonar, Music Player) keep running with their window closed.

### Under the hood
- Native .NET 8 / WPF overlay — a single process talking directly to Windows APIs (MumbleLink, OCR, microphone, transparent click-through overlay, global hotkeys via Win32 `RegisterHotKey`).
- Everything runs locally. The only network calls are on-demand model downloads and the optional GW2 API / ElevenLabs cloud voice, both gated behind your own keys.

---

*Earlier internal builds predate this public beta and are not tracked here.*
