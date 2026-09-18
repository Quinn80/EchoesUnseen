using System.Runtime.InteropServices;
using System.Windows;

namespace EchoesUnseen.Services;

/// <summary>
/// Screen geometry in PHYSICAL PIXELS — the same coordinate space as
/// <c>GetCursorPos</c> and <c>BitBlt</c>.
///
/// WHY THIS EXISTS
///
/// WPF's <c>SystemParameters.VirtualScreenWidth</c> and friends report DIPs — the
/// screen divided by the display scaling. Win32's <c>GetCursorPos</c> reports physical
/// pixels. They are the same number only at 100% scaling, and every one of Quinn's
/// machines is not at 100%: a 2560x1440 32-inch panel at 125% reports 2048x1152.
///
/// Mixing the two is what broke hover for weeks. The cursor reader clamped a PHYSICAL
/// pointer position against a DIP screen width:
///
///     x = Math.Clamp(x, vx, vx + vw - BoxWidth);   // vw was 2048, not 2560
///
/// so the capture box could never be placed past x=2048 or y=1152 and the outer fifth
/// of the screen was invisible to it. That is exactly where Guild Wars 2 puts the
/// inventory and the gold counter. Pointing into that strip pinned the box to a fixed
/// spot and read out whatever happened to be sitting there — the icon grid, a wall,
/// the chat panel. Four separate attempts to fix "hover reads garbage" went at the
/// text filter, because the reads genuinely were garbage. The lens was fine. The
/// camera was pointed somewhere else.
///
/// The same mistake sat in three other places, so the numbers live here now and every
/// caller asks for them rather than working them out again.
///
/// NOT TUNED TO ONE MACHINE. Everything below is measured from Windows at the moment
/// it is asked for: any resolution, any scaling, any number of monitors, and monitors
/// that disagree with each other about all three.
/// </summary>
public static class ScreenMetrics
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                      SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>
    /// Every monitor together, in physical pixels. On a multi-monitor desktop the
    /// origin can be negative — a second screen placed left of the primary starts at
    /// a negative X — so callers must use <see cref="Rect.Left"/> rather than assuming 0.
    /// </summary>
    public static Rect VirtualBounds =>
        new(GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>
    /// The monitor containing a physical point, in physical pixels.
    ///
    /// Clamping to THIS rather than to the whole desktop matters on more than one
    /// screen: it stops a capture box straddling the gap between two panels and
    /// reading half a tooltip beside half of somebody's browser.
    /// </summary>
    public static Rect MonitorAt(int x, int y) =>
        RectOf(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTOPRIMARY));

    /// <summary>The monitor a window is on, in physical pixels. Used to record the
    /// screen the game is actually running on rather than assuming the primary.</summary>
    public static Rect MonitorOfWindow(IntPtr hwnd) =>
        RectOf(MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY));

    /// <summary>
    /// Display scaling of the monitor under a point — 1.25 for 125%, and so on.
    ///
    /// Per-monitor, deliberately. A laptop at 150% beside an external screen at 100%
    /// is ordinary, and one global scale factor gets one of them wrong.
    /// </summary>
    public static double ScaleAt(int x, int y)
    {
        try
        {
            var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTOPRIMARY);
            if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { /* Shcore is Windows 8.1+; fall through to the system scale */ }

        // Fallback: infer it from the two numbers we can always get.
        var sp = SystemParameters.VirtualScreenWidth;
        return sp > 0 ? GetSystemMetrics(SM_CXVIRTUALSCREEN) / sp : 1.0;
    }

    /// <summary>
    /// Convert a WPF rectangle in screen DIPs to physical pixels, using the scaling of
    /// the monitor that rectangle actually sits on.
    ///
    /// The chat reader's region is chosen with a WPF overlay and therefore stored in
    /// DIPs. Anything that compares it against a cursor position, or hands it to a
    /// screen grab, has to come through here first.
    /// </summary>
    public static Rect DipToPhysical(Rect dip)
    {
        if (dip.Width <= 0 || dip.Height <= 0) return dip;

        // Probe with the DIP origin scaled by the primary monitor first, which is
        // enough to identify the right monitor in every arrangement short of a
        // deliberately pathological one.
        var probe = ScaleAt(0, 0);
        var scale = ScaleAt((int)(dip.X * probe), (int)(dip.Y * probe));
        return new Rect(dip.X * scale, dip.Y * scale, dip.Width * scale, dip.Height * scale);
    }

    private static Rect RectOf(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfoW(hMonitor, ref mi))
            return new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                            mi.rcMonitor.Right - mi.rcMonitor.Left,
                            mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        return VirtualBounds;
    }
}
