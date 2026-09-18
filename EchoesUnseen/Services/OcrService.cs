using System.IO;
using WpfRect = System.Windows.Rect;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace EchoesUnseen.Services;

/// <summary>
/// Windows Media OCR wrapper.
///
/// WHY THIS FIXES THE FRAGMENTATION BUG ARCHITECTURALLY:
///   The previous Electron build used Tesseract.js which defaults to
///   PSM 3 (auto page segmentation with layout analysis). That mode
///   fragments small game text into one character per line. We had to
///   force PSM 6 (uniform text block) with preserve_interword_spaces=1
///   to make it usable for GW2 chat.
///
///   Windows.Media.Ocr has no PSM modes. It's trained on arbitrary
///   screen content and handles multi-line text correctly out of the
///   box. The bug is simply not possible here.
///
/// LANGUAGE SELECTION:
///   TryCreateFromUserProfileLanguages() picks the best match for the
///   user's Windows display language. If no OCR language is installed
///   (rare — Windows 10+ ships with English by default), we fall back
///   to TryCreateFromLanguage("en-US") and show a warning.
///
/// USAGE:
///   var text = await OcrService.ReadAsync(pngBytes);
///   (Returns full recognized text, one space between words, newlines
///    preserved between distinct text regions.)
/// </summary>
public static class OcrService
{
    /// <summary>
    /// OCR the provided PNG bytes and return recognized text.
    /// Returns an empty string if the image has no detectable text.
    /// </summary>
    public static async Task<string> ReadAsync(byte[] pngBytes)
    {
        if (pngBytes == null || pngBytes.Length == 0) return "";

        // Route to Tesseract when chosen. If it isn't downloaded yet, kick off the
        // one-time download and use Windows OCR for now; it switches over once ready.
        if (App.Settings.Current.OcrEngine == "tesseract")
        {
            if (TesseractOcrService.IsReady) return await TesseractOcrService.ReadAsync(pngBytes);
            _ = TesseractOcrService.EnsureReadyAsync();
        }

        try
        {
            // 0. Condition the image first. Windows OCR is built for document-
            //    sized text on flat backgrounds; Guild Wars 2 draws ~12px
            //    anti-aliased text over a moving 3D scene. Without upscaling and
            //    contrast work it returns fragments ("[10:24 AM]" → "1024 AVij[S]").
            //    This is plain image maths — no AI involved.
            pngBytes = ImagePrep.EnhanceForOcr(pngBytes);

            // 1. Wrap the byte[] in an InMemoryRandomAccessStream (WinRT stream type)
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            // 2. Decode the PNG into a SoftwareBitmap
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            // 3. Create the OCR engine (preferred: user's display language)
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null)
            {
                CrashLogger.Log("OcrService.ReadAsync",
                    new Exception("No OCR engine could be created. Is an OCR language pack installed?"));
                return "";
            }

            // 4. Recognize
            var result = await engine.RecognizeAsync(softwareBitmap);
            DiagLog.Ocr("read", "windows", softwareBitmap.PixelWidth, softwareBitmap.PixelHeight, result?.Text ?? "");
            return result?.Text ?? "";
        }
        catch (Exception ex)
        {
            CrashLogger.Log("OcrService.ReadAsync", ex);
            return "";
        }
    }

    /// <summary>
    /// OCR and return the recognised text as SEPARATE LINES, using the engine's
    /// own line segmentation rather than splitting a flattened string.
    ///
    /// This matters for chat: OcrResult.Text runs everything together, so two
    /// messages could merge into one "line" and a single message could be split
    /// by a stray newline — which then defeats the de-duplication and makes the
    /// same text get re-read. OcrResult.Lines uses the engine's own layout
    /// analysis and keeps one chat message per entry.
    /// </summary>
    /// <param name="forceWindows">
    /// Skip the configured engine and use Windows OCR. For text drawn INTO the world -
    /// player nameplates, interaction prompts, map markers - where Tesseract, trained
    /// on scanned pages, returns nothing at all and Windows OCR, trained on scene
    /// text, reads it.
    /// </param>
    /// <summary>
    /// Where each line from the last <see cref="ReadLinesAsync"/> sat, as a fraction of
    /// the picture (0..1). Same order and length as the returned list. Only filled by
    /// the Windows engine, which is the one that reports geometry.
    /// </summary>
    public static List<WpfRect>? LastLineBoxes { get; private set; }

    public static async Task<List<string>> ReadLinesAsync(
        byte[] pngBytes,
        TesseractOcrService.Layout layout = TesseractOcrService.Layout.Block,
        bool forceWindows = false,
        int? upscaleOverride = null)
    {
        var lines = new List<string>();
        LastLineBoxes = null;      // stale geometry is worse than none
        if (pngBytes == null || pngBytes.Length == 0) return lines;

        if (!forceWindows && App.Settings.Current.OcrEngine == "tesseract")
        {
            if (TesseractOcrService.IsReady) return await TesseractOcrService.ReadLinesAsync(pngBytes, layout);
            _ = TesseractOcrService.EnsureReadyAsync();
        }

        try
        {
            pngBytes = ImagePrep.EnhanceForOcr(pngBytes, upscaleOverride: upscaleOverride);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null) return lines;

            var result = await engine.RecognizeAsync(softwareBitmap);
            if (result?.Lines == null) return lines;


            double imgW = softwareBitmap.PixelWidth, imgH = softwareBitmap.PixelHeight;
            LastLineBoxes = new List<WpfRect>();

            foreach (var line in result.Lines)
            {
                var text = line.Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                lines.Add(text);

                // WHERE the line was, as a fraction of the picture, so a caller can ask
                // which line the pointer is actually on without knowing anything about
                // the upscale factor. Union of the word rectangles - OcrLine itself
                // does not carry one.
                double l = double.MaxValue, t = double.MaxValue, r = 0, b = 0;
                foreach (var word in line.Words)
                {
                    var wr = word.BoundingRect;
                    l = Math.Min(l, wr.Left);   t = Math.Min(t, wr.Top);
                    r = Math.Max(r, wr.Right);  b = Math.Max(b, wr.Bottom);
                }
                LastLineBoxes.Add(l <= r && imgW > 0 && imgH > 0
                    ? new WpfRect(l / imgW, t / imgH, (r - l) / imgW, (b - t) / imgH)
                    : new WpfRect(0, 0, 1, 1));   // no words: treat as "everywhere"
            }

            // HOW BIG IS THE TEXT, REALLY?
            //
            // Quinn plays at Larger interface size with display scaling on; the next
            // person will not. The median line height, divided back out of the upscale,
            // is the height of a line of text in the untouched grab - the one number
            // that says whether this player's interface is small or huge. It steers the
            // next read rather than this one, which costs nothing and settles after a
            // single hover.
            DiagLog.Ocr("lines", "windows", softwareBitmap.PixelWidth, softwareBitmap.PixelHeight, string.Join("\n", lines));
            return lines;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("OcrService.ReadLinesAsync", ex);
            return lines;
        }
    }

    /// <summary>Check that an OCR engine is available. Call on app startup to show a warning if not.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() != null
                || OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) != null;
        }
        catch { return false; }
    }
}
