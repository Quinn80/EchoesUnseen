using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using OpenCvSharp;
using RapidOcrNet;
using SkiaSharp;

namespace EchoesUnseen.Services.Hover.Fusion;

/// <summary>
/// RapidOCR over part of an OpenCV picture, with every box handed back in the PICTURE's
/// pixels.
///
/// Two jobs, deliberately separate:
///
///   Detect - DBNet only. Where the lines of text are, no reading. This is the cheap
///            "text geometry" the targeting needs over a wide area.
///   Read   - detection and recognition, for the one or two regions targeting chose.
///
/// Reading everything near the pointer and deciding afterwards is the mistake the whole
/// hover reader has spent weeks climbing out of. Finding the object first and reading
/// only that is the point of this design.
/// </summary>
public sealed class MatOcr
{
    private readonly RapidOcr _engine;
    private readonly TextRecognizer? _recognizer;
    private readonly object _gate = new();

    /// <param name="engine">The loaded RapidOCR pipeline (detector, classifier, recogniser).</param>
    /// <param name="recognizer">The same recognition model on its own, for reading chosen
    /// line crops without running detection again. Optional; without it Recognize falls
    /// back to a full read of each crop.</param>
    public MatOcr(RapidOcr engine, TextRecognizer? recognizer = null)
    {
        _engine = engine;
        _recognizer = recognizer;
    }

    /// <summary>
    /// Read particular lines: each box cropped from the full-resolution picture and handed to
    /// the recogniser alone. This is how a line found cheaply at low resolution is read
    /// sharply, and why reading costs only the lines that matter.
    ///
    /// Character positions come back too (CharX, picture pixels), from the recogniser's own
    /// column alignment - enough to tell "15" from "36" in a price the recogniser ran together.
    /// </summary>
    public List<TextLine> Recognize(Mat picture, IReadOnlyList<Box> boxes, double lineHeight, CancellationToken ct = default)
    {
        var result = new List<TextLine>(boxes.Count);
        if (boxes.Count == 0) return result;
        if (_recognizer == null)
        {
            foreach (var b in boxes)
            {
                var got = Read(picture, b.Inflate(lineHeight * 0.3, lineHeight * 0.15), Math.Max(1, 40.0 / Math.Max(8, b.H)), ct);
                result.Add(got.Count == 0 ? new TextLine("", b, 0)
                    : new TextLine(string.Join(" ", got.OrderBy(g => g.Box.X).Select(g => g.Text)), b, got.Average(g => g.Confidence)));
            }
            return result;
        }

        var crops = new List<(SKBitmap Bmp, Box Crop, double Scale)>();
        try
        {
            foreach (var b in boxes)
            {
                var crop = b.Inflate(Math.Max(2, b.H * CropPadX), b.H * CropPadY)
                            .Clip(new Box(0, 0, picture.Width, picture.Height));
                int x = (int)Math.Floor(crop.X), y = (int)Math.Floor(crop.Y);
                int w = Math.Max(4, Math.Min(picture.Width - x, (int)Math.Ceiling(crop.W)));
                int h = Math.Max(4, Math.Min(picture.Height - y, (int)Math.Ceiling(crop.H)));
                using var roi = new Mat(picture, new OpenCvSharp.Rect(x, y, w, h));
                double k = h < 40 ? 48.0 / h : 1.0;
                using var sized = new Mat();
                if (k > 1) Cv2.Resize(roi, sized, new OpenCvSharp.Size((int)Math.Round(w * k), 48), 0, 0, InterpolationFlags.Cubic);
                else roi.CopyTo(sized);
                crops.Add((ToSkBitmap(sized), new Box(x, y, w, h), k));
            }

            RapidOcrNet.TextLine[] lines;
            lock (_gate) lines = _recognizer.GetTextLines(crops.Select(c => c.Bmp).ToArray(), null, ct);

            for (int i = 0; i < boxes.Count; i++)
            {
                var l = i < lines.Length ? lines[i] : null;
                if (l == null || l.Chars == null || l.Chars.Length == 0) { result.Add(new TextLine("", boxes[i], 0)); continue; }
                var text = Regex.Replace(string.Concat(l.Chars).Trim(), @"\s{2,}", " ");
                double[]? xs = null;
                if (l.CharCols is { Length: > 0 } cols && l.ColCount > 0)
                    xs = cols.Select(c => crops[i].Crop.X + (c + 0.5) / l.ColCount * crops[i].Crop.W).ToArray();
                var conf = l.CharScores is { Length: > 0 } cs ? cs.Average() * 100 : 0;
                result.Add(new TextLine(text, boxes[i], conf, xs));
            }
            return result;
        }
        finally
        {
            foreach (var c in crops) c.Bmp.Dispose();
        }
    }

    /// <summary>Options for a region we have already enlarged ourselves: no further
    /// resizing by the engine, a little padding so edge glyphs are not clipped.</summary>
    private static RapidOcrOptions OptionsFor(int w, int h) => RapidOcrOptions.Default with
    {
        ImgResize = 0,
        Padding = 16,
        MaxSideLen = Math.Max(2000, Math.Max(w, h) + 64),
        // The detector otherwise enlarges any picture whose short side is under 736 px up
        // to 736 - a fixed cost of ~150 ms on every small region, for text that is already
        // large enough to find. Our regions are prepared at the scale we want.
        LimitSideLen = LimitSideLen,
        DoAngle = false,
    };

