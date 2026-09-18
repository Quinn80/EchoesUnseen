using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>A straight edge found in the picture: horizontal (Y fixed) or vertical (X fixed).</summary>
public readonly record struct Segment(bool Horizontal, double Pos, double From, double To, double Strength)
{
    public double Length => To - From;
}

/// <summary>A rectangle whose four sides are drawn in the picture.</summary>
public readonly record struct UiRect(Box Box, double Coverage, string Why);

/// <summary>
/// WHAT OBJECT IS THE POINTER IN - asked of the pixels, not of the text.
///
/// Guild Wars 2 draws its interface as rectangles: a reward card has a frame, a tooltip has
/// a border, a merchant row lights up as a band when hovered, a button is a filled box. OCR
/// sees none of that; it only sees letters. So this finds the long straight edges in the
/// picture and the rectangles they enclose, and the rest of the fusion asks "which of those
/// contains the pointer" and "which of them contains this text".
///
/// Everything is measured in multiples of the line height handed in, so it holds for any
/// interface size and display scaling.
/// </summary>
public sealed class UiGeometry : IDisposable
{
    private readonly Mat _gray;
    private readonly Mat _value;
    private readonly Box _region;          // picture pixels this analysis covers
    private readonly double _h;             // text line height, picture pixels
    private Mat? _gx, _gy;

    public List<Segment> Horizontal { get; } = new();
    public List<Segment> Vertical { get; } = new();

    public UiGeometry(Mat picture, Box region, double lineHeight)
    {
        _region = region.Clip(new Box(0, 0, picture.Width, picture.Height));
        _h = Math.Max(6, lineHeight);
        using var crop = new Mat(picture, new OpenCvSharp.Rect((int)_region.X, (int)_region.Y, (int)_region.W, (int)_region.H));
        _gray = new Mat();
        _value = new Mat();
        if (crop.Channels() == 3)
        {
            Cv2.CvtColor(crop, _gray, ColorConversionCodes.BGR2GRAY);
            // the brightest channel: green and blue lettering is as bright as white by this measure
            var ch = Cv2.Split(crop);
            Cv2.Max(ch[0], ch[1], _value);
            Cv2.Max(_value, ch[2], _value);
            foreach (var c in ch) c.Dispose();
        }
        else
        {
            crop.CopyTo(_gray);
            _gray.CopyTo(_value);
        }
        FindSegments();
    }

    public Box Region => _region;

    /// <summary>Mean brightness of a box (picture pixels).</summary>
    public double Mean(Box b)
    {
        var r = ToLocal(b);
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var roi = new Mat(_gray, r);
        return Cv2.Mean(roi).Val0;
    }

    /// <summary>Standard deviation of brightness in a box - how busy it is.</summary>
    public double StdDev(Box b)
    {
        var r = ToLocal(b);
        if (r.Width <= 1 || r.Height <= 1) return 0;
        using var roi = new Mat(_gray, r);
        Cv2.MeanStdDev(roi, out _, out var sd);
        return sd.Val0;
    }

    /// <summary>The 95th-percentile brightness of a box: how bright its lettering is, whatever the
    /// background. Text seen through a translucent panel is a fraction of the panel's own.</summary>
    public double Lettering(Box b)
    {
        var r = ToLocal(b);
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var roi = new Mat(_value, r);
        using var copy = roi.Clone();
        copy.GetArray(out byte[] px);
        Array.Sort(px);
        return px[Math.Min(px.Length - 1, (int)(px.Length * 0.95))];
    }

    private OpenCvSharp.Rect ToLocal(Box b)
    {
        var c = b.Clip(_region);
        if (c.IsEmpty) return new OpenCvSharp.Rect(0, 0, 0, 0);
        return new OpenCvSharp.Rect((int)(c.X - _region.X), (int)(c.Y - _region.Y),
                        Math.Max(0, (int)Math.Round(c.W)), Math.Max(0, (int)Math.Round(c.H)));
    }

