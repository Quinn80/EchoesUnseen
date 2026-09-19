<p align="center">
  <img src="EchoesUnseen/Resources/Images/echoes-unseen-logo.png" alt="Echoes Unseen logo" width="220">
</p>

<h1 align="center">Echoes Unseen</h1>

<p align="center"><b>A free accessibility companion for Guild Wars 2.</b></p>

<p align="center">
  <img src="https://img.shields.io/badge/release-1.6B-1f6feb" alt="Release 1.6B">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6" alt="Windows 10/11">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/license-MIT-3fb950" alt="MIT License">
</p>

Echoes Unseen is a free accessibility companion for Guild Wars 2, built for blind and low-vision
players and designed for a broad range of visual access needs. It helps expose game information
through screen reading, the Hover Reader, speech, navigation assistance and customizable
accessibility settings.

It runs as a transparent, click-through overlay on top of the game. A radial wheel gives you its
tools, driven entirely by keyboard and voice — nothing requires seeing the screen — and everything
is spoken aloud by a natural voice running on your own computer.

<p align="center">
  <img src="docs/images/hud-wheel-b1.5.png" alt="The Echoes Unseen HUD: a ring of glowing tool icons around a compass rose and a blue dragon." width="360">
</p>

> **Current release: 1.6B** · Windows 10/11 (64-bit) · Free (donations welcome) ·
> **[⬇ Download](https://github.com/Quinn80/EchoesUnseen/releases/latest)**

---

## What Echoes Unseen Does

- **Reads what you point at** — rest the mouse on something in the game and hear *that one thing*:
  an inventory item, a merchant row with its own price, a Wizard's Vault card, a bank slot.
- **Reads the screen and the chat** — anything on screen can be spoken, including whatever the
  mouse is resting on.
- **Speaks with a natural voice** — a local neural voice, or through NVDA if that is what you use.
- **Guides you around the map** — audio sonar, painted trails, and spoken turn-by-turn directions.
- **Lets you talk instead of type** — dictate into chat with local speech-to-text.
- **Plays music** — import songs and play Guild Wars 2 instruments, with a practice guide.
- **Adapts to how you see** — interface and text size, contrast, colour-vision palettes, motion,
  glow and focus strength. Set up in one choice, or tuned control by control.
- **Finds anything you own** — searches your bank, every character's bags and material storage
  through the official Guild Wars 2 API, and tells you exactly where something is.

**The tools on the wheel:** Screen Reader · Heart Quests · Sonar Trails · Chat Reader ·
Voice to Chat · Music Player · Oracle · Account Search · Trading Post · Build & Gear ·
Map Completion · Event Timers · Wizard's Vault · Settings. Any of them can be hidden from the ring
in **Settings → HUD**.

**Driving the wheel:** hold `Alt` and tap the arrow keys to move between tools — the voice names
each one — then `Alt+Enter` to open. It works while the game has focus. The wheel shrinks to just
its logo while you play and unfolds when you hover it.

---

## Vision Accessibility

Echoes Unseen 1.6B adds a dedicated **Accessibility** tab in Settings, designed for a broad range
of visual access needs, including blindness, low vision, colour-vision differences, reduced
contrast sensitivity, field-of-view limitations, light sensitivity and motion sensitivity.

These are interface options, not treatment, and they make no medical claims.

| group | what is in it |
|---|---|
| Quick setup | Standard · Low Vision · High Contrast · Color Vision · Eye Comfort · Screen Reader First · Custom |
| Readability and size | interface scale, text size, bold text, larger controls, stronger borders, tooltip size |
| Colour and contrast | protanopia / deuteranopia / tritanopia palettes, high contrast, reduce transparency, solid backgrounds behind text, accent colour |
| Motion and eye comfort | reduce motion, stop pulsing, reduce glow, stop decorative animation, reduce flashing, dim bright effects |
| Focus and visibility | focus indicator strength, stronger selection marks, simplified interface |
| Preferred viewing area | where on screen messages are easiest for you to see |

**Organised by need, not by diagnosis.** There is no glaucoma mode and no macular-degeneration
mode. Two people with the same diagnosis routinely need opposite things — one wants everything
larger, the other wants less motion and dimmer highlights — and nobody should have to describe
their eyes to a settings page to make text bigger. Every choice is phrased as a need: *larger*,
*higher contrast*, *less motion*, *stronger focus*, *where on the screen*.

**A profile is a starting point, not a lock.** Change any setting afterwards and it simply becomes
Custom, keeping your change. You never have to pick one at all.

**The colour settings change Echoes Unseen's own interface.** Nothing is filtered over Guild Wars 2.

**Preferred viewing area is stored, and nothing reads it yet.** It is groundwork for features still
to come — the Story Guide, the Music Guide and navigation overlays — and it is listed here as a
saved preference rather than as something that repositions anything today.

One rule is not a setting and cannot be switched off: **colour is never the only thing that tells
you something.** Every selected, checked, focused or unavailable state also carries a mark, a word
or a shape, and a screen reader is told the same thing the screen shows.

---

## Hover Reader

Echoes Unseen can read supported Guild Wars 2 interface objects individually, instead of simply
gathering the text nearest the pointer.

**Point at one thing. Hear that one thing.**

> **OpenCV finds the object → RapidOCR reads the text → Echoes Unseen speaks it.**

| You point at | You hear |
|---|---|
| an inventory slot | the item, its tooltip, how many are in the stack |
| a bank slot in the Account Vault | that item, its tooltip and its stack count — not the bank tab whose row happens to run behind it |
| a merchant row | that item, its description, its own price |
| a Wizard's Vault reward | the whole card: name, description, Astral Acclaim price, availability |
| a Trading Post row | the item and its price in gold, silver and copper |
| a Gem Store card | the product, its description, its gem price, and any sale price |
| a dialog choice | that one choice, ending "Dialog option." |
| a button | "Name. Button." — anywhere on the button, not just its label |
| an NPC | that NPC's name |

Core Hover Reader processing happens locally on your own PC. OpenCV works out the geometry of the
interface — where a card, row, tooltip or button begins and ends — and reads no text at all.
RapidOCR then recognises the text inside that object. RapidOCR is a local neural OCR model: it
turns pictures of text into text. It is not generative AI, and nothing analyses your gameplay.
**Windows OCR** remains the local fallback.

This is **Enhanced Hover Targeting**, on by default. **Classic Hover Targeting**, which reads the
text nearest the pointer, is still one switch away in **Settings → Features**.

It is **beta**. Every change is measured offline against a library of hovers recorded from real
sessions before it ships, and the current limits are listed in the
[release notes](https://github.com/Quinn80/EchoesUnseen/releases/latest).

---

## Navigation

- **Sonar Trails** — an audio sonar pings you toward your next waypoint, heart or point of
  interest, and tells you how far away it is and which way it lies.
- **Spoken turn-by-turn directions** — a sightless compass for getting somewhere without seeing
  the map.
- **Painted trails** — a route drawn on the ground for anyone who can see some of the screen.
- **Marker packs** — the Navigator reads the open TacO / BlishHUD format so you can import your
  own. **No marker packs ship with Echoes Unseen**, in this or any release.

---

## Screen Reader / Chat Reader

- **Screen Reader** — `Ctrl+Shift+Space` reads whatever is under the pointer: tooltips, menu
  buttons, list rows.
- **Chat Reader** — reads incoming chat aloud, and keeps working with its panel closed.
- **Plays nicely with NVDA** — route speech through NVDA, and the readers stay quiet unless Guild
  Wars 2 has focus. Quiet Mode hushes everything with one key.

---

## Voice and Speech

- **A natural local voice** — [Piper](https://github.com/rhasspy/piper) speaks on your own
  computer. 22 English voices to preview and download; six arrive automatically on first run.
- **Windows voices** are built in, and **ElevenLabs** is optional with your own key.
- **Voice to Chat** — dictate into chat. The default is the speech recognition built into Windows;
  Whisper is an optional local alternative. Either way the audio is processed on your PC.

---

## Music

- Import songs as number notation, ABC letters, MIDI or AutoHotkey files.
- Full three-octave instrument range, and auto-play for the Guild Wars 2 instruments.
- A practice guide that follows along.

---

## Privacy and Local Processing

**Core screen-reading and accessibility processing happens locally on your PC.** The Hover Reader,
the Screen Reader and the Chat Reader capture and recognise text on your own machine with OpenCV,
RapidOCR and Windows OCR, and Piper speaks it locally. Normal live screen reading does not upload
your screen anywhere. There is no telemetry and no account.

Some features do use the network:

- the **official Guild Wars 2 API**, with the key you supply, for account features
- the **Guild Wars 2 Wiki**, for Oracle searches and the community song library
- an **update check** against this repository's releases
- **one-time downloads** of voices and optional models (Piper, Whisper, Tesseract language data)
- **ElevenLabs**, only if you turn it on with your own key
- a **bug report**, only when you choose to send one

**Bug reports are always your choice.** Pressing the bug-record key (`F9`) saves a short screen
recording, recent screenshots and logs into your Downloads folder as a zip. The "Send feedback /
crash report" button in Settings then opens your own mail app addressed to `Echoes.Unseen@pm.me`
with the report saved ready to attach — you press Send. Reports contain no API keys, account names
or email addresses, but they do contain pictures of your own screen, which is what makes them
useful.

---

## Download

**[⬇ Download the latest release — `EchoesUnseen-1.6B.exe`](https://github.com/Quinn80/EchoesUnseen/releases/latest)**

It is **self-contained — no .NET install needed.** Download it, then run it. Windows 10/11
(64-bit), about 138 MB.

> On first launch, Windows SmartScreen may warn "unknown publisher" (it is an unsigned indie
> build). Click **More info → Run anyway**. On first run it also downloads six voices
> automatically.

Your settings, voices and songs live in `%APPDATA%\EchoesUnseen` and carry over between releases.

---

## No gameplay automation

Echoes Unseen does not play Guild Wars 2 and makes no decisions in it. It has no generative-AI
chatbot and does no cloud analysis of your game; the local neural models it uses turn pictures into
text and text into speech. You still move, choose, buy, fight and explore. Music auto-play is an
optional instrument feature that sends the imported song's instrument key presses; it does not
control character movement, combat, story progression or dialog choices.

---

## Technical Components

What each piece actually does in the app. Full versions and licence texts are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

| component | version | its role in Echoes Unseen |
|---|---|---|
| [OpenCV](https://opencv.org/) (via [OpenCvSharp](https://github.com/shimat/opencvsharp)) | 4.13.0 | Hover Reader interface-object geometry — where an object begins and ends |
| [RapidOCR](https://github.com/RapidAI/RapidOCR) (via [RapidOcrNet](https://github.com/BobLd/RapidOcrNet)) | 4.2.0 | local Hover Reader OCR, using the PP-OCRv5 models |
| [ONNX Runtime](https://onnxruntime.ai/) | 1.29.0 | local model runtime for those OCR models |
| **Windows.Media.Ocr** | built in | the Hover Reader's local OCR fallback |
| [Piper](https://github.com/rhasspy/piper) | — | local neural text-to-speech, downloaded on first use |
| [Tesseract](https://github.com/tesseract-ocr/tesseract) | 5.2.0 | optional OCR engine for the Chat Reader, selectable in Settings |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | 1.9.1 | optional local speech-to-text for Voice to Chat; Windows Speech is the default |
| [NAudio](https://github.com/naudio/NAudio) | 2.2.1 | audio playback, capture, and the sonar and earcon tones |
| [Clipper2](https://github.com/AngusJohnson/Clipper2) | 2.0.0 | polygon clipping for the trail renderer |
| [AutoHotkey](https://www.autohotkey.com/) | v2 | a separate process used for music auto-play |
| **NVDA Controller Client** | — | speaks through NVDA when you ask it to |
| **Guild Wars 2 API** | — | exact account data for the item finder, the Wizard's Vault and prices |
| **TacO / BlishHUD format** | — | read for navigation; no packs are bundled |

Built on **.NET 8 / WPF** as a native Windows overlay.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```bash
git clone <your-fork-url>
cd EchoesUnseen
dotnet build
dotnet run --project EchoesUnseen
```

To produce a self-contained single-file release exe (no .NET needed on the target machine):

```powershell
powershell -ExecutionPolicy Bypass -File publish-beta.ps1 -Version "1.6B"
```

The end-user build is published with `-p:Distribution=true` (it shows the feedback button).

**One binary is not in this repository:** `EchoesUnseen/Resources/Ahk/AutoHotkey64.exe`, the
unmodified [AutoHotkey](https://www.autohotkey.com/) v2 64-bit executable that the Music Player runs
as a separate process for auto-play. AutoHotkey is GPL-2.0, so it is not redistributed here.
Download it from the AutoHotkey site and place it there before building, or remove the
embedded-resource line from `EchoesUnseen.csproj` to build without auto-play.

## Known issues

The Hover Reader is beta, and the current known limits — translucent tooltips over some cards,
the occasional misspelling, a pointer exactly on a row boundary, unusual currencies read as plain
figures — are listed in full with each release. Two more are worth knowing in the Account Vault:
pointing at the *first* bank tab header reads the empty search box above it as part of the same
block, and an empty slot is silent rather than announced. See the
[latest release notes](https://github.com/Quinn80/EchoesUnseen/releases/latest).

## Support and feedback

**Echoes.Unseen@pm.me** — write any time: a problem, an idea, or something that reads wrongly.

If something is misread, press **F9** first. Echoes Unseen saves a short recording, some
screenshots and its logs as a zip in your Downloads folder; attaching that says exactly what
was on screen. Reports contain no API keys, account names or email addresses, but they do
contain pictures of your own screen, so sending one is always your choice.

## Contributing

Feedback and suggestions are genuinely welcome — especially from players who use screen readers.
Open an issue with what's confusing by ear, mis-read, or missing.

## Licences / Third-Party Notices

Echoes Unseen is released under the MIT License — see [LICENSE](LICENSE).

Full third-party component list, versions and licence texts:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Not affiliated with ArenaNet

Echoes Unseen is an unofficial accessibility companion for Guild Wars 2. It is not affiliated with,
endorsed by, sponsored by or approved by ArenaNet or NCSOFT. Guild Wars 2 and its assets are
© ArenaNet, LLC.
