using System.Text.Json.Serialization;
using System.Windows.Threading;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Services;

/// <summary>
/// World vs World awareness. Uses the official WvW API to announce objective
/// flips, your team, and — the headline — a lord-invulnerability countdown.
///
/// LORD INVULNERABILITY: when an objective is captured, its lord/guards get
/// "Righteous Indignation" (invulnerable + damage boost) for FIVE MINUTES. So an
/// enemy tower/camp you're attacking can't be taken until 5 minutes after it last
/// flipped. We read each objective's last_flipped time from the API and, when
/// you're near an enemy objective still under RI, tell you when the lord becomes
/// killable.
///
/// LIMITS (honest): the API has no data on other players' positions, so this
/// never claims "enemies nearby" — that's the separate combat alert's job.
/// </summary>
public sealed class WvwService : IDisposable
{
    private const double RiSeconds = 300;         // Righteous Indignation = 5 min
    private const double NearUnits = 1600;        // "at" an objective, continent units

    private readonly MumbleLinkReader _mumble;
    private readonly TtsService _tts;
    private readonly Gw2ApiService _api;
    private readonly DispatcherTimer _timer;

    private int _worldId = -1;
    private string _teamColor = "";
    private Dictionary<string, ObjMeta> _meta = new();     // objId -> name/coord
    private readonly Dictionary<string, string> _owners = new();       // objId -> owner
    private readonly Dictionary<string, DateTime> _flipUtc = new();    // objId -> last flip
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _polling;
    private int _lastMapId = -1;
    private int _currentMapId = -1;   // the WvW map the player is on right now
    private float _px, _py;           // last known player position (for direction)

    private string _riCuedFor = "";          // objId we've played the shield cue / opener for
    private string _vulnAnnouncedFor = "";   // objId we've announced "now vulnerable" for
    private readonly HashSet<string> _milestonesSpoken = new();  // "objId|seconds"