    /// <summary>Padding added around a detector box before it is read, as a share of its height.
    /// Vertical padding is NEGATIVE by default: boxes found on a reduced picture come back a
    /// little tall, and the recogniser, which scales every crop to a fixed height, then sees
    /// smaller letters - "Enchanted Rler Beetle Skin" instead of "Roller".</summary>
    public static double CropPadX { get; set; } = 0.2;
    public static double CropPadY { get; set; } = -0.08;

    /// <summary>Exposed for the latency probe only.</summary>
    public static int LimitSideLen { get; set; } = 32;

    /// <summary>Text line rectangles inside <paramref name="region"/>, in picture pixels.</summary>
    public IReadOnlyList<Box> Detect(Mat picture, Box region, double upscale, CancellationToken ct = default)
    {
        using var prepared = Prepare(picture, region, upscale, out var roi);
        if (prepared == null) return Array.Empty<Box>();
        using var bmp = ToSkBitmap(prepared);
        IReadOnlyList<RapidOcrNet.TextBox> found;
        lock (_gate) found = _engine.DetectBoxes(bmp, OptionsFor(prepared.Width, prepared.Height), ct);

        var list = new List<Box>(found.Count);
        foreach (var b in found)
        {
            var box = BoundsOf(b.BoxPoints);
            if (box.IsEmpty) continue;
            list.Add(new Box(roi.X + box.X / upscale, roi.Y + box.Y / upscale, box.W / upscale, box.H / upscale));
        }
        return list;
    }

    /// <summary>Lines with their text inside <paramref name="region"/>, in picture pixels.</summary>
    public IReadOnlyList<TextLine> Read(Mat picture, Box region, double upscale, CancellationToken ct = default,
                                        RapidOcrOptions? options = null)
    {
        using var prepared = Prepare(picture, region, upscale, out var roi);
        if (prepared == null) return Array.Empty<TextLine>();
        using var bmp = ToSkBitmap(prepared);
        OcrResult result;
        lock (_gate) result = _engine.Detect(bmp, options ?? OptionsFor(prepared.Width, prepared.Height), ct);

        var list = new List<TextLine>();
        foreach (var block in result.TextBlocks ?? Array.Empty<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var box = BoundsOf(block.BoxPoints);
            if (box.IsEmpty) continue;
            var conf = block.CharScores is { Length: > 0 } cs ? cs.Average() * 100 : 0;
            list.Add(new TextLine(Regex.Replace(block.Text.Trim(), @"\s{2,}", " "),
                new Box(roi.X + box.X / upscale, roi.Y + box.Y / upscale, box.W / upscale, box.H / upscale),
                conf));
        }
        return list;
    }

    private static Mat? Prepare(Mat picture, Box region, double upscale, out Box roi)
    {
        var r = region.Clip(new Box(0, 0, picture.Width, picture.Height));
        int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
        int w = (int)Math.Ceiling(r.Right) - x, h = (int)Math.Ceiling(r.Bottom) - y;
        w = Math.Min(w, picture.Width - x); h = Math.Min(h, picture.Height - y);
        roi = new Box(x, y, w, h);
        if (w < 4 || h < 4) return null;

        using var crop = new Mat(picture, new OpenCvSharp.Rect(x, y, w, h));
        var outMat = new Mat();
        if (Math.Abs(upscale - 1.0) < 1e-6) crop.CopyTo(outMat);
        else Cv2.Resize(crop, outMat, new OpenCvSharp.Size((int)Math.Round(w * upscale), (int)Math.Round(h * upscale)),
                        0, 0, upscale > 1 ? InterpolationFlags.Cubic : InterpolationFlags.Area);
        return outMat;
    }

    private static Box BoundsOf(SKPointI[]? pts)
    {
        if (pts == null || pts.Length == 0) return default;
        double l = pts.Min(p => (double)p.X), r = pts.Max(p => (double)p.X);
        double t = pts.Min(p => (double)p.Y), b = pts.Max(p => (double)p.Y);
        return r > l && b > t ? Box.FromLTRB(l, t, r, b) : default;
    }

    /// <summary>BGR (or BGRA/grey) Mat to a Skia bitmap, copied row by row.</summary>
    public static unsafe SKBitmap ToSkBitmap(Mat src)
    {
        using var bgra = new Mat();
        if (src.Channels() == 4) src.CopyTo(bgra);
        else if (src.Channels() == 3) Cv2.CvtColor(src, bgra, ColorConversionCodes.BGR2BGRA);
        else Cv2.CvtColor(src, bgra, ColorConversionCodes.GRAY2BGRA);

        var bmp = new SKBitmap(new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var dst = (byte*)bmp.GetPixels();
        int rowBytes = bgra.Width * 4;
        for (int y = 0; y < bgra.Height; y++)
        {
            var srcRow = (byte*)bgra.Ptr(y);
            Buffer.MemoryCopy(srcRow, dst + (long)y * bmp.RowBytes, rowBytes, rowBytes);
        }
        return bmp;
    }
}
