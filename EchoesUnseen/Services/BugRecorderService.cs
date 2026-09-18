using System.IO;
using System.IO.Compression;
using System.Windows;

namespace EchoesUnseen.Services;

/// <summary>
/// Records a short bug report: what was on screen, plus what the app thought it was
/// doing at the time.
///
/// WHY THIS EXISTS. Diagnosing the trail overlay took over a week of Quinn describing
/// symptoms and me guessing at causes, because neither of us could see the same thing.
/// Game Bar and OBS "Game Capture" record the GAME'S window, and this app is a separate
/// transparent window sitting on top, so the overlay never appeared in any recording she
/// made. And a screenshot alone can't show flicker, or a trail that vanishes only while
/// turning.
///
/// So: capture the whole DESKTOP (which does include the overlay) as a short flipbook,
/// and zip it together with the tail of the diagnostics log, so a report carries both
/// what she saw and what the code believed. One file to hand over, no video editing, no
/// third-party recorder to learn with a screen reader.
///
/// Deliberately bounded: two minutes maximum, small JPEG frames, spoken start and stop.
/// A recorder you can leave running by accident is a recorder that fills a disk.
/// </summary>
public static class BugRecorderService
{
    /// <summary>Hard stop. Long enough to reproduce a bug, short enough that the zip
    /// stays mailable and the frames stay reviewable one by one.</summary>
    public const int MaxSeconds = 120;

    private const int Fps = 2;                 // a flipbook, not a film
    private const int FrameMaxWidth = 1600;    // plenty to see a trail; a fifth of the bytes
    private const int JpegQuality = 72;

    private static CancellationTokenSource? _cts;
    private static string? _dir;
    private static int _frames;
    private static DateTime _startedAt;
    private static Hover.HoverTargeting.Counts _targetingAtStart;

    public static bool IsRecording => _cts != null;

    /// <summary>Raised when the two-minute limit ends a recording on its own, with the
    /// zip path (or null if it could not be saved).
    ///
    /// Without this the recorder stopped CAPTURING at the limit but never finished:
    /// nothing was said, nothing was saved, and the app still believed it was recording.
    /// Quinn's first report sat like that for another hundred seconds until she pressed
    /// stop by hand - a silent stop is indistinguishable from a broken one.</summary>
    public static event Action<string?>? AutoStopped;

    /// <summary>The last report written this session, so the feedback button can attach
    /// it without asking the user to go and find a file.</summary>
    public static string? LastReportZip { get; private set; }

