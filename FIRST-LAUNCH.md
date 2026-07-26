# Echoes Unseen — First Launch Setup Guide

Welcome! This guide walks you through everything you need to do to get Echoes Unseen running for the first time. Take it one step at a time — there's no rush.

---

## What You're Setting Up

Echoes Unseen is a **native Windows desktop application** that runs as a transparent overlay on top of Guild Wars 2. It provides 12 accessibility panels (screen reader, chat reader, trail navigator, and more), 10 dark themes, and natural-sounding text-to-speech.

To run it, you need:

1. A Windows 10 or 11 computer
2. **Visual Studio 2022 Community** (free) to build and launch the app
3. **.NET 8 SDK** (Visual Studio installs this automatically)
4. The **Piper voice engine** files (downloaded by our installer script)
5. Optionally, a **Guild Wars 2 API key** (for API-based panels)

---

## Step 1: Extract the Zip

Extract `EchoesUnseen-WPF-Complete.zip` somewhere easy to find, like:

```
C:\Users\YourName\Documents\EchoesUnseen
```

Avoid extracting to OneDrive or Desktop — OneDrive can interfere with file access during builds.

---

## Step 2: Install Visual Studio 2022 Community

**Skip this step if you already have Visual Studio 2022.**

1. Download from: https://visualstudio.microsoft.com/vs/community/
2. Run the installer
3. When asked which workloads to install, check:
   - ☑ **.NET desktop development**
4. Click Install. This takes about 15 minutes.

That's it — this installs everything needed: the IDE, the .NET 8 SDK, and the WPF build tools.

---

## Step 3: Download the Piper Voice Engine

I built a one-click script that downloads everything you need. Here's how to run it:

1. Open the extracted folder (e.g. `Documents\EchoesUnseen`)
2. You'll see a file called `install-piper.ps1`
3. **Right-click it** and choose **"Run with PowerShell"**

PowerShell will open a window and show you progress as it downloads:
- `piper.exe` (~25 MB — the voice engine)
- `en_US-lessac-high.onnx` (~60 MB — the default natural voice)

When it's done, the window will say "✓ Setup complete!" and wait for you to press any key to close it.

### If Windows says "running scripts is disabled"

Don't worry — this is a one-time fix:

1. Search your Start menu for **"PowerShell"**
2. Right-click it → **"Run as Administrator"**
3. In the blue window that opens, paste this command and press Enter:
   ```
   Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
   ```
4. Type `Y` and press Enter to confirm
5. Close the admin PowerShell window
6. Go back to step 3 above and try running `install-piper.ps1` again

---

## Step 4: Open the Project in Visual Studio

1. In the extracted folder, **double-click `EchoesUnseen.sln`**
2. Visual Studio will open
3. Wait a moment — the first time it opens, it will download NuGet packages (NAudio, Whisper, etc.) automatically. You'll see progress in the bottom right of Visual Studio.
4. When it says "Ready" in the status bar, you're good to go.

---

## Step 5: Run the App

With the project open in Visual Studio, press **F5** (or click the green ▶ Play button at the top).

Visual Studio will compile the project and launch it. The first compile takes 20-30 seconds. After that, launches are instant.

### What you'll see

- Your entire screen stays looking normal (the app is transparent)
- A **pink radial dial of 12 buttons** appears somewhere on your screen at low opacity
- Hover over the dial to make it fully visible
- Click any button to open that panel
- Press **F1–F12** as keyboard shortcuts for the 12 panels
- Press **Escape** to close a panel or stop speech
- Press **Ctrl+Shift+H** if you ever lose the HUD off-screen — it will snap back to center

---

## Step 6: Test the Voice Engine

1. Click the **Settings** button on the HUD (gear icon at bottom, or press **F12**)
2. Go to the **Voice** tab
3. Click the **"🔊 Test Voice"** button

You should hear a natural voice say: *"Hello, Commander. This is how I will sound."*

If you hear nothing, check:
- Your speakers/headphones are on and not muted
- In Settings > Voice, the engine is set to **"Piper (Offline Neural) — Recommended"**
- The install-piper.ps1 script completed successfully

---

## Step 7: Try the Themes

1. Still in Settings, go to the **HUD** tab
2. Near the top, find the **Theme** dropdown
3. Pick any of the 10 themes — the whole app repaints instantly

Try a few to find your favorite. Your choice is saved automatically.

---

## Step 8 (Optional): Add Your GW2 API Key

Several panels (Build & Gear, Account Search, Trading Post, Heart Quests, Trail Navigator) can use your Guild Wars 2 account data. To enable this:

1. Go to https://account.arena.net/applications
2. Sign in with your GW2 account
3. Click "New Key"
4. Check these scopes: **account**, **characters**, **inventories**, **tradingpost**, **wallet**
5. Copy the key it gives you
6. Back in Echoes Unseen, open Settings > **API Keys** tab
7. Paste your key into the "Guild Wars 2 API Key" box

The key is encrypted at rest using Windows DPAPI, so only your Windows user account can read it.

---

## What to Test First

Once it's running, here are the quickest wins to confirm everything works:

1. **HUD drag**: Hold Shift and drag the pink ring around. It should stay fully on screen (won't let you drop it half off-screen).
2. **HUD rescue**: Press Ctrl+Shift+H. The ring should snap to screen center.
3. **Theme switching**: Settings > HUD > Theme. Pick Matrix. Then Cyberpunk. Then back to Hot Pink.
4. **Voice Assistant (F7)**: Type "Charr" and press Enter. It should find the Charr page, not some random article that mentions charr. (This is the wrong-subject bug fix from the old version!)
5. **Screen Reader (F1)**: Click "Select Area", drag a rectangle over any text on your screen. It should read the text aloud.
6. **Trail Navigator (F3)**: With GW2 running and your character loaded into a map, this should show your current map name and nearby objectives.

---

## If Something Goes Wrong

**Check the crash log.** Any errors are written to:

```
%APPDATA%\EchoesUnseen\crash.log
```

(Paste that into File Explorer's address bar to open it.)

If something breaks that you can't figure out, copy the last few lines of `crash.log` and share them with me. I can almost always diagnose from there.

---

## Questions I Can Help With Later

- Adjusting any theme's colors if something feels off
- Adding more themes (GW2 race-themed, holiday themes, etc.)
- Fixing any bugs you discover during testing
- Adding small panel features you want
- Walking through any step of the Visual Studio setup

You're in good hands. Take it one step at a time. 🎮💙
