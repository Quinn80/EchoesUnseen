using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace EchoesUnseen.Services;

/// <summary>
/// Image conditioning for OCR. This is plain image maths — no AI, no ML, no
/// network — but it is the single biggest factor in whether Windows OCR can
/// read Guild Wars 2's interface.
///
/// WHY IT'S NEEDED
///   Game text is small (~12px), anti-aliased, and drawn semi-transparently
///   over a moving 3D scene. Windows OCR is trained on document-sized text on
///   flat backgrounds, so fed raw screen pixels it returns fragments and
///   nonsense — "[10:24 AM]" comes back as "1024 AVij[S]".
///
/// WHAT IT DOES — AND WHAT IT DELIBERATELY DOESN'T
///   Upscales 3x with high-quality bicubic interpolation. That's all.
///
///   This was measured, not guessed. Against pale semi-transparent text over a
///   busy scene (averaged over several random backgrounds), word recovery was:
///
///       raw, no preprocessing ....... 65%
///       2x bicubic .................. 72%
///       3x bicubic .................. 80%   <-- chosen
///       4x bicubic .................. 79%
///       greyscale only .............. 66%
///       3x bicubic + greyscale ...... 78%
///
///   Greyscaling and contrast-stretching both made things WORSE: they harden the
///   anti-aliased edges Windows OCR relies on to resolve small glyphs. An earlier
///   version of this file did both and dropped accuracy from 85% to 55% on a
///   single sample. Resist re-adding them without re-running the numbers.
/// </summary>
public static class ImagePrep
{
    /// <summary>Scale factor applied before OCR. 3x is the sweet spot: bigger
    /// helps accuracy but costs time, and past ~4x the gains flatten out.</summary>
    private const int Scale = 3;

    /// <summary>
    /// Enhance a captured PNG for OCR. Returns the original bytes unchanged if
    /// anything goes wrong — a worse image is always better than no reading.
    ///
    /// <paramref name="binarize"/> adds greyscale + Otsu thresholding on top of the
    /// upscale. This is ENGINE-SPECIFIC: it HELPS Tesseract (which is trained on
    /// clean black-on-white scans and wants hard binary edges) but HURTS Windows
    /// OCR (which leans on the anti-aliased edges binarizing destroys — see the
    /// measured numbers above). So Windows OCR passes false; Tesseract passes true.
    /// </summary>
    /// <param name="upscaleOverride">
    /// Force the upscale instead of guessing it from the picture's size. What OCR
    /// actually cares about is how tall the LETTERS are, and the same 880x640 grab
    /// holds letters of very different sizes depending on the player's interface size
    /// and display scaling. A caller that has measured the text should say so.
    /// </param>
    /// <summary>Pixel height of a PNG without decoding it fully into a working bitmap.
    /// Used to undo the upscale when working out how tall the text was.</summary>
    public static double HeightOf(byte[] pngBytes)
    {
        try
        {
            using var src = LoadBitmap(pngBytes);
            return src.Height;
        }
        catch { return 0; }
    }

    public static byte[] EnhanceForOcr(byte[] pngBytes, bool binarize = false, int? upscaleOverride = null)
    {
        if (pngBytes == null || pngBytes.Length == 0) return pngBytes ?? Array.Empty<byte>();

        try
        {
            using var src = LoadBitmap(pngBytes);
            if (src.Width == 0 || src.Height == 0) return pngBytes;

            // Small regions (a chat box, a tooltip) are where the text is tiniest
            // and OCR struggles most, so give them a bigger upscale. Large grabs
            // fall back to keep memory and time sane.
            // Small grabs get 4x, big ones 3x - and the hover grab (880x640) is a big
            // one on purpose now.
            //
            // I raised this threshold when the box grew, to keep hover at 4x, and the
            // first full-size grabs out of a bug report say that was wrong. On a real
            // tooltip 3x and 4x return the same words. On a real nameplate the SMALLER
            // upscale reads better - 2x got "Warsong" and a second player's name where
            // 4x gave "Warsongt" and missed them. Blowing a screen grab up invents edges
            // as readily as it reveals letters. 3x costs 44% fewer pixels than 4x for no
            // measured loss, which on the immediate lane is worth having.
            int factor = upscaleOverride is int given
                ? Math.Clamp(given, 2, 6)
                : src.Width * src.Height <= 400_000 ? 4 : Scale;
            long scaled = (long)src.Width * factor * src.Height * factor;
            if (scaled > 40_000_000) factor = 2;

            int w = src.Width * factor, h = src.Height * factor;

            using var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h));
            }

            if (binarize) Binarize(big);

            using var ms = new MemoryStream();
            big.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ImagePrep.EnhanceForOcr", ex);
            return pngBytes;
        }
    }

    /// <summary>
    /// Greyscale + Otsu thresholding, in place — turns the upscaled grab into pure
    /// black text on white, which is what Tesseract reads best. Classical image
    /// maths only: builds a luminance histogram, finds the threshold that best
    /// separates dark from light (Otsu's method), then paints every pixel one or
    /// the other. Chat text is bright on a dark box, so we output text as BLACK on
    /// WHITE (Tesseract's preferred polarity) by treating bright pixels as ink.
    /// </summary>
    private static unsafe void Binarize(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* baseP = (byte*)data.Scan0;
            int stride = data.Stride, wpx = bmp.Width, hpx = bmp.Height;
            var hist = new long[256];

            // Pass 1: luminance histogram.
            for (int y = 0; y < hpx; y++)
            {
                byte* row = baseP + y * stride;
                for (int x = 0; x < wpx; x++)
                {
                    byte* px = row + x * 4;               // BGRA
                    int lum = (px[2] * 299 + px[1] * 587 + px[0] * 114) / 1000;
                    hist[lum]++;
                }
            }

            // Otsu: pick the threshold that maximises between-class variance.
            long total = (long)wpx * hpx;
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
            double sumB = 0; long wB = 0; double best = -1; int thresh = 127;
            for (int i = 0; i < 256; i++)
            {
                wB += hist[i];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;
                sumB += i * (double)hist[i];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best) { best = between; thresh = i; }
            }

            // Nudge the threshold down a touch so faint / anti-aliased edges of the
            // (bright) text are still captured as ink — thin game characters were
            // breaking up under a hard Otsu cut. Downstream word filters drop noise.
            thresh = Math.Max(1, (int)(thresh * 0.88));

            // Pass 2: paint. Bright pixels (>= threshold) are the text -> black ink;
            // the dark box background -> white.
            for (int y = 0; y < hpx; y++)
            {
                byte* row = baseP + y * stride;
                for (int x = 0; x < wpx; x++)
                {
                    byte* px = row + x * 4;
                    int lum = (px[2] * 299 + px[1] * 587 + px[0] * 114) / 1000;
                    byte v = (byte)(lum >= thresh ? 0 : 255);
                    px[0] = px[1] = px[2] = v; px[3] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    private static Bitmap LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        // Copy out of the stream-backed image so the stream can be disposed.
        using var loaded = new Bitmap(ms);
        return new Bitmap(loaded);
    }

}
