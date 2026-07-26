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
        // Rough conversion — for per-monitor DPI accuracy, prefer the explicit pixel overload
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(
            System.Windows.Application.Current.MainWindow ?? new System.Windows.Window());
        var scaleX = dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY;
        return CapturePng(
            (int)(dipRect.X * scaleX),
            (int)(dipRect.Y * scaleY),
            (int)(dipRect.Width * scaleX),
            (int)(dipRect.Height * scaleY));
    }
}
