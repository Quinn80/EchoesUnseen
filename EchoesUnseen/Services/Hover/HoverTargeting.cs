namespace EchoesUnseen.Services.Hover;

/// <summary>
/// Which reader answers a hover - said aloud when it changes, and written down for every hover.
///
/// WHY THIS EXISTS: the first smoke test of the OpenCV + RapidOCR targeting (17 September 2026)
/// ran the classic reader from start to finish. The switch was off by default, it was never
/// turned on in that session, and nothing the player could hear said so - the only trace was
/// "hoverTargeting=classic" in one log line. A whole tour of the game was spent testing the
/// wrong code. So now: the mode is announced when it changes, every hover logs which path
/// answered it, a fallback always logs its reason, and the bug report counts all three.
/// </summary>
public static class HoverTargeting
{
    private static int _new, _classic, _fallback;
    private static string? _lastFallbackReason;

    public static string ModeName(bool fusion) => fusion ? "OpenCV + RapidOCR" : "Classic";

    public static string Announcement(bool fusion) => fusion
        ? "Enhanced hover targeting enabled. OpenCV plus RapidOCR."
        : "Classic hover targeting enabled.";

    /// <summary>The player flipped the switch: remember it was their choice, save at once
    /// (not after the usual half-second wait, which a quit hotkey can beat), and log it.</summary>
    public static void Choose(bool fusion)
    {
        var s = App.Settings.Current;
        s.HoverTargetingFusion = fusion;
        s.HoverTargetingChosen = true;
        LogMode(fusion);
        App.Settings.NotifyChanged();
        App.Settings.Save();
        // load the models now, so the first hover afterwards is not the one that waits
        if (fusion) _ = Task.Run(() => Ocr.RapidOcrService.GetMatOcrAsync(CancellationToken.None));
    }

    public static void LogMode(bool fusion) =>
        DiagLog.Log("HOVER TARGETING", fusion ? "OpenCV+RapidOCR ENABLED" : "CLASSIC ENABLED");

    /// <summary>The new targeting answered this hover (spoken, quiet, or nothing readable).</summary>
    public static void AnsweredByNew()
    {
        Interlocked.Increment(ref _new);
        DiagLog.Log("HOVER", "TARGETING=new");
    }

    /// <summary>The switch is off: the classic reader answers.</summary>
    public static void AnsweredByClassic()
    {
        Interlocked.Increment(ref _classic);
        DiagLog.Log("HOVER", "TARGETING=classic");
    }

    /// <summary>The switch is on but the new targeting could not answer: the classic reader
    /// answers instead, and the reason is never silent.</summary>
    public static void FellBack(string reason)
    {
        Interlocked.Increment(ref _fallback);
        _lastFallbackReason = reason;
        DiagLog.Log("HOVER", "TARGETING=new -> classic fallback");
        DiagLog.Log("HOVER", "reason=" + reason);
    }

    public readonly record struct Counts(int New, int Classic, int Fallback, string? LastFallbackReason);

    public static Counts Snapshot() => new(_new, _classic, _fallback, _lastFallbackReason);
}
