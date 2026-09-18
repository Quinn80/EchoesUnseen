using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace EchoesUnseen.Services;

/// <summary>
/// Captures a rectangular region of the Windows desktop to an in-memory bitmap.
///
/// Uses Win32 BitBlt for speed — native GDI is significantly faster than
/// .NET's Graphics.CopyFromScreen for small regions (chat window, dialogue box)
/// which is what we're doing 95% of the time.
///
/// The returned PNG bytes are fed directly to Windows.Media.Ocr via its
/// BitmapDecoder → SoftwareBitmap pipeline.
///
/// COORDINATE SYSTEM:
///   Input rect is in PHYSICAL SCREEN PIXELS (not WPF DIPs). The caller is
///   responsible for converting WPF DIPs to physical pixels if needed. The
///   SelectionOverlayWindow handles this conversion using PresentationSource
///   compositing info.
/// </summary>
public static class ScreenCaptureService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest,
        int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int SRCCOPY = 0x00CC0020;

    /// <summary>
    /// Capture a screen rectangle as a DOWNSCALED JPEG.
    ///
    /// For bug reports we want many frames, not one perfect one. A full-resolution PNG
    /// of a 2480x1680 screen is well over a megabyte; the same frame at 1600 wide as
    /// quality-72 JPEG is a fraction of that and still shows a trail clearly. Two
    /// minutes of PNG would be a gigabyte and unusable; as JPEG it is a few tens of
    /// megabytes and zips down further.
    /// </summary>
    public static byte[]? CaptureJpeg(int x, int y, int width, int height, int maxWidth, int quality)
    {
        var png = CapturePng(x, y, width, height);
        if (png == null) return null;
        try
        {
            using var src = new Bitmap(new MemoryStream(png));
            int w = src.Width, h = src.Height;
            if (maxWidth > 0 && w > maxWidth)
            {
                h = (int)Math.Round(h * (maxWidth / (double)w));
                w = maxWidth;
            }

            using var dst = new Bitmap(w, h);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }

            var codec = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            if (codec == null) return png;

            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                                               (long)Math.Clamp(quality, 20, 95));
            using var ms = new MemoryStream();
            dst.Save(ms, codec, ps);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ScreenCaptureService.CaptureJpeg", ex);
            return png;                       // better a big frame than none
        }
    }

    /// <summary>
    /// Capture the given screen rectangle and return it as PNG-encoded bytes.
    /// Returns null if the rectangle is empty or capture failed.
    /// </summary>
    public static byte[]? CapturePng(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        IntPtr hDesktop = IntPtr.Zero;
        IntPtr hSrcDC = IntPtr.Zero;
        IntPtr hMemDC = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hDesktop = GetDesktopWindow();
            hSrcDC = GetWindowDC(hDesktop);
            hMemDC = CreateCompatibleDC(hSrcDC);
            hBitmap = CreateCompatibleBitmap(hSrcDC, width, height);
            hOld = SelectObject(hMemDC, hBitmap);

            if (!BitBlt(hMemDC, 0, 0, width, height, hSrcDC, x, y, SRCCOPY))
                return null;

            // Restore DC and convert to managed Bitmap for PNG encoding
            SelectObject(hMemDC, hOld);

            using var bmp = Image.FromHbitmap(hBitmap);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ScreenCaptureService.CapturePng", ex);
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hMemDC != IntPtr.Zero) DeleteDC(hMemDC);
            if (hSrcDC != IntPtr.Zero) ReleaseDC(hDesktop, hSrcDC);
        }
    }

    /// <summary>
    /// Convenience overload taking a WPF Rect in SCREEN DIPs. Converts to
    /// physical pixels using the DPI of the primary monitor.
    /// </summary>
    public static byte[]? CapturePng(Rect dipRect)
    {
        // Uses the scaling of the monitor the rectangle is actually on. It used to take
        // the DPI of the app's own main window, which is right only while every screen
        // agrees - a laptop at 150% beside an external at 100% got one of them wrong,
        // and the grab landed somewhere other than the region the user drew.
        var r = ScreenMetrics.DipToPhysical(dipRect);
        return CapturePng((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }
}
