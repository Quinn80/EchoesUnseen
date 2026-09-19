# Echoes Unseen — release changelog

# Echoes Unseen 1.6B

Compared against the **public b1.5** release, commit `e8fd494`. Seven commits, 52 files,
about 6,700 lines added. Every entry below was checked against the source and the tests at
commit `bee0879`; nothing is written from memory, and nothing that is only a hook for future
work is described as a feature.

| | |
|---|---|
| version in code | `1.6.0` (`AssemblyVersion` / `FileVersion` `1.6.0.0`) |
| version shown on the About tab | `1.6B` |
| release version / filename | **1.6B** / `EchoesUnseen-1.6B.exe` |
| build stamp | `build20260918-1739` (UTC) |
| commit | `bee0879` |
| distribution build | Release, win-x64, self-contained, single file, compressed, `-p:Distribution=true`; 138.4 MB; SHA-256 `179B445ADF7F7EC97892BF2A71371B2E38E93607201FB3B812B2BF4EE04FB0EC` |
| support address | `Echoes.Unseen@pm.me` |
| hover reader default | **Enhanced Hover Targeting (OpenCV + RapidOCR)**; Classic remains in Settings |

---
## Vision Accessibility Suite

A new **Accessibility** tab in Settings, between Features and About, holding the global
visual preferences the whole app respects. It is organised by what somebody needs — larger,
higher contrast, less motion, stronger focus, where on the screen — and never by a diagnosis.
There is no glaucoma mode and no macular-degeneration mode: two people with the same
diagnosis often need opposite things.

**Quick setup** offers seven starting points, each with a line of plain language: Standard,
Low Vision, High Contrast, Color Vision, Eye Comfort, Screen Reader First, Custom. A profile
is a **starting point, not a lock** — changing any single setting afterwards moves the profile
to Custom and keeps the change. Choosing a profile is entirely optional.

Six collapsible groups, two open by default:

- **Readability and size** — interface scale (0.85–1.60, applied to panels, control heights,
  padding, switches, tabs and fields, and to the wheel when its own scale is untouched), text
  size, bold text, larger controls, stronger borders, tooltip size, and how large long lists
  are drawn.
- **Colour and contrast** — colour-vision palettes for protanopia, deuteranopia and
  tritanopia; high contrast; reduce transparency; solid backgrounds behind text; and a choice
  of accent colour (seven measured options, or your own with a plain-language readability
  check). These change Echoes Unseen's own colours. They are never a filter over Guild Wars 2.
- **Motion and eye comfort** — reduce motion, stop pulsing indicators, reduce glow, stop
  decorative animation, reduce flashing, dim bright effects.
- **Focus and visibility** — focus indicator strength (Standard, Strong, Extra strong; the
  ring is white inside black at every strength, never a hue), stronger marks on selected
  items, simplified interface, hide decorative effects.
- **Preferred viewing area** — where on the screen accessibility messages are easiest to see,
  stored as fractions of the screen so it survives a change of monitor. **Stored only**: it
  has a live API and nothing consumes it yet. It is not a feature you can see working.
- **Advanced** — a custom accent colour with a readability check, and a custom viewing area
  as four fractions.

Also: **Reset accessibility settings** with confirmation, a reset for each group, and every
control has an accessible name, a spoken confirmation when it changes, and a plain-language
explanation. Collapsible groups announce expanded and collapsed.

**Nothing was duplicated.** Text size, High Contrast and the old "Access Mode" **moved** here
from the HUD tab; an automated check fails if any control ever appears on two tabs.

## Colour vision and contrast

An audit of every theme and control, measured rather than judged by eye: WCAG contrast and
CIEDE2000 distance, each re-computed as protanopia, deuteranopia and tritanopia receive them.
95 failing checks became 0.

