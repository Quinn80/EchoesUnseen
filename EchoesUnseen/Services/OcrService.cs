using System.IO;
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
    public static async Task<List<string>> ReadLinesAsync(byte[] pngBytes)
    {
        var lines = new List<string>();
        if (pngBytes == null || pngBytes.Length == 0) return lines;

        try
        {
            pngBytes = ImagePrep.EnhanceForOcr(pngBytes);

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

            foreach (var line in result.Lines)
            {
                var text = line.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) lines.Add(text);
            }
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