    /// <summary>Start recording. Returns the folder being written to, or null if it
    /// couldn't start (already running, or the folder couldn't be made).</summary>
    public static string? Start()
    {
        if (IsRecording) return null;
        try
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", $"EchoesUnseen-bug-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(Path.Combine(_dir, "frames"));

            _frames = 0;
            _startedAt = DateTime.Now;
            _targetingAtStart = Hover.HoverTargeting.Snapshot();
            _cts = new CancellationTokenSource();
            DiagLog.Log("BUGREC", $"recording started -> {_dir}");

            _ = Task.Run(() => CaptureLoopAsync(_cts.Token));
            return _dir;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("BugRecorderService.Start", ex);
            _cts = null; _dir = null;
            return null;
        }
    }

    /// <summary>
    /// The last few pictures the hover reader actually looked at, full size.
    ///
    /// Every hover fix so far has been measured on the recorded frames, which are
    /// downscaled to 1600 wide - about 0.6x on Quinn's screen. Tooltip text that is
    /// fourteen pixels tall on her monitor is nine in the evidence, so every bench
    /// number has understated the real thing and the upscale factor could not be tuned
    /// at all. These are the grabs themselves, exactly as the OCR saw them.
    /// </summary>
    private static readonly Queue<byte[]> _hoverGrabs = new();
    private const int KeepHoverGrabs = 6;

    /// <summary>
    /// A tooltip the shadow finder thought it saw, kept so it can be judged later.
    ///
    /// The replay harness has never been able to test tooltip detection, because it
    /// holds single captures and this needs the screen before AND after. Saving the
    /// candidate panel with its OCR and the age of the baseline is what turns "it
    /// regressed" into a case that either passes or fails.
    /// </summary>
    private static readonly List<(byte[] Png, string Meta)> _tipCandidates = new();

    public static void NoteTooltipCandidate(byte[] png, System.Windows.Rect bounds,
                                            string text, double baselineAgeMs)
    {
        if (!IsRecording || png == null) return;
        lock (_tipCandidates)
        {
            if (_tipCandidates.Count >= KeepHoverGrabs) _tipCandidates.RemoveAt(0);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"bounds\": [{(int)bounds.Left}, {(int)bounds.Top}, "
                        + $"{(int)bounds.Width}, {(int)bounds.Height}],");
            sb.AppendLine($"  \"baselineAgeMs\": {baselineAgeMs:F0},");
            sb.AppendLine($"  \"ocr\": \"{text.Replace('"', '\'')}\"");
            sb.AppendLine("}");
            var meta = sb.ToString();
            _tipCandidates.Add((png, meta));
        }
    }

    private static void SaveTooltipCandidates(string dir)
    {
        try
        {
            (byte[] Png, string Meta)[] items;
            lock (_tipCandidates) { items = _tipCandidates.ToArray(); _tipCandidates.Clear(); }
            if (items.Length == 0) return;

            var sub = Path.Combine(dir, "tooltips");
            Directory.CreateDirectory(sub);
            for (int i = 0; i < items.Length; i++)
            {
                File.WriteAllBytes(Path.Combine(sub, $"tip{i + 1:00}.png"), items[i].Png);
                File.WriteAllText(Path.Combine(sub, $"tip{i + 1:00}.json"), items[i].Meta);
            }
            DiagLog.Log("BUGREC", $"kept {items.Length} shadow tooltip candidate(s)");
        }
        catch (Exception ex) { CrashLogger.Log("BugRecorderService.SaveTooltipCandidates", ex); }
    }

    public static void NoteHoverGrab(byte[] png)
    {
        if (!IsRecording || png == null || png.Length == 0) return;
        lock (_hoverGrabs)
        {
            _hoverGrabs.Enqueue(png);
            while (_hoverGrabs.Count > KeepHoverGrabs) _hoverGrabs.Dequeue();
        }
    }

    private static void SaveHoverGrabs(string dir)
    {
        try
        {
            byte[][] grabs;
            lock (_hoverGrabs) { grabs = _hoverGrabs.ToArray(); _hoverGrabs.Clear(); }
            if (grabs.Length == 0) return;
            var sub = Path.Combine(dir, "hover");
            Directory.CreateDirectory(sub);
            for (int i = 0; i < grabs.Length; i++)
                File.WriteAllBytes(Path.Combine(sub, $"hover{i + 1:00}.png"), grabs[i]);
            DiagLog.Log("BUGREC", $"kept {grabs.Length} hover grab(s) at full size");
        }
        catch (Exception ex) { CrashLogger.Log("BugRecorderService.SaveHoverGrabs", ex); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static async Task CaptureLoopAsync(CancellationToken ct)
    {
        int x = 0, y = 0, w = 0, h = 0;
        try
        {
            // PHYSICAL PIXELS, AND THE SCREEN THE GAME IS ON.
            //
            // This used to take PrimaryScreenWidth/Height, which are DIPs, and hand
            // them to a physical-pixel screen grab. On a 2560x1440 display at 125%
            // that captured the top-left 2048x1152 and silently threw away the right
            // and bottom fifths of the screen - so every bug report Quinn ever sent
            // was missing the corner where the inventory and the gold counter live,
            // which is the exact thing she was reporting. The reports could not show
            // the bug they were recording.
            var mon = ScreenMetrics.MonitorOfWindow(GetForegroundWindow());
            x = (int)mon.Left; y = (int)mon.Top;
            w = (int)mon.Width; h = (int)mon.Height;
            await Task.CompletedTask;

            var until = DateTime.UtcNow.AddSeconds(MaxSeconds);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
            {
                var jpg = ScreenCaptureService.CaptureJpeg(x, y, w, h, FrameMaxWidth, JpegQuality);
                if (jpg != null && _dir != null)
                {
                    _frames++;
                    await File.WriteAllBytesAsync(
                        Path.Combine(_dir, "frames", $"frame{_frames:0000}.jpg"), jpg, ct);
                }
                await Task.Delay(1000 / Fps, ct);
            }
        }
        catch (OperationCanceledException) { return; }   // stopped by hand; Stop() finishes up
        catch (Exception ex) { CrashLogger.Log("BugRecorderService.CaptureLoop", ex); }

        // Ran out the clock rather than being stopped: finish the job ourselves.
        if (!ct.IsCancellationRequested)
        {
            var zip = Stop();
            DiagLog.Log("BUGREC", "reached the time limit and saved itself");
            try { AutoStopped?.Invoke(zip); } catch (Exception ex) { CrashLogger.Log("BugRecorder.AutoStopped", ex); }
        }
    }

    /// <summary>Stop recording, gather the evidence, and zip it. Returns the zip path,
    /// or null if nothing was recorded.</summary>
    public static string? Stop(string? whatWentWrong = null)
    {
        if (!IsRecording || _dir == null) return null;

        var cts = _cts; _cts = null;
        try { cts?.Cancel(); } catch { /* already gone */ }
        cts?.Dispose();

        var dir = _dir; _dir = null;
        try
        {
            // Give the in-flight frame a moment to land rather than racing the zip.
            Thread.Sleep(250);

            SaveHoverGrabs(dir);
            SaveTooltipCandidates(dir);
            File.WriteAllText(Path.Combine(dir, "report.txt"), BuildReport(whatWentWrong));
            CopyTail(DiagLog.LogPath, Path.Combine(dir, "diagnostics-tail.log"), 6000);
            CopyTail(Path.Combine(App.Settings.AppDataDirectory, "crash.log"),
                     Path.Combine(dir, "crash-tail.log"), 400);

            var zip = dir + ".zip";
            if (File.Exists(zip)) File.Delete(zip);
            ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, false);
            try { Directory.Delete(dir, true); } catch { /* keep the folder if locked */ }

            DiagLog.Log("BUGREC", $"saved {_frames} frames -> {zip}");
            LastReportZip = zip;
            PruneOldReports();
            return zip;
        }
        catch (Exception ex)
        {
            CrashLogger.Log("BugRecorderService.Stop", ex);
            return null;
        }
    }

    /// <summary>How many reports to keep in Downloads. Each is around 30 MB, and they
    /// pile up fast during a testing session - Quinn had four sitting in her Downloads
    /// folder inside twenty minutes. The newest few are the ones anyone ever looks at.</summary>
    private const int KeepReports = 3;

    private static void PruneOldReports()
    {
        try
        {
            var dl = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var old = new DirectoryInfo(dl)
                .GetFiles("EchoesUnseen-bug-*.zip")
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(KeepReports)
                .ToList();

            foreach (var f in old)
            {
                try { f.Delete(); DiagLog.Log("BUGREC", "pruned old report " + f.Name); }
                catch { /* in use, or gone already - not worth complaining about */ }
            }
        }
        catch (Exception ex) { CrashLogger.Log("BugRecorderService.PruneOldReports", ex); }
    }

    /// <summary>What the app believed at the moment of the report. NO API KEYS: this
    /// file gets shared, and a report that leaks a key is worse than no report.</summary>
    private static string BuildReport(string? whatWentWrong)
    {
        var s = App.Settings.Current;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Echoes Unseen — bug report");
        sb.AppendLine($"when       : {_startedAt:yyyy-MM-dd HH:mm:ss} (+{(DateTime.Now - _startedAt).TotalSeconds:F0}s)");
        sb.AppendLine($"version    : {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} {App.BuildStamp()}");
        sb.AppendLine($"windows    : {Environment.OSVersion.VersionString}");
        sb.AppendLine($"frames     : {_frames} at {Fps} per second");
        if (!string.IsNullOrWhiteSpace(whatWentWrong)) sb.AppendLine($"note       : {whatWentWrong}");
        sb.AppendLine();

        sb.AppendLine("-- guide ------------------------------------------------------");
        var trail = TrailFollower.Current;
        sb.AppendLine($"active guide  : {(trail == null ? "(none)" : $"{trail.Name} / {trail.Category}")}");
        sb.AppendLine($"guide map id  : {trail?.MapId.ToString() ?? "-"}");
        sb.AppendLine($"trail points  : {trail?.Segments.Sum(x => x.Count).ToString() ?? "-"}");
        sb.AppendLine();

        sb.AppendLine("-- settings that affect the trail -----------------------------");
        sb.AppendLine($"draw ahead    : {s.TrailDrawAheadM} m");
        sb.AppendLine($"3-D renderer  : {s.TrailUse3D}");
        sb.AppendLine($"width x       : {s.TrailWidthMultiplier}");
        sb.AppendLine($"elev base/per/max : {s.TrailElevBaseM} / {s.TrailElevPerMetre} / {s.TrailElevMaxM}");
        sb.AppendLine($"marker colour : {s.MarkerColorIndex}   animation: {s.MarkerAnimIndex}");
        sb.AppendLine();

        sb.AppendLine("-- app --------------------------------------------------------");
        sb.AppendLine($"voice         : {s.VoiceEngine}");
        // What the hover reader DOES, not what a settings key says. These disagreed for
        // weeks: the report read "ocr: tesseract" while the log showed Windows OCR
        // running first, which sent me hunting in the wrong engine more than once.
        sb.AppendLine($"hover ocr     : {Ocr.RapidOcrService.ModeDescription}");
        sb.AppendLine();

        // WHICH READER THIS RECORDING TESTED. The first smoke test of the new targeting ran the
        // classic reader throughout and nothing in the report said so. Counts are for the
        // hovers during this recording.
        var now = Hover.HoverTargeting.Snapshot();
        sb.AppendLine("-- hover targeting (during this recording) --------------------");
        sb.AppendLine($"hover targeting : {Hover.HoverTargeting.ModeName(s.HoverTargetingFusion)}" +
                      (s.HoverTargetingFusion && Hover.FusionHoverReader.Unavailable
                          ? $"  (NOT RUNNING: {Hover.FusionHoverReader.UnavailableReason})" : "") +
                      (s.HoverTargetingChosen ? "" : "  (build default)"));
        sb.AppendLine($"new targeting hover count : {now.New - _targetingAtStart.New}");
        sb.AppendLine($"classic hover count       : {now.Classic - _targetingAtStart.Classic}");
        sb.AppendLine($"fallback count            : {now.Fallback - _targetingAtStart.Fallback}");
        if (now.Fallback > _targetingAtStart.Fallback && now.LastFallbackReason != null)
            sb.AppendLine($"last fallback reason      : {now.LastFallbackReason}");
        sb.AppendLine();
        sb.AppendLine($"chat ocr      : {s.OcrEngine}");
        sb.AppendLine($"theme         : {s.ThemeId}");
        sb.AppendLine($"gw2 api key   : {(string.IsNullOrEmpty(s.Gw2ApiKey) ? "not set" : "set (not included)")}");
        sb.AppendLine();
        sb.AppendLine("No API keys, account names or email addresses are included in this file.");
        return sb.ToString();
    }

    /// <summary>Copy the last N lines of a log. The whole diagnostics file runs to
    /// megabytes and the interesting part is always the end.</summary>
    private static void CopyTail(string from, string to, int lines)
    {
        try
        {
            if (!File.Exists(from)) return;
            var all = File.ReadLines(from).ToList();
            var tail = all.Count > lines ? all.Skip(all.Count - lines) : all;
            File.WriteAllLines(to, tail);
        }
        catch (Exception ex) { CrashLogger.Log("BugRecorderService.CopyTail " + from, ex); }
    }
}
