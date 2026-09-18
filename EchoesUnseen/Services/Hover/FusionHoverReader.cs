using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using EchoesUnseen.Services.Hover.Fusion;
using OpenCvSharp;
using WpfRect = System.Windows.Rect;

namespace EchoesUnseen.Services.Hover;

/// <summary>
/// The app's side of the OpenCV + RapidOCR hover targeting: capture the screen around the
/// pointer straight into an OpenCV picture, hand it to <see cref="HoverFusion"/> - the same
/// code HoverReplay judges against the recorded corpus - off the UI thread, and give back
/// one accessible object.
///
/// Everything here is plumbing. Every decision about WHAT is under the pointer lives in
/// Services/Hover/Fusion, so a change to it is measured offline before it is heard.
/// </summary>
public static class FusionHoverReader
{
    private static HoverFusion? _fusion;
    private static readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>The area analysed around the pointer, in physical pixels. Wider above than
    /// below, because the window's title - which names the interface - sits at its top, and
    /// wide either side because a tooltip can be drawn well away from what it describes.
    /// Matches the region HoverFusion analyses, so no more is captured than is looked at.</summary>
    public const int Left = 1050, Right = 1050, Above = 1000, Below = 700;

    public sealed record Result(AccessibleTarget Target, WpfRect Captured, double CaptureMs);

    /// <summary>Is the engine available on this machine? False when ONNX or OpenCV's native
    /// library cannot load - the classic reader then does the work.</summary>
    public static bool Unavailable { get; private set; }

    /// <summary>Why it is unavailable, for the log line of every hover that falls back.</summary>
    public static string UnavailableReason { get; private set; } = "";

    public static async Task<Result?> ReadAsync(int px, int py, IReadOnlyList<string>? vaultNames, CancellationToken ct)
    {
        if (Unavailable) return null;
        var ocr = await Ocr.RapidOcrService.GetMatOcrAsync(ct).ConfigureAwait(false);
        if (ocr == null)
        {
            Unavailable = true;
            UnavailableReason = "the recognition models could not load";
            DiagLog.Log("FUSION", "the recognition models could not load - using the classic hover reader");
            return null;
        }

        // One analysis at a time. A newer hover cancels the older one's token, so the older
        // one abandons at its next checkpoint and this one takes over.
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var mon = ScreenMetrics.MonitorAt(px, py);
                var x0 = Math.Max((int)mon.Left, px - Left);
                var y0 = Math.Max((int)mon.Top, py - Above);
                var x1 = Math.Min((int)mon.Right, px + Right);
                var y1 = Math.Min((int)mon.Bottom, py + Below);
                var rect = new WpfRect(x0, y0, x1 - x0, y1 - y0);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var picture = Capture(rect);
                var captureMs = sw.Elapsed.TotalMilliseconds;
                if (picture == null) return null;
                ct.ThrowIfCancellationRequested();

                if (BugRecorderService.IsRecording)
                {
                    // Full-size grabs for the replay corpus, only while a report is recording.
                    Cv2.ImEncode(".png", picture, out var png);
                    BugRecorderService.NoteHoverGrab(png);
                }

                _fusion ??= new HoverFusion(ocr);
                _fusion.KnownVaultNames = vaultNames;
                var screen = new ScreenPicture(x0, y0, 1.0, picture.Width, picture.Height);
                var target = _fusion.Analyse(picture, null, screen, px, py, ct);
                return new Result(target, rect, captureMs);
            }, ct).ConfigureAwait(false);
        }
        catch (TypeInitializationException ex) { Fail(ex); return null; }
        catch (DllNotFoundException ex) { Fail(ex); return null; }
        catch (BadImageFormatException ex) { Fail(ex); return null; }
        finally { _one.Release(); }
    }

    private static void Fail(Exception ex)
    {
        Unavailable = true;
        UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        CrashLogger.Log("FusionHoverReader", ex);
        DiagLog.Log("FUSION", "OpenCV + RapidOCR targeting could not load - using the classic hover reader");
    }

    // ---- capture straight into a BGR picture: no PNG encode and decode on the way ----------

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    private const int SRCCOPY = 0x00CC0020;

    /// <summary>Physical pixels in, a BGR picture out. Null if the capture failed.</summary>
    public static Mat? Capture(WpfRect r)
    {
        int w = (int)r.Width, h = (int)r.Height;
        if (w <= 0 || h <= 0) return null;
        IntPtr desk = IntPtr.Zero, src = IntPtr.Zero, mem = IntPtr.Zero, bmp = IntPtr.Zero;
        try
        {
            desk = GetDesktopWindow();
            src = GetWindowDC(desk);
            mem = CreateCompatibleDC(src);
            bmp = CreateCompatibleBitmap(src, w, h);
            var old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, src, (int)r.X, (int)r.Y, SRCCOPY)) return null;
            SelectObject(mem, old);

            using var image = System.Drawing.Image.FromHbitmap(bmp);
            var data = image.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                using var wrapped = Mat.FromPixelData(h, w, MatType.CV_8UC3, data.Scan0, data.Stride);
                return wrapped.Clone();
            }
            finally { image.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("FusionHoverReader.Capture", ex);
            return null;
        }
        finally
        {
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (src != IntPtr.Zero) ReleaseDC(desk, src);
        }
    }
}
