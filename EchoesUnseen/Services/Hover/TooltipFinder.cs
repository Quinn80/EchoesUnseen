using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace EchoesUnseen.Services.Hover;

/// <summary>
/// FIND THE THING THAT JUST APPEARED.
///
/// Guild Wars 2 does not draw an item's tooltip on top of the item. It draws it wherever
/// there is room - beside the bag, above the bar, across the panel - which means the
/// pointer is very often nowhere near the words it caused to exist. Every attempt to
/// find those words by looking AROUND THE POINTER has failed for the same reason: they
/// are not there. Quinn's inventory captures contain an icon, a border and a stack
/// count, and the name she wanted was three hundred pixels away.
///
/// But the tooltip has one property nothing else on screen has: A MOMENT AGO IT WAS NOT
/// THERE. It exists BECAUSE she hovered. That is a far stronger association than
/// distance, and it is cheap to detect - hold a small greyscale thumbnail of the screen,
/// take another after the pointer settles, and the rectangle that changed is the answer.
///
/// The thumbnail is an eighth of the screen in each direction, so a 2560x1440 display
/// compares 320x180 pixels: about a megabyte of work, a few milliseconds. Only the
/// resulting rectangle is grabbed and read at full resolution, so the expensive part
/// still only ever looks at the tooltip itself.
///
/// WHAT IT DELIBERATELY IGNORES. The world moves - grass, water, other players, the
/// character's own animation - so a change that is not panel-shaped is not a tooltip.
/// Tooltips are rectangular, solid, and appear all at once.
/// </summary>
public static class TooltipFinder
{
    /// <summary>How much the screen is shrunk before comparing. An eighth is enough to
    /// locate a panel and cheap enough to do on every hover.</summary>
    private const int Shrink = 8;

    /// <summary>A change smaller than this (in thumbnail pixels) is noise, not a panel.</summary>
    private const int MinBlockW = 12, MinBlockH = 6;

    /// <summary>How different a pixel must be to count as changed, 0-255.</summary>
    private const int PixelDelta = 26;

    private static byte[]? _before;
    private static int _w, _h;
    private static Rect _bounds;
    private static DateTime _takenAt;
    private static bool _frozen;
    private static int _refreshes;

    /// <summary>How many times the baseline has been refreshed this session. A number
    /// that stops climbing is the symptom of a latch that never released.</summary>
    public static int Refreshes => _refreshes;

    /// <summary>Why the last attempt ended the way it did, for the log and the report.</summary>
    public static string LastReason { get; private set; } = "not run";

    /// <summary>The after-frame of the last attempt, full size, so a rejection can be
    /// looked at rather than argued about.</summary>
    public static byte[]? LastAfterPng { get; private set; }

    /// <summary>The bounding box of whatever changed last time, accepted or not.</summary>
    public static Rect LastChangedBox { get; private set; }

    /// <summary>How old the baseline is, in milliseconds, or -1 if there is none.
    /// A baseline taken too long ago has watched the world move and is worthless.</summary>
    public static double BaselineAgeMs =>
        _before == null ? -1 : (DateTime.UtcNow - _takenAt).TotalMilliseconds;

    /// <summary>
    /// Remember what the screen looks like now, so a later call can spot what appeared.
    ///
    /// Called while the pointer is still MOVING - before the game has had a reason to
    /// draw anything - so the "before" really is before.
    /// </summary>
    public static void Remember(Rect monitor)
    {
        try
        {
            // FROZEN ONCE SETTLING BEGINS.
            //
            // The baseline has to be from BEFORE the pointer stopped, or it already
            // contains the tooltip and nothing appears to have changed. Overwriting it
            // during the dwell is the subtlest way to make this whole mechanism quietly
            // useless, so once Freeze() is called it stays put until the read is done.
            if (_frozen) return;

            var thumb = Grab(monitor);
            if (thumb == null) return;
            _before = thumb;
            _bounds = monitor;
            _takenAt = DateTime.UtcNow;
            _refreshes++;
        }
        catch (Exception ex) { CrashLogger.Log("TooltipFinder.Remember", ex); }
    }

    /// <summary>
    /// A baseline older than this has watched the world move and is no longer a picture
    /// of "just before". Rather than compare against it and report nonsense, the finder
    /// refuses to run and waits for a fresh one.
    /// </summary>
    private const double MaxBaselineMs = 1000;

    /// <summary>
    /// Stop replacing the baseline: the pointer has settled and whatever appears from
    /// here is what we are looking for.
    ///
    /// Idempotent on purpose - the poll calls it on every tick once the pointer is still,
    /// and only the FIRST call should decide which frame is the "before".
    /// </summary>
    public static void Freeze()
    {
        if (_frozen || _before == null) return;
        _frozen = true;
        DiagLog.Log("TOOLTIP", $"BASELINE FROZEN age={BaselineAgeMs:F0}");
    }

    /// <summary>
    /// Let the baseline start updating again. Called the moment the pointer moves.
    ///
    /// THIS IS THE ONE THAT WAS MISSING. Freeze() latched and nothing ever unlatched it,
    /// because the only Forget() call lived in the live path that shadow mode removed.
    /// So Remember() returned early forever and the "before" picture kept ageing -
    /// Quinn's log shows it reaching 281,599 ms, which is not a stale frame so much as a
    /// photograph of a different afternoon.
    /// </summary>
    public static void Thaw() => _frozen = false;

