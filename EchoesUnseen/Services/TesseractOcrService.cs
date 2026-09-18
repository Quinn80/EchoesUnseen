using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using Tesseract;

namespace EchoesUnseen.Services;

/// <summary>
/// Optional Tesseract OCR engine — an alternative to Windows OCR that often reads
/// small, stylised game text better. Fully offline once set up; the English LSTM
/// model (eng.traineddata) is downloaded once on first use, like the voices, so
/// the app download stays small.
///
/// Uses PSM 6 (single uniform block) — the mode that keeps GW2 chat/tooltip text
/// as whole lines instead of fragmenting it one character per line.
/// </summary>
public static class TesseractOcrService
{
    private const string ModelUrl =
        "https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata";

    private static readonly object _lock = new();
    private static TesseractEngine? _engine;
    private static string? _dataDir;
    private static bool _downloading;

    private static string DataDir
    {
        get
        {
            _dataDir ??= Path.Combine(App.Settings.AppDataDirectory, "tessdata");
            return _dataDir;
        }
    }

    /// <summary>True once the model is present and the engine is ready.</summary>
    public static bool IsReady
    {
        get { lock (_lock) return _engine != null; }
    }

    /// <summary>Ensure the model is downloaded (once). Safe to call repeatedly;
    /// only one download runs. Returns true when ready.</summary>
    public static async Task<bool> EnsureReadyAsync()
    {
        if (IsReady) return true;
        var modelPath = Path.Combine(DataDir, "eng.traineddata");
        if (!File.Exists(modelPath))
        {
            lock (_lock) { if (_downloading) return false; _downloading = true; }
            try
            {
                Directory.CreateDirectory(DataDir);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var bytes = await http.GetByteArrayAsync(ModelUrl);
                await File.WriteAllBytesAsync(modelPath, bytes);
            }
            catch (Exception ex) { CrashLogger.Log("TesseractOcr download", ex); return false; }
            finally { lock (_lock) _downloading = false; }
        }
        return InitEngine();
    }

