# Echoes Unseen

**More ways to read, navigate and play.**

A free, unofficial Windows companion for Guild Wars 2, designed especially for players with low vision. Choose spoken information, clearer creator trails and keyboard-accessible tools that work your way.

**[Download Echoes Unseen 2.0](https://github.com/Quinn80/EchoesUnseen/releases/tag/2.0)** · Windows 10/11, 64-bit · Public beta · No website account required

![Echoes Unseen’s green launcher menu open over Guild Wars 2 gameplay.](https://github.com/Quinn80/EchoesUnseen/releases/download/2.0/Echoes-Unseen-V2-in-game-burnt.png)

*In-game screenshot provided by Quinn; decorative burnt edges and a soft left-edge fade have been applied.*

## Start small

1. Download **Echoes.Unseen.exe** from the [2.0 release](https://github.com/Quinn80/EchoesUnseen/releases/tag/2.0). It is one self-contained executable; no separate .NET installation is required.
2. Open Guild Wars 2, then launch Echoes Unseen normally. Close an older running copy first and back up your settings before upgrading.
3. Click the dragon or press **Alt+Down**, choose a comfortable voice and text size in Settings, and try one tool at a time.

The file is unsigned. Windows SmartScreen may warn; verify the official release and its SHA-256 rather than disabling Windows security. Storage for voices, recognition models and packs is additional to the executable download.

## What you can do

- **Read supported game text:** hover over items, player names and interface rows. The default dwell is 400 ms; OCR and speech take additional processing time.
- **Display creator trails:** themed Ribbon + Arrows or Arrows Only, galaxy effects and configurable objective markers. Creator paths and section gaps are preserved.
- **Find account items:** search supported storage and character items and see their locations, with Build & Gear and Trading Post tools alongside. Account data requires a GW2 API key.
- **Communicate:** hear chat-channel announcements and optional whisper cues; dictate locally with Whisper through a movable recorder with Copy, Read and Delete controls.
- **Learn music manually:** ten aligned lanes, five themes and four pacing modes. Timed songs have falling cues; untimed sheets stay static. Autoplay and AutoHotkey have been removed.
- **Choose your presentation:** text and interface size, contrast, motion, Standard/Clarity choices and voice preferences. Cori is the default app voice. NVDA routing is optional.
- **Keep information close:** Heart Quests, Map Completion, Event Timers, Wizard’s Vault, Wiki search and possible rift-site markers.

[Read about the app](https://echoesoftyria.com/echoes-unseen) · [Full 2.0 release notes](https://github.com/Quinn80/EchoesUnseen/releases/tag/2.0)

## Community creators and contributors

**Lady Elyssa** contributes trail and achievement guide content and valuable direction for the project.

**[Tekkit’s Workshop](https://www.tekkitsworkshop.net/)** creates the All-In-One marker and trail pack, used and redistributed with permission.

TacO and Blish HUD are separate overlay applications that read compatible packs; they are not the creators of these packs. Thank you to every creator, tester and community member whose feedback helps Echoes improve.

## In loving memory of J.W.

<picture>
  <source media="(prefers-reduced-motion: reduce)" srcset="https://github.com/Quinn80/EchoesUnseen/releases/download/2.0/memorial-phoenix.webp">
  <img src="https://github.com/Quinn80/EchoesUnseen/releases/download/2.0/jw-phoenix-memory.gif" width="420" alt="An ivory and lavender phoenix rising with both wings outstretched inside an ornate gold border, with falling embers.">
</picture>

My older brother passed away shortly before Echoes Unseen’s first launch. Through grief, I kept creating—and this project became a way to carry his memory forward while building something for the community.

*A phoenix from the ashes. For him, and for everyone finding their own way to play.*

— Quinn

[View the website memorial with animation controls](https://echoesoftyria.com/echoes-unseen#jw-dedication) · [View still artwork](https://github.com/Quinn80/EchoesUnseen/releases/download/2.0/memorial-phoenix.webp)

## Beta limits and feedback

OCR can misread or miss text, and wrapped chat or whispers may still be incomplete. Trails follow creator data; they do not calculate safe terrain routes. Possible rift sites are not active rifts. The Heart Quests area preview is inside the panel, not a ground-following world boundary. Some music is an excerpt.

A MumbleLink startup failure was observed in a DEV run of the same source. The public executable worked in Quinn’s test; recovery improvements remain in separate DEV work. If repeated error dialogs occur, close Echoes with **Ctrl+F12** and report what happened.

Story Guide and scanner-based rift assistance are **not included in the public 2.0 build**. They remain in development, alongside the planned .NET 10 migration and further communication improvements.

[Open an issue](https://github.com/Quinn80/EchoesUnseen/issues) or email **Echoes.Unseen@pm.me**. Review recordings before sharing them: reports may show visible chat and other screen content.

## Technical details and privacy

Native C# / WPF / .NET 8, self-contained Windows x64, with Direct3D 11 trail rendering and MumbleLink position data. OCR, Piper speech and Whisper dictation run locally. Optional cloud speech sends text to the selected provider; API lookups, Wiki searches, pack updates, model downloads and update checks use the network. Keep your API key private.

Executable: **build20261018-1733**, local source checkpoint **50c5384**, **201,075,446 bytes**.

SHA-256: `7D45B98B2B5950CADD8CB23F685886C310271B09BCA30203C250AABD430C27EF`

This repository’s current public source snapshot is older than the 2.0 executable. The release tag and automatic source archives do not represent publication of the local 50c5384 development checkpoint. Source synchronization is separate from this binary release; do not expect the current public checkout to build the new executable.

See [LICENSE](LICENSE) and [third-party notices](THIRD-PARTY-NOTICES.md). Contributions and reproducible bug reports are welcome. Accessibility features are free; [optional donations](https://echoesoftyria.com/donate) support continued work.

Echoes Unseen is independent and is not affiliated with or endorsed by ArenaNet or NCSOFT. Guild Wars 2 belongs to its respective owners.
