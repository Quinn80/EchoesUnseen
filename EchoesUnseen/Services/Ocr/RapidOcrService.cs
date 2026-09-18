using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RapidOcrNet;
using SkiaSharp;

namespace EchoesUnseen.Services.Ocr;

/// <summary>One line RapidOCR read, and where it was, in the picture's own pixels.</summary>
public readonly record struct OcrLineBox(string Text, double X, double Y, double W, double H, double Confidence);

/// <summary>
/// RapidOCR — PP-OCRv5 mobile models through ONNX Runtime, entirely on this machine.
///
/// WHY IT IS HERE. Measured against Quinn's own hover captures rather than a paper's
/// benchmark: 43 real grabs of her inventory, WvW tactic panels, the trading post and
/// nameplates in a crowd, every engine reading the same picture at the same upscale.
/// RapidOCR found 389 lines where Windows OCR found 259 and Tesseract 230, with the
/// lowest junk rate of the three. The full table is in RAPIDOCR-EVALUATION.md.
///
/// The difference is not academic. The same captures, side by side:
///
///     [HBLO] / -IHALOI / .1KAL0]      ->  [HALO], every time
///     Legendary Warsongt [HACOI:      ->  Legendary Warsong [HALO]
///     Ldng-TerFn Commitment           ->  Long-Term Commitment
///     Har ened Gates                  ->  Hardened Gates
///     Guards gain h-on Hide           ->  Guards gain Iron Hide
///     VeteraniQ-S-NE]                 ->  Veteran Defender [GONE]
///
/// WHAT IT IS NOT. It is not generative, it does not decide anything, it never sees the
/// network, and it has no idea it is looking at a game. It turns a picture of letters
/// into letters, which is the entire job of an accessibility overlay.
///
/// THREE SESSIONS, LOADED ONCE. Detection, orientation and recognition are separate
/// ONNX models and loading them costs about four hundred milliseconds. That happens on
/// the first hover and never again; a reader that reloaded its models for every hover
/// would be slower than the thing it replaced.
/// </summary>
public static class RapidOcrService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static RapidOcr? _ocr;
    private static bool _failed;

    /// <summary>Milliseconds the last recognition took, for the diagnostics log.</summary>
    public static double LastMs { get; private set; }

    /// <summary>Is the engine loaded and usable? False until the first successful read.</summary>
    public static bool IsReady => _ocr != null;

    /// <summary>Did the engine try and fail? Distinguishes "not yet" from "not here".</summary>
    public static bool Unavailable => _failed;

    /// <summary>
    /// What the hover reader will actually do, in words, for the diagnostics log and the
    /// bug report.
    ///
    /// The report used to say "ocr: tesseract" while the log showed Windows OCR running
    /// first and Tesseract only as a fallback. A diagnostic that describes a setting
    /// rather than the behaviour is worse than none: it sent me looking in the wrong
    /// engine more than once.
    /// </summary>
    public static string ModeDescription =>
        _failed ? "Windows OCR (RapidOCR unavailable on this machine)"
        : _ocr != null ? "Automatic - RapidOCR primary, Windows OCR fallback"
        : "Automatic - RapidOCR primary (not loaded yet), Windows OCR fallback";

    /// <summary>
    /// Load the models now, quietly, so the first hover does not pay for it.
    ///
    /// Three ONNX sessions take around four hundred milliseconds to come up. Doing that
    /// during the first hover would make the very first thing Quinn asks for the slowest
    /// answer she ever gets, which is a poor first impression of a reader whose whole
    /// promise is speed. Failures here are silent by design - the reader falls back to
    /// Windows OCR and she never needs to know.
    /// </summary>
    public static void WarmUp()
    {
        _ = Task.Run(async () =>
        {
            try { await GetEngineAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* the fallback covers it */ }
        });
    }

    /// <summary>
    /// Read a PNG and return every line with its box, or an empty list if the engine
    /// could not be brought up.
    ///
    /// Cancellation is honoured: the pointer moves far more often than a read finishes,
    /// and speaking the answer to a question the player has stopped asking is worse than
    /// saying nothing.
    /// </summary>
    public static async Task<List<OcrLineBox>> ReadAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        var empty = new List<OcrLineBox>();
        if (_failed || pngBytes == null || pngBytes.Length == 0) return empty;

        var engine = await GetEngineAsync(ct).ConfigureAwait(false);
        if (engine == null) return empty;

        try
        {
            var started = DateTime.UtcNow;
            using var bitmap = SKBitmap.Decode(pngBytes);
            if (bitmap == null) return empty;

            ct.ThrowIfCancellationRequested();

            // Off the UI thread, and abandoned if the pointer has moved on.
            var result = await Task.Run(() => engine.Detect(bitmap, RapidOcrOptions.Default), ct)
                                   .ConfigureAwait(false);
            LastMs = (DateTime.UtcNow - started).TotalMilliseconds;

            var lines = new List<OcrLineBox>();
            foreach (var block in result.TextBlocks ?? Array.Empty<TextBlock>())
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;

                var pts = block.BoxPoints;
                if (pts == null || pts.Length == 0) continue;

                double l = pts.Min(p => (double)p.X), r = pts.Max(p => (double)p.X);
                double t = pts.Min(p => (double)p.Y), b = pts.Max(p => (double)p.Y);
                if (r <= l || b <= t) continue;

                var conf = block.CharScores is { Length: > 0 } cs ? cs.Average() * 100 : 0;
                lines.Add(new OcrLineBox(block.Text.Trim(), l, t, r - l, b - t, conf));
            }
            return lines;
        }
        catch (OperationCanceledException) { return empty; }
        catch (Exception ex)
        {
            CrashLogger.Log("RapidOcrService.Read", ex);
            return empty;
        }
    }

    private static Hover.Fusion.MatOcr? _matOcr;
    private static TextRecognizer? _recognizer;

    /// <summary>
    /// The same loaded models, shaped for the OpenCV + RapidOCR hover targeting: the full
    /// pipeline for finding text, and the recognition model on its own for reading chosen
    /// lines. Null when RapidOCR cannot run here.
    /// </summary>
    public static async Task<Hover.Fusion.MatOcr?> GetMatOcrAsync(CancellationToken ct)
    {
        if (_matOcr != null) return _matOcr;
        // Loading is never abandoned half way: the pointer moving on cancels the hover, not the
        // models. A cancelled hover is reported as cancelled - returning null here would read as
        // "cannot load" and switch the new targeting off for the rest of the session.
        var loading = LoadMatOcrAsync();
        var done = await Task.WhenAny(loading, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        if (done != loading) ct.ThrowIfCancellationRequested();
        return await loading.ConfigureAwait(false);
    }

    private static Task<Hover.Fusion.MatOcr?>? _matOcrLoad;

    private static Task<Hover.Fusion.MatOcr?> LoadMatOcrAsync()
    {
        lock (typeof(RapidOcrService)) return _matOcrLoad ??= Task.Run(LoadMatOcrCoreAsync);
    }

    private static async Task<Hover.Fusion.MatOcr?> LoadMatOcrCoreAsync()
    {
        var engine = await GetEngineAsync(CancellationToken.None).ConfigureAwait(false);
        if (engine == null) return null;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_matOcr != null) return _matOcr;
            var dir = ExtractModels();
            if (dir == null) return null;
            var rec = new TextRecognizer();
            rec.InitModel(Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
                          Path.Combine(dir, "ppocrv5_latin_dict.txt"),
                          new Microsoft.ML.OnnxRuntime.SessionOptions());
            _recognizer = rec;
            _matOcr = new Hover.Fusion.MatOcr(engine, rec);
            DiagLog.Log("OCR", "OpenCV + RapidOCR hover targeting ready");
            return _matOcr;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RapidOcrService.GetMatOcr", ex);
            return null;
        }
        finally { _gate.Release(); }
    }

    private static async Task<RapidOcr?> GetEngineAsync(CancellationToken ct)
    {
        if (_ocr != null) return _ocr;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ocr != null) return _ocr;
            if (_failed) return null;

            var dir = ExtractModels();
            if (dir == null) { _failed = true; return null; }

            var started = DateTime.UtcNow;
            var ocr = new RapidOcr();

            // Loading three ONNX sessions blocks for a moment; never on the UI thread.
            await Task.Run(() => ocr.InitModels(
                Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                Path.Combine(dir, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
                Path.Combine(dir, "ppocrv5_latin_dict.txt"),
                new Microsoft.ML.OnnxRuntime.SessionOptions()), ct).ConfigureAwait(false);

            DiagLog.Log("OCR", $"RapidOCR ready in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
            _ocr = ocr;
            return _ocr;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            // A machine that cannot run ONNX still gets a working reader; it just falls
            // back to Windows OCR, which is why this is a log line and not a crash.
            CrashLogger.Log("RapidOcrService.Init", ex);
            DiagLog.Log("OCR", "RapidOCR unavailable - falling back to Windows OCR");
            _failed = true;
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Unpack the models beside the other extracted runtime bits, once.
    ///
    /// The NuGet copies them next to the binary, and a single-file exe has no next-to,
    /// so they travel as embedded resources and land in AppData the first time they are
    /// needed - the same arrangement as Tesseract's native libraries and NVDA's
    /// controller client.
    /// </summary>
    private static string? ExtractModels()
    {
        try
        {
            var dir = Path.Combine(App.Settings.AppDataDirectory, "rapidocr");
            Directory.CreateDirectory(dir);

            foreach (var name in new[]
            {
                "ch_PP-OCRv5_mobile_det.onnx",
                "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
                "latin_PP-OCRv5_rec_mobile_infer.onnx",
                "ppocrv5_latin_dict.txt",
            })
            {
                var dest = Path.Combine(dir, name);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0) continue;

                var asm = typeof(RapidOcrService).Assembly;
                var res = asm.GetManifestResourceNames()
                             .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                if (res == null)
                {
                    DiagLog.Log("OCR", $"RapidOCR model missing from the build: {name}");
                    return null;
                }

                using var src = asm.GetManifestResourceStream(res);
                if (src == null) return null;
                using var f = File.Create(dest);
                src.CopyTo(f);
            }
            return dir;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("RapidOcrService.ExtractModels", ex);
            return null;
        }
    }
}