    private static bool InitEngine()
    {
        lock (_lock)
        {
            if (_engine != null) return true;
            try
            {
                EnsureNativeLibs();
                _engine = new TesseractEngine(DataDir, "eng", EngineMode.LstmOnly);
                _engine.SetVariable("preserve_interword_spaces", "1");
                // Restrict output to the characters that actually appear in GW2
                // chat/UI. Stops Tesseract inventing math symbols, accents and
                // box-drawing glyphs out of anti-aliasing noise — the erratic
                // debris we were then trying to strip downstream.
                _engine.SetVariable("tessedit_char_whitelist",
                    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 []():,.!?'\"/+-%");
                return true;
            }
            catch (Exception ex) { CrashLogger.Log("TesseractOcr init", ex); return false; }
        }
    }

    private static bool _nativeReady;

    /// <summary>Extract the embedded Tesseract/Leptonica native DLLs and point the
    /// loader at them, so the single-file exe works with no loose x64 folder.</summary>
    private static void EnsureNativeLibs()
    {
        if (_nativeReady) return;
        try
        {
            var dir = Path.Combine(App.Settings.AppDataDirectory, "tesslib");
            var x64 = Path.Combine(dir, "x64");
            Directory.CreateDirectory(x64);
            Extract("leptonica-1.82.0.dll", x64);
            Extract("tesseract50.dll", x64);
            try { InteropDotNet.LibraryLoader.Instance.CustomSearchPath = dir; } catch { }
            try { SetDllDirectory(x64); } catch { }
        }
        catch (Exception ex) { CrashLogger.Log("TesseractOcr native", ex); }
        _nativeReady = true;
    }

    private static void Extract(string fileName, string destDir)
    {
        var dest = Path.Combine(destDir, fileName);
        if (File.Exists(dest)) return;
        var asm = typeof(TesseractOcrService).Assembly;
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (res == null) return;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return;
        using var f = File.Create(dest);
        s.CopyTo(f);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    /// <summary>
    /// How sure Tesseract was about the LAST read, 0-100.
    ///
    /// This is the difference between "the tooltip says Berserker's Helm" and "there is
    /// no text here, only grass". Reading a game tooltip drawn over the 3-D scene often
    /// produces confident-looking nonsense - "ERY NT SRE", "TI We. WENA" - and without a
    /// confidence figure there is no way to tell that from a real answer, so it gets read
    /// aloud. Tesseract knows; it was simply never asked.
    /// </summary>
    public static float LastConfidence { get; private set; }

    public static async Task<string> ReadAsync(byte[] pngBytes)
    {
        if (!await EnsureReadyAsync()) return "";
        try
        {
            pngBytes = ImagePrep.EnhanceForOcr(pngBytes, binarize: true);
            lock (_lock)
            {
                using var img = Pix.LoadFromMemory(pngBytes);
                using var page = _engine!.Process(img, PageSegMode.SingleBlock);
                var txt = page.GetText()?.Trim() ?? "";
                LastConfidence = page.GetMeanConfidence() * 100f;
                DiagLog.Ocr("read", "tesseract", img.Width, img.Height,
                            $"[conf {LastConfidence:F0}] {txt}");
                return txt;
            }
        }
        catch (Exception ex) { CrashLogger.Log("TesseractOcr.ReadAsync", ex); return ""; }
    }

    /// <summary>
    /// How the page is laid out, from the caller's point of view.
    ///
    /// <b>Block</b> means "this is one run of text lines" and is right for the chat
    /// panel. <b>Mixed</b> asks Tesseract to work the layout out for itself, and is
    /// for the hover reader, whose capture box lands on a tooltip with an inventory
    /// grid beside it.
    ///
    /// The difference is not academic. Told a two-column panel is one block,
    /// Tesseract reads straight across it:
    ///
    ///     "Type:                    Controlled by:"
    ///     "Tower                    Dwayna's Te"
    ///
    /// which is two columns of a WvW objective panel welded into one line each, so no
    /// amount of filtering afterwards can separate them - the junk IS the line. Asked
    /// to find the layout first, the same picture reads as "Controlled by:",
    /// "Dwayna's Te", "Held for:", "22m, 57s".
    ///
    /// It also fixes something else that four attempts had gone at from the wrong end.
    /// Pointed at a grid of item icons with no text in it at all, Block invents some:
    /// ten lines of "aay. I N Bh ['S" at 48% confidence, which is where hover's
    /// famous garble came from. Mixed finds no text layout in artwork and returns
    /// one line at 92%. The "is there actually a tooltip here" test that two separate
    /// designs failed to produce falls out of asking the right question.
    /// </summary>
    public enum Layout { Block, Mixed }

    private static PageSegMode Mode(Layout l) =>
        l == Layout.Mixed ? PageSegMode.Auto : PageSegMode.SingleBlock;

    public static async Task<List<string>> ReadLinesAsync(byte[] pngBytes, Layout layout = Layout.Block)
    {
        var lines = new List<string>();
        if (!await EnsureReadyAsync()) return lines;
        try
        {
            pngBytes = ImagePrep.EnhanceForOcr(pngBytes, binarize: true);
            lock (_lock)
            {
                using var img = Pix.LoadFromMemory(pngBytes);
                using var page = _engine!.Process(img, Mode(layout));
                using var iter = page.GetIterator();
                iter.Begin();
                do
                {
                    var t = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
                    if (!string.IsNullOrWhiteSpace(t)) lines.Add(t);
                } while (iter.Next(PageIteratorLevel.TextLine));
                DiagLog.Ocr("lines", "tesseract", img.Width, img.Height, string.Join("\n", lines));
            }
            return lines;
        }
        catch (Exception ex) { CrashLogger.Log("TesseractOcr.ReadLinesAsync", ex); return lines; }
    }
}