    public WvwService(MumbleLinkReader mumble, TtsService tts, Gw2ApiService api)
    {
        _mumble = mumble; _tts = tts; _api = api;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private async void Tick()
    {
        if (!App.Settings.Current.WvwEnabled) return;
        var data = _mumble.Read();
        if (data == null || !data.IsCompetitive) return;   // only in WvW/PvP

        _currentMapId = data.MapId;
        _px = data.PlayerX; _py = data.PlayerY;

        // Announce entering a new WvW map.
        if (data.MapId != _lastMapId && !string.IsNullOrEmpty(_teamColor))
        {
            _lastMapId = data.MapId;
            _ = _tts.SpeakAsync($"World versus World. Your team is {_teamColor}.", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
        }

        // Refresh the match every ~15 s (the API caches it anyway).
        if (!_polling && (DateTime.UtcNow - _lastPoll).TotalSeconds >= 15)
            await PollAsync();

        // Lord-invulnerability countdown for the enemy objective you're standing at.
        CheckNearbyLord(data.PlayerX, data.PlayerY);

        // Early-warning: announce macro-objectives you're running up on, and who holds
        // them, so you don't blunder into an enemy fortress while disoriented.
        CheckApproach(data.PlayerX, data.PlayerY);
    }

    private async Task PollAsync()
    {
        _polling = true;
        try
        {
            if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey)) return;

            if (_worldId < 0)
            {
                var acct = await _api.GetAuthAsync<AccountWorld>("account");
                if (acct == null || acct.World <= 0) return;
                _worldId = acct.World;
                DiagLog.Log("WVW", $"account world id = {_worldId}");
            }
            if (_meta.Count == 0)
            {
                var metas = await _api.GetPublicAsync<List<ObjMeta>>("wvw/objectives?ids=all");
                if (metas != null) _meta = metas.Where(m => m.Id != null).ToDictionary(m => m.Id!, m => m);
            }

            var match = await _api.GetPublicAsync<Match>($"wvw/matches?world={_worldId}");
            if (match?.Maps == null) return;

            // Manual override wins — World Restructuring makes the API's world→team
            // mapping unreliable, so let the user pin their colour.
            var manual = App.Settings.Current.WvwTeam;
            if (manual is "red" or "blue" or "green")
                _teamColor = char.ToUpper(manual[0]) + manual[1..];
            else if (string.IsNullOrEmpty(_teamColor))
            {
                _teamColor = ResolveTeam(match);
                DiagLog.Log("WVW", $"resolve team: world={_worldId} -> {_teamColor} | " +
                    $"primary R={match.Worlds?.Red} B={match.Worlds?.Blue} G={match.Worlds?.Green} | " +
                    $"allR=[{string.Join(",", match.AllWorlds?.Red ?? new())}] " +
                    $"allB=[{string.Join(",", match.AllWorlds?.Blue ?? new())}] " +
                    $"allG=[{string.Join(",", match.AllWorlds?.Green ?? new())}]");
            }
            _lastPoll = DateTime.UtcNow;

            foreach (var map in match.Maps)
            {
                if (map.Objectives == null) continue;
                foreach (var o in map.Objectives)
                {
                    if (o.Id == null || o.Owner == null) continue;
                    // Record the flip time.
                    if (DateTime.TryParse(o.LastFlipped, out var lf)) _flipUtc[o.Id] = lf.ToUniversalTime();

                    if (_owners.TryGetValue(o.Id, out var prev))
                    {
                        if (prev != o.Owner && o.Owner != "Neutral")
                        {
                            _meta.TryGetValue(o.Id, out var m);
                            // reset lord announcements for the freshly flipped objective
                            if (_riCuedFor == o.Id) _riCuedFor = "";
                            if (_vulnAnnouncedFor == o.Id) _vulnAnnouncedFor = "";
                            _milestonesSpoken.RemoveWhere(k => k.StartsWith(o.Id + "|"));

                            // ONLY announce flips on the map you're actually on — the old
                            // behaviour read out every flip across all four maps, which was
                            // a flood of far-away, irrelevant objectives.
                            if (m != null && _currentMapId > 0 && m.MapId == _currentMapId)
                            {
                                string name = m.Name ?? o.Type ?? "objective";
                                bool ours = o.Owner == _teamColor;
                                string who = ours ? "We" : $"{o.Owner}";
                                string verb = ours ? "captured" : "took";
                                string dir = DirectionTo(m);
                                string where = dir.Length > 0 ? $", to your {dir}" : "";
                                DiagLog.Log("WVW", $"flip {name} owner={o.Owner} team={_teamColor} map={m.MapId} dir={dir}");
                                _ = _tts.SpeakAsync($"{who} {verb} {name}{where}.", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
                            }
                        }
                    }
                    _owners[o.Id] = o.Owner;
                }
            }
        }
        catch (Exception ex) { CrashLogger.Log("WvwService.Poll", ex); }
        finally { _polling = false; }
    }

    /// <summary>Compass direction from the player to an objective ("north-east",
    /// etc.), or "" if it's basically where you're standing. Player position and
    /// objective coords are both continent coordinates, so north = -Y, east = +X.</summary>
    private string DirectionTo(ObjMeta m)
    {
        var c = m.LabelCoord ?? m.Coord;
        if (c == null || c.Count < 2) return "";
        double dx = c[0] - _px;     // east positive
        double dy = c[1] - _py;     // south positive (Y grows downward on the map)
        if (Math.Sqrt(dx * dx + dy * dy) < 600) return "";   // you're essentially there
        double ang = Math.Atan2(dx, -dy) * 180 / Math.PI;    // 0=N, 90=E, 180=S, -90=W
        if (ang < 0) ang += 360;
        string[] pts = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
        return pts[(int)Math.Round(ang / 45.0) % 8];
    }

    private void CheckNearbyLord(float px, float py)
    {
        if (_meta.Count == 0) return;
        // Nearest objective with known coords.
        ObjMeta? nearest = null; double best = double.MaxValue;
        foreach (var m in _meta.Values)
        {
            var c = m.LabelCoord ?? m.Coord;
            if (c == null || c.Count < 2) continue;
            double dx = c[0] - px, dy = c[1] - py, d = dx * dx + dy * dy;
            if (d < best) { best = d; nearest = m; }
        }
        if (nearest?.Id == null || Math.Sqrt(best) > NearUnits) return;

        // Righteous Indignation applies to whoever just captured it — an ENEMY
        // objective you can't damage yet, or one WE hold that's protected. Both are
        // worth a countdown; only Neutral (never freshly captured) is skipped.
        if (!_owners.TryGetValue(nearest.Id, out var owner) || owner == "Neutral") return;
        if (!_flipUtc.TryGetValue(nearest.Id, out var flipped)) return;

        bool ours = owner == _teamColor;
        double remain = RiSeconds - (DateTime.UtcNow - flipped).TotalSeconds;
        string name = nearest.Name ?? "the objective";
        var s = App.Settings.Current;

        if (remain > 3)
        {
            // Arrival: distinct shield cue + the time on the clock, once.
            if (_riCuedFor != nearest.Id)
            {
                _riCuedFor = nearest.Id;
                if (s.WvwLordCue) EarconService.Shared?.WardCue(up: true);
                string time = FmtLong(remain);
                _ = _tts.SpeakAsync(ours
                        ? $"You hold {name}. Protected for {time}."
                        : $"{name} lord is invulnerable. Vulnerable in {time}.",
                    engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
            }

            // Live countdown: fire each milestone once as the timer crosses it.
            if (s.WvwLordCountdown)
            {
                foreach (int ms in Milestones)
                {
                    if (remain <= ms && remain > ms - 2 && _milestonesSpoken.Add($"{nearest.Id}|{ms}"))
                        _ = _tts.SpeakAsync(ours
                                ? $"{name}, {FmtShort(ms)} of protection left."
                                : $"{name}, {FmtShort(ms)} until the lord is vulnerable.",
                            engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
                }
            }
        }
        else if (remain <= 0 && _vulnAnnouncedFor != nearest.Id)
        {
            _vulnAnnouncedFor = nearest.Id;
            if (s.WvwLordCue) EarconService.Shared?.WardCue(up: false);
            _ = _tts.SpeakAsync(ours
                    ? $"Protection on {name} has ended."
                    : $"{name} lord is now vulnerable.",
                engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
        }
    }

    private const double ApproachUnits = 3200;   // "running up on it", continent units
    private readonly HashSet<string> _approachInRange = new();

    /// <summary>Announce a macro-objective as you come within range, once, and who owns
    /// it. Re-arms after you've moved well clear, so passing it again re-announces.</summary>
    private void CheckApproach(float px, float py)
    {
        if (!App.Settings.Current.WvwApproachAlerts || _meta.Count == 0) return;

        // Drop any previously-announced objective we've now moved well clear of.
        _approachInRange.RemoveWhere(id =>
        {
            if (!_meta.TryGetValue(id, out var mm)) return true;
            var cc = mm.LabelCoord ?? mm.Coord;
            if (cc == null || cc.Count < 2) return true;
            double ddx = cc[0] - px, ddy = cc[1] - py;
            return Math.Sqrt(ddx * ddx + ddy * ddy) > ApproachUnits * 1.5;
        });

        // Nearest objective on THIS map.
        ObjMeta? nearest = null; double best = double.MaxValue;
        foreach (var m in _meta.Values)
        {
            if (_currentMapId > 0 && m.MapId != _currentMapId) continue;
            var c = m.LabelCoord ?? m.Coord;
            if (c == null || c.Count < 2) continue;
            double dx = c[0] - px, dy = c[1] - py, d = dx * dx + dy * dy;
            if (d < best) { best = d; nearest = m; }
        }
        if (nearest?.Id == null || Math.Sqrt(best) > ApproachUnits) return;
        if (!_approachInRange.Add(nearest.Id)) return;   // already announced this pass

        string name = nearest.Name ?? nearest.Type ?? "objective";
        string who = !_owners.TryGetValue(nearest.Id, out var owner) || owner == "Neutral"
            ? "unclaimed"
            : owner == _teamColor ? "held by your team" : $"held by enemy {owner} team";
        _ = _tts.SpeakAsync($"Approaching {name}, {who}.", engineOverride: "winnatural", lane: TtsService.SpeechLane.Background);
        DiagLog.Log("WVW", $"approach {name} owner={owner} dist={Math.Sqrt(best):F0}");
    }

    private static readonly int[] Milestones = { 180, 120, 60, 30, 10 };

    /// <summary>"4 minutes 10 seconds" / "40 seconds" — the full time on arrival.</summary>
    private static string FmtLong(double seconds)
    {
        int mins = (int)(seconds / 60), secs = (int)(seconds % 60);
        return mins > 0 ? $"{mins} minute{(mins == 1 ? "" : "s")} {secs} seconds" : $"{secs} seconds";
    }

    /// <summary>"3 minutes" / "1 minute" / "30 seconds" — a milestone label.</summary>
    private static string FmtShort(int seconds)
    {
        if (seconds >= 60 && seconds % 60 == 0)
        {
            int m = seconds / 60;
            return $"{m} minute{(m == 1 ? "" : "s")}";
        }
        return $"{seconds} seconds";
    }

    private string ResolveTeam(Match m)
    {
        if (m.AllWorlds?.Red?.Contains(_worldId) == true) return "Red";
        if (m.AllWorlds?.Blue?.Contains(_worldId) == true) return "Blue";
        if (m.AllWorlds?.Green?.Contains(_worldId) == true) return "Green";
        if (m.Worlds?.Red == _worldId) return "Red";
        if (m.Worlds?.Blue == _worldId) return "Blue";
        if (m.Worlds?.Green == _worldId) return "Green";
        return "";
    }

    public void Dispose() => _timer.Stop();

    // ── API models ────────────────────────────────────────────────────────────
    private class AccountWorld { public int World { get; set; } }
    private class Match
    {
        [JsonPropertyName("worlds")] public Colors? Worlds { get; set; }
        [JsonPropertyName("all_worlds")] public ColorLists? AllWorlds { get; set; }
        [JsonPropertyName("maps")] public List<Mp>? Maps { get; set; }
    }
    private class Colors { public int Red { get; set; } public int Blue { get; set; } public int Green { get; set; } }
    private class ColorLists { public List<int>? Red { get; set; } public List<int>? Blue { get; set; } public List<int>? Green { get; set; } }
    private class Mp { [JsonPropertyName("objectives")] public List<Obj>? Objectives { get; set; } }
    private class Obj
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Owner { get; set; }
        [JsonPropertyName("last_flipped")] public string? LastFlipped { get; set; }
    }
    private class ObjMeta
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        [JsonPropertyName("map_id")] public int MapId { get; set; }
        public List<double>? Coord { get; set; }
        [JsonPropertyName("label_coord")] public List<double>? LabelCoord { get; set; }
    }
}
