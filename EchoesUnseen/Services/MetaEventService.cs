using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Services;

/// <summary>
/// World-boss / meta-event audio alarm. GW2's big events run on a FIXED daily UTC
/// clock, so no screen-reading is needed — we just watch the computer's clock and,
/// a few minutes before an event starts, play a cue and announce it. This turns the
/// giant visual event-timer wall into something you can hear.
///
/// The schedule is data, not code: a built-in starter set that a user (or a future
/// update) can extend/correct via a meta-schedule.json in the app-data folder. Times
/// are minutes past 00:00 UTC. The starter set is the stable "hardcore" world bosses,
/// whose times are widely published and don't drift.
/// </summary>
public sealed class MetaEventService : IDisposable
{
    public sealed record MetaEvent(string Name, string Map, int[] MinutesUtc, int DurationMin);

    public static MetaEventService? Shared { get; private set; }

    private readonly TtsService _tts;
    private readonly DispatcherTimer _timer;
    private List<MetaEvent> _schedule;
    private readonly Dictionary<string, DateTime> _lastAnnounced = new();

    public MetaEventService(TtsService tts)
    {
        _tts = tts;
        _schedule = LoadSchedule();
        Shared = this;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ── Schedule (minutes past 00:00 UTC) ────────────────────────────────────
    private static List<MetaEvent> DefaultSchedule() => new()
    {
        new("Tequatl the Sunless", "Sparkfly Fen",
            new[] { H(0,0), H(3,0), H(7,0), H(11,30), H(16,0), H(19,0) }, 30),
        new("Triple Trouble Wurm", "Bloodtide Coast",
            new[] { H(1,0), H(4,0), H(8,0), H(12,30), H(16,30), H(20,0) }, 30),
        new("Karka Queen", "Southsun Cove",
            new[] { H(2,0), H(6,0), H(10,30), H(15,0), H(18,0), H(23,0) }, 30),
    };

    private static int H(int hour, int min) => hour * 60 + min;

    private static string SchedulePath => Path.Combine(App.Settings.AppDataDirectory, "meta-schedule.json");

    private static List<MetaEvent> LoadSchedule()
    {
        try
        {
            if (File.Exists(SchedulePath))
            {
                var loaded = JsonSerializer.Deserialize<List<MetaEvent>>(File.ReadAllText(SchedulePath));
                if (loaded is { Count: > 0 }) return loaded;
            }
        }
        catch (Exception ex) { CrashLogger.Log("MetaEventService.Load", ex); }
        return DefaultSchedule();
    }

    /// <summary>Write the current schedule out so it can be hand-edited/extended.</summary>
    public void ExportSchedule()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchedulePath)!);
            File.WriteAllText(SchedulePath, JsonSerializer.Serialize(_schedule,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { CrashLogger.Log("MetaEventService.Export", ex); }
    }

    public void Reload() => _schedule = LoadSchedule();

    // ── Upcoming query (for the panel and the hotkey) ────────────────────────
    public List<(string Name, string Map, int SecondsUntil)> Upcoming(int max)
    {
        var now = DateTime.UtcNow;
        var list = new List<(string, string, int)>();
        foreach (var e in _schedule)
        {
            double best = double.MaxValue;
            foreach (var m in e.MinutesUtc)
            {
                var occ = NextOccurrence(now, m);
                best = Math.Min(best, (occ - now).TotalSeconds);
            }
            list.Add((e.Name, e.Map, (int)best));
        }
        return list.OrderBy(x => x.Item3).Take(max).ToList();
    }

    private static DateTime NextOccurrence(DateTime nowUtc, int minutesUtc)
    {
        var occ = nowUtc.Date.AddMinutes(minutesUtc);
        if (occ <= nowUtc) occ = occ.AddDays(1);
        return occ;
    }

    // ── The alarm tick ───────────────────────────────────────────────────────
    private void Tick()
    {
        var s = App.Settings.Current;
        if (!s.MetaAlertsEnabled || s.QuietMode) return;
        double leadSec = Math.Clamp(s.MetaLeadMinutes, 1, 30) * 60;
        var now = DateTime.UtcNow;

        foreach (var e in _schedule)
        {
            foreach (var m in e.MinutesUtc)
            {
                var occ = NextOccurrence(now, m);
                double sec = (occ - now).TotalSeconds;
                if (sec > 0 && sec <= leadSec)
                {
                    if (_lastAnnounced.TryGetValue(e.Name, out var last) && last == occ) continue;
                    _lastAnnounced[e.Name] = occ;
                    int mins = (int)Math.Round(sec / 60.0);
                    string when = mins <= 1 ? "in about a minute" : $"in {mins} minutes";
                    EarconService.Shared?.WardCue(up: false);   // the bright "opening" cue
                    _ = _tts.SpeakAsync($"{e.Name} starts {when}, in {e.Map}.", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
                    break;   // one time per event per tick
                }
            }
        }
    }

    /// <summary>Speak the next few events on demand (hotkey / panel button).</summary>
    public void AnnounceNext(int count = 3)
    {
        var up = Upcoming(count);
        if (up.Count == 0) { _ = _tts.SpeakAsync("No events scheduled.", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background); return; }
        var parts = up.Select(u => $"{u.Name} in {FmtSpan(u.SecondsUntil)}, in {u.Map}");
        _tts.StopSpeaking();
        _ = _tts.SpeakAsync("Next events. " + string.Join(". ", parts) + ".", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
    }

    public static string FmtSpan(int seconds)
    {
        int m = seconds / 60;
        if (m < 1) return "under a minute";
        if (m < 60) return $"{m} minute{(m == 1 ? "" : "s")}";
        int h = m / 60, rem = m % 60;
        return rem == 0 ? $"{h} hour{(h == 1 ? "" : "s")}" : $"{h} hour{(h == 1 ? "" : "s")} {rem} minutes";
    }

    public void Dispose() => _timer.Stop();
}
