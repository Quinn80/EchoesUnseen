using SendKeys = System.Windows.Forms.SendKeys;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Sends numeric keypresses to whichever window currently has focus —
/// used by the Music Player's Auto-Play mode to play notes on a GW2 instrument.
///
/// TOS COMPLIANCE:
///   ArenaNet explicitly permits programs that send ONLY music note keys (1–8)
///   to in-game instruments. This service rigidly enforces that policy:
///     - Only digits 0–9 are ever sent
///     - Duration is clamped to 50–500ms per note
///     - No modifier keys, no other characters, ever
///   This is NOT a general-purpose input automation tool. It exists solely
///   for the Music Player panel and has no other callers.
///
/// FOCUS REQUIREMENT:
///   <see cref="SendKeys.SendWait"/> targets whichever window has focus.
///   The Music Player panel instructs the user to click on GW2 before
///   starting auto-play so GW2 has focus when the keys fire.
/// </summary>
public class KeyPressService
{
    /// <summary>
    /// Play a parsed song sequence. Respects the cancellation token so the
    /// user's Stop button immediately interrupts playback.
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
            int ms = Math.Clamp((int)(note.BeatMs / Math.Max(0.25f, tempo)), 50, 500);

            if (!note.IsRest && char.IsDigit(note.Key))
            {
                try
                {
                    SendKeys.SendWait(note.Key.ToString());
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("KeyPressService.SendKeys", ex);
                    return;
                }
            }

            // Even during a rest we wait the note's duration so timing feels right.
            try { await Task.Delay(ms, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