    /// <summary>Forget the snapshot entirely - the pointer has left the game, or a read
    /// has finished with it.</summary>
    public static void Forget() { _before = null; _frozen = false; }

    /// <summary>
    /// What rectangle of the screen has appeared since <see cref="Remember"/>?
    ///
    /// Returns empty when nothing panel-shaped changed, which is the common case and
    /// means "there is no tooltip; read what is under the pointer instead".
    /// </summary>
    public static Rect FindNewPanel(Rect monitor)
    {
        try
        {
            if (_before == null || _bounds != monitor) return Rect.Empty;

            var age = BaselineAgeMs;
            if (age > MaxBaselineMs)
            {
                LastReason = $"baseline stale ({age:F0} ms)";
                DiagLog.Log("TOOLTIP", "ABORT: " + LastReason);
                _before = null;          // make the next movement take a fresh one
                _frozen = false;
                return Rect.Empty;
            }

            var after = Grab(monitor);
            if (after == null || after.Length != _before.Length) return Rect.Empty;

            // Bounding box of everything that changed.
            int minX = _w, minY = _h, maxX = -1, maxY = -1, changed = 0;
            for (int y = 0; y < _h; y++)
            {
                int row = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    if (Math.Abs(after[row + x] - _before[row + x]) < PixelDelta) continue;
                    changed++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX >= 0)
                LastChangedBox = new Rect(monitor.Left + minX * Shrink, monitor.Top + minY * Shrink,
                                          (maxX - minX + 1) * Shrink, (maxY - minY + 1) * Shrink);
            LastAfterPng = ScreenCaptureService.CapturePng(
                (int)LastChangedBox.Left, (int)LastChangedBox.Top,
                Math.Max(1, (int)LastChangedBox.Width), Math.Max(1, (int)LastChangedBox.Height));

            var pct = 100.0 * changed / Math.Max(1, _w * _h);
            DiagLog.Log("TOOLTIP", $"AFTER CAPTURE age={age:F0}  DIFF changed={pct:F1}%");

            if (maxX < 0)
            {
                LastReason = "nothing changed at all";
                DiagLog.Log("TOOLTIP", "REJECT: " + LastReason);
                return Rect.Empty;
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < MinBlockW || bh < MinBlockH)
            {
                LastReason = $"change too small ({bw}x{bh} thumb px, need {MinBlockW}x{MinBlockH})";
                DiagLog.Log("TOOLTIP", "REJECT: " + LastReason);
                return Rect.Empty;
            }

            // A TOOLTIP IS A SOLID BLOCK, NOT A SCATTER.
            //
            // If the changed pixels fill most of their own bounding box, something
            // rectangular appeared. If they are sprinkled across it, that is the world
            // moving - grass, water, a passing player - and the bounding box is merely
            // the extent of the weather.
            var fill = (double)changed / (bw * bh);
            if (fill < 0.45)
            {
                LastReason = $"scattered, fills {fill:P0} of its own box";
                DiagLog.Log("TOOLTIP", "REJECT: " + LastReason);
                return Rect.Empty;
            }

            // Nor is a tooltip most of the screen; that is a panel opening or a map.
            if (bw * bh > _w * _h * 0.55)
            {
                LastReason = $"too large ({100.0 * bw * bh / (_w * _h):F0}% of the screen)";
                DiagLog.Log("TOOLTIP", "REJECT: " + LastReason);
                return Rect.Empty;
            }

            var panel = new Rect(monitor.Left + minX * Shrink, monitor.Top + minY * Shrink,
                                 bw * Shrink, bh * Shrink);
            LastReason = $"accepted, fills {fill:P0}";
            DiagLog.Log("TOOLTIP", $"PANEL APPEARED {(int)panel.Width}x{(int)panel.Height} at " +
                                   $"({(int)panel.Left},{(int)panel.Top}), fills {fill:P0}");
            return panel;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("TooltipFinder.FindNewPanel", ex);
            return Rect.Empty;
        }
    }

    /// <summary>A greyscale thumbnail of the monitor, one byte per pixel.</summary>
    private static byte[]? Grab(Rect monitor)
    {
        var png = ScreenCaptureService.CapturePng((int)monitor.Left, (int)monitor.Top,
                                                  (int)monitor.Width, (int)monitor.Height);
        if (png == null) return null;

        using var src = new Bitmap(new MemoryStream(png));
        _w = Math.Max(1, src.Width / Shrink);
        _h = Math.Max(1, src.Height / Shrink);

        using var small = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            // Low-quality on purpose: this is a fingerprint, not a picture, and the
            // cheap sampler is several times faster.
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(src, 0, 0, _w, _h);
        }

        var data = small.LockBits(new System.Drawing.Rectangle(0, 0, _w, _h), ImageLockMode.ReadOnly,
                                  PixelFormat.Format32bppArgb);
        try
        {
            var grey = new byte[_w * _h];
            unsafe
            {
                var p = (byte*)data.Scan0;
                for (int y = 0; y < _h; y++)
                {
                    var row = p + y * data.Stride;
                    for (int x = 0; x < _w; x++)
                    {
                        var px = row + x * 4;          // BGRA
                        grey[y * _w + x] = (byte)((px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8);
                    }
                }
            }
            return grey;
        }
        finally { small.UnlockBits(data); }
    }
}
