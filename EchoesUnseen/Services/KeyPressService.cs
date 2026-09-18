using System.Runtime.InteropServices;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Sends numeric note keypresses to whichever window currently has focus —
/// used by the Music Player's Auto-Play mode to play notes on a GW2 instrument.
///
/// WHY SCAN CODES (SendInput) AND NOT SendKeys:
///   Guild Wars 2 reads raw/hardware keyboard input and IGNORES the higher-level
///   window-message input that <c>SendKeys</c> produces — which is why the old
///   implementation silently did nothing in-game. This version injects HARDWARE
///   SCAN CODES via <c>SendInput</c>, exactly as AutoHotkey does, so the game
///   registers them as real key presses.
///
/// TOS COMPLIANCE:
///   ArenaNet permits programs that send ONLY music note keys to in-game
///   instruments. This service rigidly enforces that and nothing else:
///     - Only the digit note keys 0–9 are ever sent (instruments use 1–8)
///     - Each note is a single down+up; no held modifiers, no other keys, ever
///     - Per-note duration is clamped to 50–500 ms
///   It is NOT a general input-automation tool and has no other callers.
///
/// FOCUS REQUIREMENT:
///   SendInput targets the FOCUSED window, so the Music Player counts down and
///   tells the user to click Guild Wars 2 first, giving it focus before notes fire.
/// </summary>
public class KeyPressService
{
    /// <summary>Note key → Set-1 keyboard scan code for the top-row digits.</summary>
    private static readonly Dictionary<char, ushort> ScanCodes = new()
    {
        ['1'] = 0x02, ['2'] = 0x03, ['3'] = 0x04, ['4'] = 0x05, ['5'] = 0x06,
        ['6'] = 0x07, ['7'] = 0x08, ['8'] = 0x09, ['9'] = 0x0A, ['0'] = 0x0B,
    };

    /// <summary>
    /// Play a parsed song sequence. Respects the cancellation token so the
    /// user's Stop button immediately interrupts playback (and releases any key).
    /// </summary>
    public async Task PlayAsync(
        List<ParsedNote> notes,
        float tempo,
        Action<int>? onNoteChange,
        CancellationToken ct)
    {
        for (int i = 0; i < notes.Count; i++)
        {
            if (ct.IsCancellationRequested) return;

            onNoteChange?.Invoke(i);
            var note = notes[i];
            // Honour the note's real duration (imports carry exact ms). Wide clamp
            // so long held notes and quick grace notes both survive.
            int ms = Math.Clamp((int)(note.BeatMs / Math.Max(0.25f, tempo)), 40, 3000);

            if (!note.IsRest && ScanCodes.TryGetValue(note.Key, out var sc))
            {
                // Hold the key long enough for GW2 to register it, then leave a clear
                // GAP before the next note so the game sees a distinct release/press
                // (too-short holds and back-to-back presses were being missed).
                int hold = Math.Clamp((int)(ms * 0.6), 45, 150);
                int gap = Math.Max(ms - hold, 30);
                try
                {
                    SendScan(sc, down: true);
                    try { await Task.Delay(hold, ct); }
                    finally { SendScan(sc, down: false); }
                    await Task.Delay(gap, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { CrashLogger.Log("KeyPressService.SendScan", ex); return; }
            }
            else
            {
                // Rest — still wait the beat so timing stays true.
                try { await Task.Delay(ms, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // ── SendInput hardware-scan-code injection ────────────────────────────────
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private static bool _loggedFirstSend;

    private static void SendScan(ushort scan, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,                       // 0 → the wScan field is authoritative
                    wScan = scan,
                    dwFlags = KEYEVENTF_SCANCODE | (down ? 0 : KEYEVENTF_KEYUP),
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        // Log the FIRST send of a run: sent==0 means Windows rejected it — almost
        // always because Guild Wars 2 is elevated and we are not (run us as admin).
        if (!_loggedFirstSend)
        {
            _loggedFirstSend = true;
            int err = Marshal.GetLastWin32Error();
            DiagLog.Log("AUTOPLAY", sent == 1
                ? "SendInput OK (input is reaching the system)."
                : $"SendInput REJECTED (sent={sent}, err={err}). GW2 is likely running as administrator — run Echoes Unseen as administrator too.");
        }
    }

    /// <summary>Reset the one-time send log so the next Play run logs again.</summary>
    public static void ResetSendLog() => _loggedFirstSend = false;

    /// <summary>Send key-UP for every note key. Called when playback stops, in case
    /// it was interrupted mid-note and a key was left logically held in the game —
    /// so nothing ever stays "stuck down" after you press Stop.</summary>
    public static void ReleaseNoteKeys()
    {
        try { foreach (var sc in ScanCodes.Values) SendScan(sc, down: false); } catch { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Full INPUT layout so Marshal.SizeOf matches what Windows expects (the union
    // must be sized for the largest member, MOUSEINPUT, or SendInput no-ops).
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData;
        public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
}
