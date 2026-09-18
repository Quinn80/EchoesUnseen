using System.IO;
using System.Runtime.InteropServices;

namespace EchoesUnseen.Services;

/// <summary>
/// Speaks through the running NVDA screen reader via the official NVDA Controller
/// Client (nvdaControllerClient64.dll, LGPL — bundled unmodified).
///
/// WHY: when a blind user runs NVDA, the app having its OWN separate voice fights
/// NVDA — double-talk, NVDA's audio-ducking quietening our output, and two audio
/// pipelines competing (which felt like "TTS dropping"). Routing our speech
/// through NVDA means ONE voice — the user's own NVDA, at their chosen rate and
/// settings — with no conflict at all.
///
/// The native DLL is embedded and extracted next to the app data on first use,
/// then loaded so the P/Invokes below resolve. Every call fails soft: if NVDA
/// isn't running the caller falls back to the app's own TTS.
/// </summary>
public static class NvdaOutput
{
    [DllImport("nvdaControllerClient64.dll")]
    private static extern int nvdaController_testIfRunning();

    [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakText(string text);

    [DllImport("nvdaControllerClient64.dll")]
    private static extern int nvdaController_cancelSpeech();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static bool _loaded;
    private static bool _available;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var dir = Path.Combine(App.Settings.AppDataDirectory, "nvda");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "nvdaControllerClient64.dll");
            if (!File.Exists(dest))
            {
                var asm = typeof(NvdaOutput).Assembly;
                var res = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("nvdaControllerClient64.dll", StringComparison.OrdinalIgnoreCase));
                if (res != null)
                {
                    using var s = asm.GetManifestResourceStream(res);
                    if (s != null) { using var f = File.Create(dest); s.CopyTo(f); }
                }
            }
            _available = LoadLibrary(dest) != IntPtr.Zero;
        }
        catch (Exception ex) { CrashLogger.Log("NvdaOutput load", ex); _available = false; }
    }

    /// <summary>Is NVDA running and reachable right now?</summary>
    public static bool IsRunning()
    {
        EnsureLoaded();
        if (!_available) return false;
        try { return nvdaController_testIfRunning() == 0; }
        catch { return false; }
    }

    /// <summary>Speak via NVDA. <paramref name="interrupt"/> stops current speech first.</summary>
    public static bool Speak(string text, bool interrupt = true)
    {
        EnsureLoaded();
        if (!_available) return false;
        try
        {
            if (interrupt) nvdaController_cancelSpeech();
            return nvdaController_speakText(text) == 0;
        }
        catch { return false; }
    }

    /// <summary>Stop NVDA speech.</summary>
    public static void Cancel()
    {
        if (!_loaded || !_available) return;
        try { nvdaController_cancelSpeech(); } catch { }
    }
}