    /// <summary>
    /// Long straight edges. A Sobel gradient split into its horizontal and vertical parts,
    /// thresholded, then opened with a long thin kernel so that only runs longer than a
    /// couple of text heights survive - letters, icons and artwork texture do not.
    /// </summary>
    private void FindSegments()
    {
        _gx = new Mat(); _gy = new Mat();
        using var sx = new Mat();
        using var sy = new Mat();
        Cv2.Sobel(_gray, sx, MatType.CV_16S, 1, 0, 3);
        Cv2.Sobel(_gray, sy, MatType.CV_16S, 0, 1, 3);
        Cv2.ConvertScaleAbs(sx, _gx);
        Cv2.ConvertScaleAbs(sy, _gy);

        // Edge strength threshold: faint enough for a dim tooltip border, strong enough
        // that JPEG ringing and painted backgrounds stay out.
        const double T = 40;
        using var hBin = new Mat();
        using var vBin = new Mat();
        Cv2.Threshold(_gy, hBin, T, 255, ThresholdTypes.Binary);
        Cv2.Threshold(_gx, vBin, T, 255, ThresholdTypes.Binary);

        int hLen = Math.Max(12, (int)(_h * 2.2));
        int vLen = Math.Max(8, (int)(_h * 1.1));
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(hLen, 1)))
            Cv2.MorphologyEx(hBin, hBin, MorphTypes.Open, k);
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, vLen)))
            Cv2.MorphologyEx(vBin, vBin, MorphTypes.Open, k);

        // A drawn border is often two edges a pixel or two apart (dark line, light line);
        // join them so each border becomes one segment.
        using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
        {
            Cv2.Dilate(hBin, hBin, k);
            Cv2.Dilate(vBin, vBin, k);
        }

        Collect(hBin, horizontal: true, Horizontal);
        Collect(vBin, horizontal: false, Vertical);
    }

    private void Collect(Mat bin, bool horizontal, List<Segment> into)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var cents = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(bin, labels, stats, cents, PixelConnectivity.Connectivity8);
        for (int i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, 0), y = stats.At<int>(i, 1), w = stats.At<int>(i, 2), hgt = stats.At<int>(i, 3);
            int area = stats.At<int>(i, 4);
            if (horizontal)
            {
                if (w < _h * 2.2 || hgt > _h * 0.6) continue;   // thick blobs are not lines
                into.Add(new Segment(true, _region.Y + y + hgt / 2.0, _region.X + x, _region.X + x + w, area / (double)w));
            }
            else
            {
                if (hgt < _h * 1.1 || w > _h * 0.6) continue;
                into.Add(new Segment(false, _region.X + x + w / 2.0, _region.Y + y, _region.Y + y + hgt, area / (double)hgt));
            }
        }
    }

    /// <summary>
    /// Rectangles drawn around the point: pairs of horizontal edges above and below it that
    /// span it, closed by vertical edges left and right. Returned innermost first.
    /// </summary>
    public List<UiRect> RectanglesAround(double x, double y, double minW, double minH, double maxW, double maxH)
    {
        double slack = _h * 0.6;
        var tops = Horizontal.Where(s => s.Pos <= y && s.From - slack <= x && s.To + slack >= x)
                             .OrderByDescending(s => s.Pos).Take(6).ToList();
        var bottoms = Horizontal.Where(s => s.Pos >= y && s.From - slack <= x && s.To + slack >= x)
                                .OrderBy(s => s.Pos).Take(6).ToList();
        var lefts = Vertical.Where(s => s.Pos <= x && s.From - slack <= y && s.To + slack >= y)
                            .OrderByDescending(s => s.Pos).Take(6).ToList();
        var rights = Vertical.Where(s => s.Pos >= x && s.From - slack <= y && s.To + slack >= y)
                             .OrderBy(s => s.Pos).Take(6).ToList();

        var found = new List<UiRect>();
        foreach (var t in tops)
            foreach (var b in bottoms)
            {
                var hgt = b.Pos - t.Pos;
                if (hgt < minH || hgt > maxH) continue;
                foreach (var l in lefts)
                    foreach (var r in rights)
                    {
                        var w = r.Pos - l.Pos;
                        if (w < minW || w > maxW) continue;
                        var box = Box.FromLTRB(l.Pos, t.Pos, r.Pos, b.Pos);
                        // Each side must actually run along the rectangle, not merely cross it.
                        var ct = Cover(t, box.X, box.Right) ;
                        var cb = Cover(b, box.X, box.Right);
                        var cl = Cover(l, box.Y, box.Bottom);
                        var cr = Cover(r, box.Y, box.Bottom);
                        var sides = new[] { ct, cb, cl, cr };
                        if (sides.Count(c => c >= 0.6) < 4 && !(sides.Count(c => c >= 0.8) >= 3 && sides.Min() >= 0.3)) continue;
                        found.Add(new UiRect(box, sides.Average(), $"t{ct:F1} b{cb:F1} l{cl:F1} r{cr:F1}"));
                    }
            }

        // Innermost first; drop near-duplicates.
        var result = new List<UiRect>();
        foreach (var f in found.OrderBy(f => f.Box.Area))
            if (!result.Any(r => r.Box.IoU(f.Box) > 0.85)) result.Add(f);
        return result;
    }

    private static double Cover(Segment s, double from, double to)
    {
        var len = to - from;
        if (len <= 0) return 0;
        var ov = Math.Min(s.To, to) - Math.Max(s.From, from);
        return Math.Max(0, ov) / len;
    }

    /// <summary>
    /// Brightness of a horizontal band across a span, sampled only where there is no text:
    /// a hovered merchant or trading post row is drawn lighter than its neighbours, and that
    /// is the game telling us which row the pointer is in.
    /// </summary>
    public double BandBrightness(double top, double bottom, double left, double right, IEnumerable<Box> textToIgnore)
    {
        var band = Box.FromLTRB(left, top, right, bottom);
        var r = ToLocal(band);
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var roi = new Mat(_gray, r);
        using var mask = new Mat(roi.Size(), MatType.CV_8U, new Scalar(255));
        foreach (var t in textToIgnore)
        {
            var tr = t.Inflate(_h * 0.2, _h * 0.1).Intersect(band);
            if (tr.IsEmpty) continue;
            Cv2.Rectangle(mask, new OpenCvSharp.Rect((int)(tr.X - band.X), (int)(tr.Y - band.Y), (int)tr.W, (int)tr.H), new Scalar(0), -1);
        }
        return Cv2.Mean(roi, mask).Val0;
    }

    /// <summary>
    /// Grow a panel outward from a block of text until the drawn border: for each side, the
    /// nearest line along which the gradient is strong for most of the block's extent.
    /// Returns the block itself, padded, when no border is found on a side.
    /// </summary>
    /// <param name="padding">Where the search starts outside the block, in line heights. A
    /// button's box sits tight around its label - often inside the detector's box for it - so
    /// buttons search from almost at the text.</param>
    public (Box Box, int SidesFound) PanelAround(Box block, double padding = 0.35)
    {
        int found = 0;
        double left = Scan(block, side: 0, padding, ref found);
        double top = Scan(block, side: 1, padding, ref found);
        double right = Scan(block, side: 2, padding, ref found);
        double bottom = Scan(block, side: 3, padding, ref found);
        return (Box.FromLTRB(left, top, right, bottom), found);
    }

    private double Scan(Box block, int side, double padding, ref int found)
    {
        // side: 0 left, 1 top, 2 right, 3 bottom
        double h = _h;
        double pad = h * padding;
        double maxOut = side switch { 0 => h * 2.2, 1 => h * 1.4, 2 => h * 3.0, _ => h * 1.4 };
        bool vertical = side is 0 or 2;
        var grad = vertical ? _gx! : _gy!;
        double start = side switch { 0 => block.X - pad, 1 => block.Y - pad, 2 => block.Right + pad, _ => block.Bottom + pad };
        int dir = side is 0 or 1 ? -1 : 1;

        // Span along the side to test: the block's extent, trimmed a little at the ends.
        double a = vertical ? block.Y + h * 0.2 : block.X + h * 0.2;
        double b = vertical ? block.Bottom - h * 0.2 : block.Right - h * 0.2;
        if (b - a < h * 0.8) { a = vertical ? block.Y : block.X; b = vertical ? block.Bottom : block.Right; }

        // The NEAREST convincing edge is the panel's own border. Taking the strongest edge
        // anywhere in reach grabbed the row of inventory slots under a tooltip, or the
        // caption of the card below it, and the panel swallowed them.
        double bestPos = start + dir * pad, bestFrac = 0;
        for (double d = 0; d <= maxOut; d += 1)
        {
            double pos = start + dir * d;
            double frac = LineFraction(grad, vertical, pos, a, b, 30);
            if (frac >= 0.7) { bestFrac = frac; bestPos = pos; break; }
            if (frac > bestFrac + 0.02) { bestFrac = frac; bestPos = pos; }
        }
        if (bestFrac >= 0.7) { found++; return bestPos; }
        return side switch { 0 => block.X - pad, 1 => block.Y - pad, 2 => block.Right + pad, _ => block.Bottom + pad };
    }

    /// <summary>
    /// A filled button's box, found from its label outwards. The side edges are searched along
    /// the label's height; the top and bottom edges only in the padding left and right of the
    /// label, where no letter strokes can pass for an edge. Null unless all four are found.
    /// </summary>
    public Box? ButtonAround(Box label)
    {
        var h = _h;
        double midA = label.CentreY - label.H * 0.3, midB = label.CentreY + label.H * 0.3;
        double? Nearest(bool vertical, double start, int dir, double maxOut, IEnumerable<(double A, double B)> spans)
        {
            var grad = vertical ? _gx! : _gy!;
            var list = spans.Where(s => s.B - s.A >= 3).ToList();
            if (list.Count == 0) return null;
            var total = list.Sum(s => s.B - s.A + 1);
            for (double d = 0; d <= maxOut; d += 1)
            {
                double pos = start + dir * d;
                var frac = list.Sum(s => LineFraction(grad, vertical, pos, s.A, s.B, 30) * (s.B - s.A + 1)) / total;
                if (frac >= 0.75) return pos;
            }
            return null;
        }
        // start clear of the first and last letters' own upright strokes
        var left = Nearest(true, label.X - h * 0.25, -1, h * 2.2, new[] { (midA, midB) });
        var right = Nearest(true, label.Right + h * 0.25, 1, h * 3.0, new[] { (midA, midB) });
        if (left == null || right == null) return null;
        var pads = new[] { (left.Value + 2, label.X - 2), (label.Right + 2, right.Value - 2) };
        var top = Nearest(false, label.CentreY - label.H * 0.2, -1, h * 1.4, pads);
        var bottom = Nearest(false, label.CentreY + label.H * 0.2, 1, h * 1.4, pads);
        if (top == null || bottom == null) return null;
        var box = Box.FromLTRB(left.Value, top.Value, right.Value, bottom.Value);

        // A button is FILLED: the padding beside its label is one colour, and a clearly different
        // one lies just outside its edges. Edges that merely happen to surround a line of text on
        // artwork, a banner or a card do not have that.
        double Median(double x0, double y0, double x1, double y1)
        {
            var r = ToLocal(Box.FromLTRB(x0, y0, x1, y1));
            if (r.Width < 2 || r.Height < 2) return double.NaN;
            using var roi = new Mat(_gray, r);
            var values = new List<byte>(r.Width * r.Height);
            for (int row = 0; row < r.Height; row++)
                for (int c = 0; c < r.Width; c++) values.Add(roi.At<byte>(row, c));
            values.Sort();
            return values[values.Count / 2];
        }
        double inT = box.Y + 3, inB = box.Bottom - 3;
        var checks = new (double In, double Out)[]
        {
            (Median(box.X + 3, inT, label.X - 2, inB), Median(box.X - 7, inT, box.X - 2, inB)),
            (Median(label.Right + 2, inT, box.Right - 3, inB), Median(box.Right + 2, inT, box.Right + 7, inB)),
            (Median(box.X + 3, box.Y + 2, label.X - 2, box.Y + 5), Median(box.X + 3, box.Y - 7, box.Right - 3, box.Y - 2)),
            (Median(box.X + 3, box.Bottom - 5, label.X - 2, box.Bottom - 2), Median(box.X + 3, box.Bottom + 2, box.Right - 3, box.Bottom + 7)),
        };
        var distinct = checks.Count(c => !double.IsNaN(c.In) && !double.IsNaN(c.Out) && Math.Abs(c.In - c.Out) >= 12);
        return distinct >= 3 ? box : null;
    }

    private double LineFraction(Mat grad, bool vertical, double pos, double from, double to, byte thresh)
    {
        int p = vertical ? (int)Math.Round(pos - _region.X) : (int)Math.Round(pos - _region.Y);
        int f = vertical ? (int)Math.Round(from - _region.Y) : (int)Math.Round(from - _region.X);
        int t = vertical ? (int)Math.Round(to - _region.Y) : (int)Math.Round(to - _region.X);
        int lim = vertical ? grad.Width : grad.Height;
        int along = vertical ? grad.Height : grad.Width;
        if (p < 1 || p >= lim - 1) return 0;
        f = Math.Clamp(f, 0, along - 1); t = Math.Clamp(t, 0, along - 1);
        if (t <= f) return 0;
        int hit = 0;
        for (int i = f; i <= t; i++)
        {
            // one pixel of tolerance across the line
            byte v0 = vertical ? grad.At<byte>(i, p) : grad.At<byte>(p, i);
            byte v1 = vertical ? grad.At<byte>(i, p - 1) : grad.At<byte>(p - 1, i);
            byte v2 = vertical ? grad.At<byte>(i, p + 1) : grad.At<byte>(p + 1, i);
            if (v0 >= thresh || v1 >= thresh || v2 >= thresh) hit++;
        }
        return hit / (double)(t - f + 1);
    }

    /// <summary>Share of pixels in a box that differ noticeably from another picture of the
    /// same screen - "did this change since the pointer last moved".</summary>
    public static double ChangedFraction(Mat now, Mat before, Box box)
    {
        var c = box.Clip(new Box(0, 0, Math.Min(now.Width, before.Width), Math.Min(now.Height, before.Height)));
        if (c.W < 2 || c.H < 2) return 0;
        var r = new OpenCvSharp.Rect((int)c.X, (int)c.Y, (int)c.W, (int)c.H);
        using var a = new Mat(now, r);
        using var b = new Mat(before, r);
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        using var g = new Mat();
        if (diff.Channels() == 3) Cv2.CvtColor(diff, g, ColorConversionCodes.BGR2GRAY); else diff.CopyTo(g);
        using var bin = new Mat();
        Cv2.Threshold(g, bin, 24, 255, ThresholdTypes.Binary);
        return Cv2.CountNonZero(bin) / (double)(r.Width * r.Height);
    }

    /// <summary>
    /// The rows of a text box that hold letters: the longest run of rows with strong
    /// side-to-side gradients (upright strokes), padded a little. A box the detector drew
    /// around a sentence AND a larger price set lower beside it is too tall for the sentence
    /// once the price has been cut away; this gives the sentence its own height back.
    /// </summary>
    public Box TightenVertical(Box b)
    {
        var r = ToLocal(b);
        if (r.Width < 4 || r.Height < 6 || _gx == null) return b;
        using var roi = new Mat(_gx, r);
        // Coverage, not total energy: the line's own letters run the whole width of the box,
        // while the top of the next line poking into its bottom corner covers only part of it.
        int binW = Math.Max(4, (int)Math.Round(_h * 0.8));
        int bins = Math.Max(1, r.Width / binW);
        var coverage = new double[r.Height];
        for (int row = 0; row < r.Height; row++)
        {
            int active = 0;
            for (int k = 0; k < bins; k++)
            {
                int x0 = k * binW, x1 = k == bins - 1 ? r.Width : (k + 1) * binW;
                int strong = 0;
                for (int c = x0; c < x1; c++) if (roi.At<byte>(row, c) >= 60) strong++;
                if (strong >= 1) active++;
            }
            coverage[row] = active / (double)bins;
        }
        var max = coverage.Max();
        if (max <= 0) return b;
        int bestStart = 0, bestLen = 0, start = -1, quiet = 0;
        for (int row = 0; row <= r.Height; row++)
        {
            bool on = row < r.Height && coverage[row] >= Math.Max(0.35, max * 0.55);
            if (on) { if (start < 0) start = row; quiet = 0; continue; }
            if (start < 0) continue;
            // a row or two of quiet between strokes (the waist of an "e") is still the line
            if (row < r.Height && ++quiet <= 2) continue;
            int end = row - quiet;           // one past the last strong row
            if (end - start > bestLen) { bestStart = start; bestLen = end - start; }
            start = -1; quiet = 0;
        }
        if (bestLen < 4) return b;
        double pad = bestLen * 0.2;
        var top = Math.Max(b.Y, _region.Y + r.Y + bestStart - pad);
        var bottom = Math.Min(b.Bottom, _region.Y + r.Y + bestStart + bestLen + pad);
        return bottom - top >= 4 ? Box.FromLTRB(b.X, top, b.Right, bottom) : b;
    }

    /// <summary>
    /// Where something is drawn along a band: runs of columns holding strong strokes (letters,
    /// digits, icons), left to right, in picture x. Columns a pixel or two apart join.
    /// </summary>
    public unsafe List<(double X0, double X1)> InkRuns(Box band)
    {
        var runs = new List<(double X0, double X1)>();
        var r = ToLocal(band);
        if (r.Width < 3 || r.Height < 3 || _gx == null || _gy == null) return runs;
        var gx = (byte*)_gx.DataPointer; var gy = (byte*)_gy.DataPointer;
        long sx = _gx.Step(), sy = _gy.Step();
        int start = -1, lastInk = -10;
        for (int c = 0; c <= r.Width; c++)
        {
            bool ink = false;
            if (c < r.Width)
            {
                int strong = 0;
                for (int row = r.Y; row < r.Y + r.Height && strong < 2; row++)
                    if (gx[row * sx + r.X + c] >= 60 || gy[row * sy + r.X + c] >= 60) strong++;
                ink = strong >= 2;
            }
            if (ink)
            {
                if (start < 0 || c - lastInk > 2)
                {
                    if (start >= 0) runs.Add((_region.X + r.X + start, _region.X + r.X + lastInk + 1));
                    start = c;
                }
                lastInk = c;
            }
        }
        if (start >= 0) runs.Add((_region.X + r.X + start, _region.X + r.X + lastInk + 1));
        return runs;
    }

    public void Dispose()
    {
        _gray.Dispose();
        _value.Dispose();
        _gx?.Dispose();
        _gy?.Dispose();
    }
}
