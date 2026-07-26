using System.Runtime.InteropServices;
using System.Windows;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Services;

/// <summary>
/// Reads aloud whatever is on screen around the mouse pointer — item tooltips,
/// inventory slots, menu buttons, achievement rows.
///
/// WHY A REGION AROUND THE CURSOR, NOT THE WHOLE SCREEN
///   Guild Wars 2 draws its tooltip next to whatever you're pointing at, so the
///   text you want is almost always within a few hundred pixels of the cursor.
///   Grabbing just that area means OCR has a small, dense patch to read instead
///   of an entire 3D scene, which is both far more accurate and much faster.
///
/// NO AI: this is Windows' built-in OCR plus the contrast/upscale conditioning
/// in <see cref="ImagePrep"/>. Nothing is sent anywhere; nothing is trained.
///
/// LIMITS, HONESTLY
///   The game exposes no accessibility information, so reading its interface
///   means reading pixels. Tooltips (large, high-contrast, on a dark panel) read
///   well. Tiny dim labels over a bright scene are still hard. For inventory
///   contents and gold there is a far better route than pixels — the official
///   GW2 API — which returns exact data (see Gw2ApiService).
/// </summary>
public sealed class CursorReader
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // The tooltip box GW2 draws is roughly this size; generous enough to catch a
    // full item description, tight enough to keep OCR fast and focused.
    private const int BoxWidth = 620;
    private const int BoxHeight = 420;

    private readonly TtsService _tts;
    private string _lastSpoken = "";

    public CursorReader(TtsService tts) => _tts = tts;

    /// <summary>
    /// Capture around the pointer, OCR it, and speak the result.
    /// Safe to call repeatedly; identical consecutive reads are not repeated.
    /// </summary>
    public async Task ReadAsync()
    {
        try
        {
            if (!GetCursorPos(out var p))
            {
                await _tts.SpeakAsync("I couldn't find the mouse pointer.");
                return;
            }

            // Bias the box DOWN-RIGHT of the cursor, because that's where the
            // game puts tooltips, but keep some margin above/left so a tooltip
            // flipped to the other side is still caught.
            int x = p.X - BoxWidth / 3;
            int y = p.Y - BoxHeight / 4;

            var vx = (int)SystemParameters.VirtualScreenLeft;
            var vy = (int)SystemParameters.VirtualScreenTop;
            var vw = (int)SystemParameters.VirtualScreenWidth;
            var vh = (int)SystemParameters.VirtualScreenHeight;

            x = Math.Clamp(x, vx, Math.Max(vx, vx + vw - BoxWidth));
            y = Math.Clamp(y, vy, Math.Max(vy, vy + vh - BoxHeight));

            var png = ScreenCaptureService.CapturePng(x, y, BoxWidth, BoxHeight);
            if (png == null)
            {
                await _tts.SpeakAsync("I couldn't capture that part of the screen.");
                return;
            }

            var raw = await OcrService.ReadAsync(png);
            var text = Tidy(raw);

            if (string.IsNullOrWhiteSpace(text))
            {
                await _tts.SpeakAsync("Nothing readable under the pointer.");
                return;
            }

            if (text == _lastSpoken)
            {
                // Same thing again — say it rather than sit silent, but don't
                // pretend it's new.
                await _tts.SpeakAsync(text);
                return;
            }

            _lastSpoken = text;
            await _tts.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("CursorReader.ReadAsync", ex);
            await _tts.SpeakAsync("Something went wrong reading the screen.");
        }
    }

    /// <summary>
    /// Drop OCR debris and cap the length. Lines that are mostly punctuation or
    /// stray glyphs get spelled out letter-by-letter by a screen reader, which
    /// is exactly the jumbled result we're trying to avoid.
    /// </summary>
    private static string Tidy(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var kept = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l =>
            {
                if (l.Length < 3) return false;
                int letters = l.Count(char.IsLetter);
                return letters >= 2 && letters >= l.Length * 0.35;
            })
            .ToList();

        if (kept.Count == 0) return "";

        var text = string.Join(". ", kept);
        return text.Length <= 600 ? text : text[..600] + "…";
    }
}
