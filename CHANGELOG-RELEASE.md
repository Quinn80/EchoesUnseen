# Echoes Unseen — release changelog

**Comparison point:** the last public release, **b1.4** (published 2026-07-30 23:57 UTC on
`github.com/Quinn80/EchoesUnseen`, asset `EchoesUnseen-b1.4.exe`, a full release, not a
prerelease). The repository carries no release tags, so the comparison was made against the
last commit before that publication — `bb8cbd9` (2026-07-30 18:10 UTC, "Guitar-Hero guide
overlay, grown-up song library, all instruments, tweaks"). Everything below is what changed
between that commit and `e6bf016` (2026-09-17): **120 commits, 114 files, ~20,000 lines added.**

| | |
|---|---|
| version in code | `1.5.0` (`AssemblyVersion` / `FileVersion` `1.5.0.0`) |
| version shown on the About tab | `1.5b` |
| release version / filename | **not yet decided — see RELEASE-SUMMARY-FOR-QUINN.md** |
| build stamp | stamped automatically at publish time as `build<yyyyMMdd-HHmm>` (UTC) |
| distribution build for this release | **not built yet** — this document describes the verified development source |

Every entry below was checked against the current source, not against memory. Where a feature
is behind a switch or still beta, it says so.

---

## Hover Reader

The largest change in this release. The hover reader is what speaks the thing the mouse is
resting on inside Guild Wars 2.

**The principle: point at one thing, hear that one thing.**

- **A new targeting system (beta), built on OpenCV and RapidOCR.** Instead of reading whatever
  text lies nearest the pointer, it works out which interface object the pointer is inside, then
  reads that object and its own tooltip and price. It covers: inventory slots, merchant rows,
  Wizard's Vault reward cards, Trading Post rows, Gem Store cards and banners, buttons, NPC
  dialog choices, menu rows, tab and icon labels, and world nameplates.
- **The old ("classic") reader is still there** and remains selectable in Settings.
- **RapidOCR is the hover reader's recognition engine**, with Windows OCR as the fallback if the
  models cannot load. Chosen on measured evidence against real captures (`RAPIDOCR-EVALUATION.md`):
  it found about half again as many lines as Windows OCR with the lowest junk rate of the three
  engines tried.
- **Prices are read as money.** Coin colours are used to tell gold from silver from copper;
  gems and Astral Acclaim are recognised by the icon beside the figure. Figures that cannot be
  coins (silver and copper never reach 100) are read as plain figures — "Price: 175 plus 250" —
  rather than named as a currency they are not.
- **A tooltip belongs to an object or is not read.** A tooltip that names a different item is not
  attached to the thing under the pointer.
- **Resting still says it once.** Moving inside the same object stays quiet, unless something new
  is now on screen — the tooltip appeared, or a price the tooltip had been covering is now visible.
- **Translucent tooltips.** Where a tooltip is drawn over a list or a card, the text showing
  through it is separated out three ways: by the tooltip's own drawn frame, by lettering
  brightness (text behind the panel is a fraction as bright, judged character by character), and,
  in the Gem Store, by the end of the tooltip's side border.
- **Read while models load.** The recognition models load once in the background, and a hover
  that is cancelled while they load no longer disables the reader for the rest of the session.
- **A stale answer is never spoken.** Each new hover cancels the one before it, so the reader
  never announces what the pointer was on half a second ago.
- **Screen coordinates are right on any display.** Captures are taken in physical pixels and
  clamped to the monitor the pointer is on, which fixes reading the wrong area on a scaled
  display (125% on a 2560×1440 screen previously made the outer fifth of the screen unreadable).

### How it is judged
- **An offline test harness (`tools/HoverReplay`) replays recorded hovers** — real screenshots
  with the real pointer position from the diagnostics log — and scores what the reader would say.
  The current corpus holds 235 cases and 26 multi-step sequences from 27 recorded sessions.
- **Current score: 214 of 234 cases pass** (198 of 214 with a logged, real pointer), **22 of 26
  sequences**, with no case failing that passed at the accepted baseline.
- **Typical hover: about half a second** (median 506 ms, 95th percentile 652 ms end to end,
  measured on recorded frames).

## Screen Reader

- Screen captures are taken in physical pixels and clamped to the monitor under the pointer, so
  reading is correct on a scaled or multi-monitor desktop.
- `Ctrl+Shift+X` captures the screen **including** the Echoes Unseen overlay, for reporting
  problems with the overlay itself.

## Chat Reader

- Messages are read whole, as `[tag] name: message`, instead of in fragments.
- Repeats are suppressed by speaker and opening words; OCR garbage tails are trimmed; guild tags
  are stripped from names; system spam is filtered out.
- The chat panel's position is remembered, and the hover reader no longer reads the chat box.
- Tesseract is available as an optional, higher-accuracy chat engine (Windows OCR is the
  default); its recognition was tuned with engine-aware binarisation and a character whitelist.
- Chat reading and hover reading no longer block each other.

## Screen reader coexistence, focus and quiet

- **Speech can be routed through NVDA** instead of the app's own voice, so one voice reads
  everything.
- **The readers only speak while Guild Wars 2 has focus**, so the app is quiet while you work on
  the desktop.
- **Quiet Mode (`Ctrl+Shift+Z`)** hushes the readers — including hover reading — without turning
  any feature off.

## Trail Navigator / Navigation

- **Marker packs.** TacO / BlishHUD packs (`.taco`, `.xml`, `.trl`) can be imported and used for
  audio navigation towards achievement, collection and map-completion points. No pack is bundled
  or redistributed; you import your own copies.
- **Painted trails on the ground.** Real `.trl` trails are drawn in 3-D, laid flat on the ground,
  with world-space width, distance fade, and only the stretch near you drawn.
- **Spoken turn-by-turn guidance** ("the sightless compass"): direction and distance spoken as you
  move, with a stereo drift cue when you leave the path, and On / Near / Off states with
  hysteresis so it does not flap.
- **Guides tab**: guides organised by area and topic with readable names, large high-contrast
  text, a route to the start of a trail, and a waypoint for each guide.
- **A visible marker overlay** for sighted or partially sighted use: markers and trails projected
  onto the screen, with 20 colours and 20 animations.
- **Waypoint auto-clipper (`Ctrl+Shift+W`)** copies a waypoint's chat link so you can travel
  without using the map.
- **Read my current objective (`F8`)** speaks the current objective text.
- **Guidance diagnostics**: `GUIDE` and `TRAILDRAW` log lines and a spoken self-check
  (`Ctrl+Shift+D`), so "not quite right" becomes a measurable number.
- The on-screen compass was removed; guidance was reduced to one guide, one switch, one voice.

## Wizard's Vault

- A **Wizard's Vault panel** on the wheel, reading the season's objectives and rewards from the
  official Guild Wars 2 API.
- The hover reader can read a **reward card as one object** — name, description, Astral Acclaim
  price and availability — including when the pointer is on the card's artwork.
- Card names are matched against the season's real reward names from the API, so a misread
  caption is corrected to the listed name.

## Trading Post

- The hover reader reads **one row as one object**: name, its tooltip, and its own price in gold,
  silver and copper.
- The Level column is never mistaken for a price.
- Category rows (Armor, Weapons, Skins…) are read one at a time instead of as a list.

## Merchant accessibility

- **One horizontal merchant row at a time**: its name, its own tooltip and its own price.
- An equipped item's "Currently Equipped" comparison tooltip is not read as part of the row, and
  no longer cuts the row short of its price.
- Vendors whose prices are not coins (WvW claim tickets, memories of battle, gathering-tool
  charges) have their figures read as figures.

## Inventory accessibility

- **One inventory slot and its own dynamically placed tooltip**, wherever the game draws it —
  above, below, left or right of the pointer, over the neighbouring slots.
- Stack counts are read ("6 in stack"), and equipment tooltips read their stats.
- Moving from slot to slot announces each new item; resting inside one stays quiet.

## Dialogs and buttons

- **NPC dialog choices** are recognised as choices and read one at a time, ending with
  "Dialog option."
- **Buttons** are recognised by the whole button box, not by hitting the small label, and are read
  as "Name. Button." This includes dimmed buttons in modal dialogs.
- A menu opened under the pointer (for example the Gem Store's Style drop-down) is no longer
  mistaken for a tooltip.

## Gem Store accessibility

- **A product card is read whole**: name, description, gem price, and sale information where it is
  shown ("Normally 500 gems").
- The "View Details" label that follows the pointer is never read instead of the card's name.
- Banner slides in the carousel are read as one banner across all their lines.

## Event timers and WvW

- **Event Timers** joined the wheel, with a meta-event alarm.
- **WvW**: Righteous Indignation countdown with a shield cue, approach alerts for objectives,
  announcements for your own map's captures with compass direction and "we / enemy" phrasing,
  a manual team override, and less supply spam.
- **Interactive-item proximity alerts** (chest, node, collectible) with a chime and a whisper.
- **Bag summary (`Ctrl+Shift+B`)** and **wallet (`Ctrl+Shift+M`)** hotkeys.
- A **forward-hum audio puck** for orientation.

## Music Player

- **Import your own music**: AutoHotkey (`.ahk`), MIDI (`.mid`) and text note files, one at a time
  or in batches, with exact-millisecond timing preserved and duplicate imports prevented.
- **Full three-octave Guild Wars 2 instrument support** (keys 1–8 with 9/0 for octave), including
  Piano and Verdarach, and rich community notation (chords, octaves, runs, pauses).
- **Auto-play** uses the bundled AutoHotkey engine and hardware scan codes, so Guild Wars 2
  actually registers the notes; it can relaunch itself elevated when Windows blocks input.
- **Practice guide** with a Guitar-Hero-style note overlay; Step mode (waits for each note) is the
  default, with a wider tempo range and a larger, raised overlay.
- **Song search** browses the community song library on the Guild Wars 2 Wiki and adds songs to
  your library; 28 public-domain songs are bundled (Greensleeves, Ode to Joy, Canon in D and so on).
- Song names read correctly with a screen reader.

## Voice / TTS

- **Windows Natural** (local neural) joined Piper, Windows Speech (SAPI) and the optional
  ElevenLabs cloud voice.
- **Speech runs in two lanes**, so a long read no longer cuts off a short announcement.
- Speech speed goes up to 4×.
- Speech can be handed to **NVDA** instead.

## UI and Settings

- The wheel now carries **fourteen tools** — Screen Reader, Heart Quests, Sonar Trails, Chat
  Reader, Voice to Chat, Music Player, Oracle, Account Search, Trading Post, Build & Gear, Map
  Completion, Event Timers, Wizard's Vault, Settings — and any of them can be hidden from the ring.
- Settings tabs: Voice, API Keys, HUD, Features, Keybinds, About.
- New feature switches include hover reading of game items and the new hover targeting.
- Rebindable hotkeys, including read-under-cursor, quiet mode, objective, guide direction,
  waypoint copy, next events, bag summary, wallet, bug recording and quit.

## Performance

- The recognition models load once in the background, warmed up when the app starts (and when the
  targeting switch is turned on), so the first hover does not pay for it.
- One persistent audio device for earcons, which stopped the volume bouncing on hover.
- The hover reader no longer waits behind the chat reader's queue.
- A typical hover through the new targeting takes about half a second end to end.

## Bug reporting / diagnostics

- **Bug recorder (`F9`)**: records up to two minutes of the game at 2 frames per second, plus the
  last few full-size hover captures, tooltip captures, the diagnostics log tail, the crash log
  tail, and a `report.txt`. Everything is zipped into your Downloads folder. Only the newest three
  reports are kept.
- **`report.txt` contains no API keys, account names or email addresses**, and says so on the page.
- It now also records the **build stamp**, which hover reader was active, and how many hovers each
  reader answered, including any fallbacks and the reason.
- Every hover logs which reader answered it (`TARGETING=new`, `TARGETING=classic`, or
  `TARGETING=new -> classic fallback` with a reason).
- **Send feedback / crash report** button (end-user builds only) posts a report to the project's
  Discord channel — only when you press it.
- App-wide diagnostic logging, a build stamp in every session header, and `Ctrl+Shift+X` to
  capture the screen including the overlay.

## Privacy

- **Hover and screen reading happen on this machine.** RapidOCR and OpenCV run locally; no
  screenshot is sent anywhere by the readers.
- **The local-AI (Ollama) integration was removed.** The app contains no generative-AI chatbot and
  no cloud analysis of your game.
- **What does use the network**, all of it either part of the game's own public services or
  started by you: the official Guild Wars 2 API (your key, for account features), the Guild Wars 2
  Wiki (Oracle searches and the community song library), an update check against this project's
  GitHub releases, one-time downloads of voices and optional models (Piper, Whisper, Tesseract
  language data), ElevenLabs if you switch it on with your own key, and a bug report **when you
  press the button**.
- A bug report you choose to send contains screen recordings, screenshots and logs of your own
  session — that is the point of it — so it is always your decision.

## Dependencies / third-party components

Added this release: **OpenCvSharp4 and its official slim Windows runtime 4.13.0.20260627**
(Apache-2.0), **RapidOcrNet 4.2.0** (Apache-2.0) with the **PP-OCRv5** mobile models (Apache-2.0),
and their dependencies **ONNX Runtime 1.29.0** (MIT), **SkiaSharp 3.119.1** (MIT) and **Clipper2
2.0.0** (Boost Software License 1.0). **AutoHotkey** (GPL-2.0) is bundled and run as a separate
process for music auto-play. See `THIRD-PARTY-NOTICES-UPDATE.md` for the full audited list.

## Bug fixes

- The hover reader no longer reads the chat panel, the scenery, or several menu entries at once.
- A cancelled hover during model loading no longer disables the new reader for the session.
- The chat reader no longer loops, spells names out, or repeats a message with a different tail.
- The chat reader was sizing the hover reader's capture; each now sizes its own.
- Split text lines are put back together instead of being read as fragments.
- Six sessions in one log file were being read as one when analysing reports.
- The compass pointed backwards (one coordinate system is now used everywhere).
- Trails vanished, stood up in the air, or shouted from far away; fragments are merged, the ribbon
  is mitered and clipped, and distance fading works like TacO's.
- Audio: the on-hover volume bounce and the earcon device leak.
- Speech death after long sessions, and one reader cutting off another.
- The combat alert (a random full-volume blast) was removed entirely.
- Money was read from the screen when the account API already knew it.
- Guild tags, the chat voice and the wheel were being thrown away by an over-eager filter.

## Known limitations

The new hover targeting is **beta**. It is measured on 235 recorded cases and it does not read
every Guild Wars 2 interface perfectly.

- **See-through tooltips** over some Wizard's Vault and Gem Store cards can still mix a neighbour's
  caption into what is read.
- **Spelling slips**: the recogniser occasionally misreads letters ("Thumphant Hero's Faceguard").
- **Row boundaries**: a pointer exactly on the line between two Trading Post rows can read the
  other row.
- **Unusual currencies** (WvW claim tickets, gathering-tool charges) are read as figures without
  being named.
- **Modal dialogs**: with the Vault's purchase dialog open, a pointer just outside it can read the
  dimmed card behind it.
- **Labels that follow the pointer**: the Gem Store's "View Details" can cover part of a card's
  name, and what is covered cannot be read.
- Interfaces not represented in the test corpus may behave differently from the ones that are.