- **The keyboard focus ring was invisible on filled buttons.** It was drawn in the theme's
  accent, and primary buttons are filled with that accent: 1:1 contrast in all seven themes.
  It is now a white ring inside a black halo — a brightness edge, not a hue — 4.9:1 to 14.1:1
  worst case, and visible to every kind of colour vision.
- **Tooltips rendered empty.** There was no tooltip style, so they used WPF's pale card while
  the app-wide text style painted their text white. Every tooltip in the app was affected,
  including the wheel's own labels. Fixed, and tooltips now stay on screen for 30 seconds
  rather than 5.
- **The row an open dropdown was set to** was a 1.5:1 shade. It now carries a tick and bold
  text — 9.9:1 in every theme.
- **Hovering or focusing a dropdown did nothing at all**: both triggers set the border to the
  colour it already was.
- **The selected tab** gained a bar along its top edge and a bold label, so selection is never
  a fill alone.
- **Three colours that carried information were changed** because they collapsed for a red- or
  green-blind eye: hero points vs waypoints (CIEDE2000 1.2 apart under deuteranopia — the same
  colour), Trading Post buy vs sell, and the error red, which missed the contrast minimum
  under protanopia.
- **Checkbox, tab and dropdown states** all carry a shape as well as a colour.

## Screen reader and keyboard

- **Twenty controls had no accessible name** — nine sliders, five icon-only refresh buttons,
  two search boxes and three dropdowns. All named, with help text where the control needs
  explaining.
- Every accessibility change is **spoken** as it happens and written to the status line as a
  live region.
- Collapsible groups report expanded and collapsed, and their open state is also a different
  glyph rather than only a shade.
- Tab order follows the visual order down the page.

## Hover Reader — Account Vault

With the Account Vault open, hovers over item slots were being named **"Bank Tab 1"** or
**"Bank Tab 2"**, sometimes with a price that belonged to nothing. In Quinn's 17 September
session six of eight bank hovers failed this way. Each time the item's own tooltip had
already been found and read — "13 Experience Boosters", "16 Black Lion Statuettes", "250
Empyreal Fragments" — and was then discarded, correctly, because it is not about a bank tab.
The target was wrong, not the tooltip.

Two causes, two narrow fixes:

- The window read as **inventory**, because the Inventory heading was nearer than the vault's
  own. "Account Vault" (or a bank tab on screen) now makes the context **bank**, and a bank
  slot is treated as an inventory slot: same one-object contract, same stack count.
- The tab strip is a list, and a list row's band runs the width of the panel — so a pointer on
  an item slot hundreds of pixels away was "inside" a tab's row. **A bank tab now wins only
  when the pointer is on a tab and no other object's tooltip is anchored at the pointer.**

Nothing else about targeting changed: no new rule about rows, distances or tooltips in
general. The corpus confirms it — 222 of 242, zero regressions, inventory 33/33, identical to
the run before the change. Eight of those cases are the Account Vault and Guild Bank
recordings added for this release; every one of them passes.

## UI and Settings

- Settings tab order is now **Voice · API Keys · HUD · Keybinds · Features · Accessibility ·
  About**.
- **Exit Echoes Unseen** — a visible way to quit, in the Settings footer on every tab, with a
  confirmation. The overlay has no title bar and does not take focus, so Alt+F4 goes to the
  game; the only way out used to be the Ctrl+Shift+Q hotkey.
- The About tab now shows the support address in full, with its own screen-reader wording.
- Panels respect the accessibility settings live: interface scale, reduced glow and stopped
  decoration take effect on the panel already open, not at the next launch.

## Diagnostics and support

- The support address is **`Echoes.Unseen@pm.me`** — in the feedback button, the About tab,
  the README and the release notes. Without a Discord webhook the feedback button saves the
  report to the Desktop, copies it to the clipboard and opens the user's mail app addressed
  there; they press Send.
- Version strings are consistent: assembly `1.6.0.0`, About `1.6B`, and every diagnostic
  header and bug report carries `v1.6.0.0 build20260918-1739`.

## Testing

- **`tools/Accessibility/`** — 807 colour and structure checks (WCAG contrast and CIEDE2000
  under all three colour-vision simulations, accessible names, colour-only state detection,
  tab order, no duplicate controls), plus a renderer that draws the app's real controls and a
  simulator that shows them as each eye receives them.
- **`tools/Accessibility/SuiteCheck`** — 110 behaviour checks for the accessibility settings,
  including migration from a b1.5 file and drawing the real Settings panel.
- **`HoverReplay selftest`** — the Account Vault rule, checked against the real pointers and
  tooltips from the session that produced the bug. 16 of 16.

## Privacy and local processing

Unchanged, and still accurate: screen reading and speech run on this computer. OpenCV finds
the object, RapidOCR reads it, Echoes Unseen speaks it. The colour-vision settings change the
app's own colours and never filter the game. What uses the network is unchanged: the official
Guild Wars 2 API with your key, the wiki, the version check, one-time voice and model
downloads, ElevenLabs only if you switch it on, and a bug report only when you send one.

## Known issues

See `KNOWN-ISSUES.md`.

---

# Echoes Unseen b1.5

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
| release version / filename | **b1.5** / `EchoesUnseen-b1.5.exe` |
| build stamp | `build20260918-1235` (UTC) |
| commit | `c762778` |
| distribution build | Release, win-x64, self-contained, single file, compressed, `-p:Distribution=true`; 138.4 MB; SHA-256 `742220F9F248CB3C8F2C40325B69C9A51D194A33503220FD1702D9E1053286EA` |
| support address | `Echoes.Unseen@pm.me` |
| hover reader default | **Enhanced Hover Targeting (OpenCV + RapidOCR)**; Classic remains in Settings |

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
- **On by default in b1.5.** The switch is *Settings → Features → "Enhanced Hover Targeting - OpenCV plus RapidOCR (recommended)"*. Turning it off returns to **Classic Hover Targeting**, which reads the text nearest the pointer.
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
  At b1.5 the corpus held 235 cases and 26 multi-step sequences from 27 recorded sessions.
- **Score at b1.5: 214 of 234 cases pass** (198 of 214 with a logged, real pointer), **22 of 26
  sequences**, with no case failing that passed at the accepted baseline. (1.6B adds the
  Account Vault recordings and scores 222 of 242; see the 1.6B section above.)
- **Typical hover: about half a second** (median 506 ms, 95th percentile 652 ms end to end,
  measured on recorded frames).

## Accessibility — the Vision Accessibility Suite

A new **Accessibility** tab in Settings, between Features and About, holding the global visual
preferences the whole app respects. It is organised by what somebody needs — larger, higher
contrast, less motion, stronger focus, where on the screen — and never by a diagnosis.

- **Quick setup** offers seven starting points: Standard, Low Vision, High Contrast, Color
  Vision, Eye Comfort, Screen Reader First, Custom. Each is a **starting point, not a lock**:
  changing any single setting afterwards moves the profile to Custom and keeps the change.
- **Readability and size** — interface scale (0.85–1.60, applied to panels, control heights,
  padding, switches, tabs and fields, and to the wheel when its own scale is untouched), text
  size, bold text, larger controls, stronger borders, tooltip size, and how large long lists are
  drawn.
- **Colour and contrast** — colour-vision palettes for protanopia, deuteranopia and tritanopia,
  high contrast, reduce transparency, solid backgrounds behind text, and a choice of accent
  colour (seven measured options, or your own with a plain-language readability check). These
  change Echoes Unseen's own colours; they are never a filter over Guild Wars 2.
- **Motion and eye comfort** — reduce motion, stop pulsing, reduce glow, stop decorative
  animation, reduce flashing, dim bright effects.
- **Focus and visibility** — focus indicator strength (the ring is white inside black at every
  strength, never a hue), stronger marks on selected items, simplified interface, hide
  decorative effects.
- **Preferred viewing area** — where on the screen accessibility messages are easiest to see,
  stored as fractions so it survives a change of monitor. *Nothing consumes it yet*: it is a
  stored preference with a live API for the Story Guide and the Music Guide.
- **Reset accessibility settings**, and a reset for each group, touching nothing outside the tab.
- Every control has an accessible name, a spoken confirmation when it changes, and a
  plain-language explanation. Collapsible groups announce expanded and collapsed.
- Text size, High Contrast and the old "Access Mode" **moved** here from the HUD tab rather than
  being duplicated; an automated check fails if any control appears on two tabs.
- Defaults reproduce the previous build exactly, so upgrading changes nothing you did not ask
  for.

## Colour, contrast and non-colour state

An audit of every theme and control, measured rather than judged by eye — WCAG contrast and
CIEDE2000 distance, each re-computed as protanopia, deuteranopia and tritanopia receive it.

- **The keyboard focus ring was invisible on filled buttons** — it was drawn in the theme accent,
  and primary buttons are filled with that accent: 1:1 contrast in all seven themes. It is now a
  white ring inside a black halo, 4.9:1 to 14.1:1 worst case, and it carries no hue at all.
- **Tooltips rendered empty.** There was no tooltip style, so they used WPF's pale card while the
  app-wide text style painted their text white. Every tooltip in the app was affected. Fixed,
  and they now stay on screen for 30 seconds rather than 5.
- **The chosen row of an open dropdown** was a 1.5:1 shade; it now carries a tick and bold text.
- **Hovering or focusing a dropdown did nothing at all** — both triggers set the border to the
  colour it already was.
- **The selected tab** gained a bar along its top edge and a bold label, so selection is not a
  fill alone.
- **Twenty controls had no name for a screen reader** — nine sliders, five icon-only refresh
  buttons, two search boxes, three dropdowns.
- **There was no visible way to quit**: the overlay has no title bar, so Alt+F4 goes to the game.
  Settings now has an **Exit Echoes Unseen** button that confirms first.
- Colours that carried information were re-measured and three were changed: hero points vs
  waypoints (CIEDE2000 1.2 apart under deuteranopia — the same colour), Trading Post buy vs sell,
  and the error red, which missed the contrast minimum under protanopia.

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
- **Auto-play** is an optional instrument feature: it sends the imported song's instrument key
  presses through the bundled AutoHotkey engine using hardware scan codes, so Guild Wars 2 registers
  the notes. It does not control character movement, combat, story progression or dialog choices.
  It can relaunch itself elevated when Windows blocks input.
- **Practice guide** with a Guitar-Hero-style note overlay; Step mode (waits for each note) is the
  default, with a wider tempo range and a larger, raised overlay.
- **Song search** browses the community song library on the Guild Wars 2 Wiki and adds songs to
  your library; 28 public-domain songs are bundled (Greensleeves, Ode to Joy, Canon in D and so on).
- Song names read correctly with a screen reader.

## Voice / TTS

- **Windows Natural** (local neural) joined Piper, Windows Speech (SAPI) and the optional
  ElevenLabs cloud voice.
- **Speech-to-text, for Voice to Chat and the spoken wiki search:** the recognition built into
  Windows is the default; **Whisper** (local, `tiny.en` / `base.en` / `small.en`, downloaded once on
  first use) is the optional alternative chosen in Settings → Voice. Verified in this build:
  `SttEngine` defaults to `windows`, and Whisper runs only when selected.
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
process for music auto-play. **WebView2 is no longer used** — the HUD is native WPF — so it carries
no notice. `THIRD-PARTY-NOTICES.md` was updated for this release with Clipper2,
Microsoft.Extensions.AI.Abstractions and the exact versions that ship, and it is published with the
release.

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

The new hover targeting is on by default and is still **beta**. It is measured on 235 recorded
cases and it does not read every Guild Wars 2 interface perfectly. Classic Hover Targeting remains
available in Settings.

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
