using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Trail Navigator — the core accessibility navigation panel.
///
/// Combines three data sources into a single experience:
///   - MumbleLink: real-time player position (polled every 2 seconds)
///   - GW2 REST API: list of objectives on the current map
///   - NAudio sonar: proximity audio pings that get faster and higher-pitched
///     as the player approaches the nearest target
///
/// All trail navigation is native to Echoes Unseen — no external tools, no
/// separate installer, no separate file paths or update cycle. Everything is
/// integrated.
///
/// DATA FLOW (every 2s):
///   1. Read MumbleLink → (mapId, playerX, playerY)
///   2. If mapId changed, fetch the map's objectives from the API (cached)
///   3. Compute distance from player to each objective
///   4. Filter by user-enabled types, sort by distance
///   5. Update UI list and sonar with the nearest target
///
/// COORDINATE SYSTEM:
///   Both MumbleLink positions and API continent coordinates use the SAME
///   coordinate system. Euclidean distance in game units is directly
///   meaningful (rough conversion: 1 game unit ≈ 1 inch).
/// </summary>
public partial class TrailNavigatorPanel : UserControl, IPanel, IBackgroundPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;
    private readonly SonarService _sonar = new();
    private readonly WikiService _wiki = new();

    private DispatcherTimer? _poll;
    private int _lastMapId = -1;
    private MapObjectives? _currentMap;
    private List<Obj> _nearest = new();

    // Marker-pack (TacO) guidance.
    private readonly TacoService _taco = new();
    private string? _markerCategory;         // selected category to guide to
    private DateTime _lastMarkerDir = DateTime.MinValue;
    private string _lastMarkerDirSpoken = "";

    // Guided navigation: the locked target we guide to until it's reached (then we
    // auto-advance to the next), the one currently active for the UI highlight,
    // and the last objective announced as reached (so we confirm it only once).
    private Obj? _locked;
    private Obj? _active;
    private string _arrivedName = "";

    // ── Spoken turn-by-turn guide (world-space) ───────────────────────────────
    // The current target expressed in GW2 WORLD metres — the same frame MumbleLink
    // reports the live player position and camera facing in. Both nav sources feed
    // this: imported markers are already world-space; API objectives are converted
    // from continent coords (see ComputeWorldTarget). The guide timer reads it a
    // few times a second and speaks a turn cue only when the cue CHANGES, so it
    // stays quiet while you're on course and pipes up the moment you drift.
    public static TrailNavigatorPanel? Current { get; private set; }
    private DispatcherTimer? _guideTimer;
    private (bool valid, double wx, double wz, string name) _worldTarget;
    private string _lastTurnSpoken = "";
    private DateTime _lastTurnAt = DateTime.MinValue;

    // Auto-calibration of the continent→world north/south sign, learned from how the
    // player's world Z moves against their continent Y as they walk. 0 = not yet
    // learned (fall back to the map-rect default). This is what lets "face it and
    // go forward" be correct without the user flipping anything.
    private int _signZ;
    private double _prevPcy, _prevWz;
    private bool _havePrev;

    // ── Trail following (the Guides tab) ──────────────────────────────────────
    private readonly TrailFollower _follower = new();
    private string _lastTrailCue = "";        // last spoken turn/off-path line
    private DateTime _lastTrailCueAt = DateTime.MinValue;
    private List<TacoService.Trail> _mapTrails = new();
    private DateTime _lastGuideLog = DateTime.MinValue;
    private bool _wasOffTrail;

    // Hybrid facing: body-facing while moving (calm and stable), camera-facing while
    // standing still (so you can look around to find the way). Speed is measured
    // between guide ticks, with a gap between the two thresholds so it doesn't flap.
    private double _lastPx, _lastPz;
    private DateTime _lastPosAt = DateTime.MinValue;
    private bool _moving;

    private double _smFx, _smFz;   // smoothed facing

    /// <summary>
    /// Which way is "forward" for the guidance.
    ///
    /// This used to switch between body-facing and camera-facing depending on whether
    /// you were moving. The log killed that idea: your camera and body sat 45 degrees
    /// apart, so the moment the code swapped vectors the guidance lurched — one second
    /// "turn around", the next "turn hard right", with you standing still. Changing
    /// which thing you measure from is not a measurement.
    ///
    /// So: always the CAMERA. In GW2 pressing forward walks you where the camera looks,
    /// so it answers "which way will I actually go". The vector is then smoothed, which
    /// removes the jitter that made the spoken turn flicker across its boundaries.
    /// </summary>
    private (double fx, double fz) FacingFor(EchoesUnseen.Models.MumbleLinkData d)
    {
        double fx = d.CameraFrontX, fz = d.CameraFrontZ;
        double len = Math.Sqrt(fx * fx + fz * fz);
        if (len < 1e-4) return (_smFx, _smFz);
        fx /= len; fz /= len;

        if (_smFx == 0 && _smFz == 0) { _smFx = fx; _smFz = fz; }
        else
        {
            const double k = 0.35;                     // gentle easing
            _smFx += (fx - _smFx) * k;
            _smFz += (fz - _smFz) * k;
            double sl = Math.Sqrt(_smFx * _smFx + _smFz * _smFz);
            if (sl > 1e-6) { _smFx /= sl; _smFz /= sl; }
        }
        return (_smFx, _smFz);
    }

    /// <summary>Internal row type combining type + coords + name. PoiId is the GW2
    /// point-of-interest id (0 if none), used to build a waypoint's chat link.</summary>
    private record Obj(string Name, string Type, float X, float Y, float DistSq, int PoiId = 0)
    {
        public float Distance => (float)Math.Sqrt(DistSq);
    }

    public TrailNavigatorPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _taco.Load();
            RefreshMarkerCategories(_lastMapId);

            // Visible-marker style pickers.
            var ms = App.Settings.Current;
            MarkerSizeSlider.Value = ms.MarkerSize;
            MarkerSizeLabel.Text = ((int)ms.MarkerSize).ToString();

            SonarVolumeSlider.Value = App.Settings.Current.SonarVolume * 100.0;
            SonarHeartbeat.IsChecked = App.Settings.Current.SonarHeartbeat;
            _sonar.SetHeartbeat(App.Settings.Current.SonarHeartbeat);

            // Populate the sonar sound picker and select the saved one.
            foreach (var (id, name, _) in SonarService.Profiles)
                SonarSoundCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            foreach (ComboBoxItem it in SonarSoundCombo.Items)
                if ((string)it.Tag == App.Settings.Current.SonarSound) { SonarSoundCombo.SelectedItem = it; break; }
            if (SonarSoundCombo.SelectedIndex < 0) SonarSoundCombo.SelectedIndex = 0;
            _sonar.SetSound(App.Settings.Current.SonarSound);

            RefreshGuideList();

            Current = this;
            StartPolling();
            StartGuide();
        };
        // Deliberately no Unloaded teardown: audio navigation is meant to guide
        // you WHILE you play, so closing the window must not silence the sonar.
        // It stops when the user turns it off, or on app exit.
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    /// <summary>Stop polling and silence the sonar. Called on app exit.</summary>
    public void StopBackgroundWork()
    {
        _poll?.Stop(); _poll = null;
        _guideTimer?.Stop(); _guideTimer = null;
        _sonar.Stop();
    }

    // ── Polling loop ─────────────────────────────────────────────────────────
    private void StartPolling()
    {
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = Dispatcher.InvokeAsync(async () => await PollAsync());
    }

    private async Task PollAsync()
    {
        if (_mumble == null || _gw2Api == null) return;
        var data = _mumble.Read();
        if (data == null)
        {
            MapName.Text = "Waiting for Guild Wars 2...";
            ObjectiveCount.Text = "Launch GW2 and enter a map.";
            return;
        }

        // Marker-pack guidance takes over when a category is selected — guide to the
        // nearest imported marker on this map, by sound (the API doesn't have these).
        if (!string.IsNullOrEmpty(_markerCategory) && SonarEnabled.IsChecked == true)
        {
            if (data.MapId != _lastMapId) { _lastMapId = data.MapId; RefreshMarkerCategories(data.MapId); }
            DoMarkerNav(data);
            return;
        }

        // Fetch map objectives if map changed
        if (data.MapId != _lastMapId)
        {
            _lastMapId = data.MapId;
            RefreshMarkerCategories(data.MapId);
            RefreshGuideList();          // guides are per-map
            _currentMap = await _gw2Api.GetMapObjectivesAsync(data.MapId);
            if (_currentMap != null)
            {
                MapName.Text = _currentMap.MapName;
                var counts = CountObjectives(_currentMap);
                ObjectiveCount.Text =
                    $"{counts.wp} waypoints · {counts.poi} POIs · {counts.vista} vistas · {counts.hero} hero challenges · {counts.heart} hearts";
            }
            else
            {
                MapName.Text = $"Map {data.MapId}";
                ObjectiveCount.Text = "Could not load objectives for this map.";
            }
        }

        if (_currentMap == null) return;

        // Compute distances and rank — nearest 5, so the list isn't overwhelming.
        var objs = CollectObjects(_currentMap, data.PlayerX, data.PlayerY);
        _nearest = objs.OrderBy(o => o.DistSq).Take(5).ToList();

        // LOCK, THEN ADVANCE. Stay locked on one objective until you reach it —
        // don't switch just because another gets closer. Refresh the locked
        // target's live distance; if it vanished (map change), or nothing is
        // locked yet, lock onto the nearest one you HAVEN'T found yet.
        Obj? active = null;
        if (_locked != null)
        {
            active = objs.FirstOrDefault(o => o.Name == _locked.Name
                && Math.Abs(o.X - _locked.X) < 1 && Math.Abs(o.Y - _locked.Y) < 1);
        }
        if (active == null)
            active = NearestUnvisited(objs, data.MapId) ?? _nearest.FirstOrDefault();
        _locked = active;

        _active = active;
        UpdateProgress(data.MapId);
        RenderList();

        LearnAxisSign(data);

        if (active != null)
        {
            // The COMPASS always points to the nearest objective (it rotates with
            // your facing) — that's the visual guide. The sonar SOUND is separate,
            // only when you've switched it on.
            if (SonarEnabled.IsChecked == true)
                _sonar.UpdateDistance(active.Distance, (float)(SonarVolumeSlider.Value / 100.0));

            // Feed the spoken guide AND the compass from the same world-space target, so
            // they can never point different ways.
            if (ComputeWorldTarget(data, active.X, active.Y, out double twx, out double twz))
            {
                _worldTarget = (true, twx, twz, active.Name);
                double wdx = twx - data.AvatarX, wdz = twz - data.AvatarZ;
                NavState.SetTarget((float)twx, (float)twz,
                    (float)Math.Sqrt(wdx * wdx + wdz * wdz), active.Name);
            }
            else
            {
                _worldTarget.valid = false;
                NavState.Clear();
            }

            // Arrival → distinct confirmation, then AUTO-ADVANCE to the next
            // nearest and announce it, so the compass never disappears.
            if (active.Distance <= ArrivalUnits && _arrivedName != active.Name)
            {
                _arrivedName = active.Name;
                MarkVisited(data.MapId, active);   // remember you found it
                bool heart = active.Type == "Heart";
                _sonar.PlayConfirmation(heart);
                // Advance to the nearest one you HAVEN'T found yet.
                var next = objs.Where(o => o.Name != active.Name && !IsVisited(data.MapId, o))
                               .OrderBy(o => o.DistSq).FirstOrDefault();
                _locked = next;
                string reached = heart
                    ? $"You've reached the heart, {active.Name}. Complete it."
                    : $"You've reached {active.Name}.";
                string nextMsg = next != null
                    ? $" Now guiding to {next.Name}, {next.Type}."
                    : " That's the last one nearby.";
                _ = _tts?.SpeakAsync(reached + nextMsg);
            }
        }
        else
        {
            NavState.Clear();
            _worldTarget.valid = false;
        }
    }

    // ── Spoken turn-by-turn guide (world space) ───────────────────────────────
    private void StartGuide()
    {
        _guideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _guideTimer.Tick += (_, _) => GuideTick();
        _guideTimer.Start();
    }

    /// <summary>Learn whether continent-Y and world-Z increase together or oppositely
    /// on this map, from actual movement. Once known, "face it and go forward" is
    /// correct with no user flips — the maths does the rest.</summary>
    private void LearnAxisSign(EchoesUnseen.Models.MumbleLinkData data)
    {
        if (_havePrev)
        {
            double dCy = data.PlayerY - _prevPcy;
            double dWz = data.AvatarZ - _prevWz;
            if (Math.Abs(dCy) > 3 && Math.Abs(dWz) > 1)
                _signZ = Math.Sign(dWz / dCy);
        }
        _prevPcy = data.PlayerY; _prevWz = data.AvatarZ; _havePrev = true;
    }

    /// <summary>Convert an objective's continent coords into world metres, anchored on
    /// the player's own live position so any offset/unit error cancels out — only the
    /// direction (which is what steers you) has to be right. Scale comes from the map
    /// rects; the north/south sign is the learned one (map-rect default until learned).</summary>
    private bool ComputeWorldTarget(EchoesUnseen.Models.MumbleLinkData data,
                                    double contX, double contY, out double wx, out double wz)
    {
        wx = wz = 0;
        var map = _currentMap;
        if (map?.MapRect is not { Length: >= 2 } mr || map.ContinentRect is not { Length: >= 2 } cr)
            return false;
        if (mr[0].Length < 2 || mr[1].Length < 2 || cr[0].Length < 2 || cr[1].Length < 2) return false;

        double crW = cr[1][0] - cr[0][0], crH = cr[1][1] - cr[0][1];
        if (Math.Abs(crW) < 1e-6 || Math.Abs(crH) < 1e-6) return false;

        const double InchesPerMetre = 39.3701;
        double scaleX = Math.Abs((mr[1][0] - mr[0][0]) / InchesPerMetre / crW);   // metres per continent unit
        double scaleZ = Math.Abs((mr[1][1] - mr[0][1]) / InchesPerMetre / crH);

        // East/continent-X always increase together in GW2 (maps are north-up).
        // North/south sign: use the learned one; until learned, the map-rect implies
        // world-Z DEcreases as continent-Y increases (the (1 - fracY) flip) → -1.
        int signZ = _signZ != 0 ? _signZ : -1;

        wx = data.AvatarX + scaleX * (contX - data.PlayerX);
        wz = data.AvatarZ + signZ * scaleZ * (contY - data.PlayerY);
        return true;
    }

    private void GuideTick()
    {
        var s = App.Settings.Current;
        if (_mumble == null) return;
        var data = _mumble.Read();
        if (data == null) return;

        // Following a chosen trail takes over from "nearest objective" guidance — it's
        // a real walkable route, so it can steer you around walls instead of into them.
        if (_follower.HasTrail) { FollowTrailTick(data, s); return; }

        if (!s.VoiceGuideEnabled || s.QuietMode || !_worldTarget.valid) return;

        var g = NavGuide.Compute(data.AvatarX, data.AvatarZ, data.CameraFrontX, data.CameraFrontZ,
                                 _worldTarget.wx, _worldTarget.wz, s.NavGuideFlipTurns);

        // Speak a cue only when it CHANGES — quiet while you hold the line, prompt
        // the instant you drift or arrive at a turn. Arrival ("you've reached…") is
        // handled by the poll loop, so here we hush inside the arrival radius.
        if (g.DistanceMetres <= 6) return;
        string turn = g.Turn;
        var now = DateTime.UtcNow;
        if (turn == _lastTurnSpoken || (now - _lastTurnAt).TotalMilliseconds < 1100) return;
        _lastTurnSpoken = turn; _lastTurnAt = now;

        string phrase = g.Ahead ? $"Straight ahead, {(int)Math.Round(g.DistanceMetres)} metres."
                      : turn == "turn around" ? "It's behind you. Turn around."
                      : Cap(turn) + ".";
        _ = _tts?.SpeakAsync(phrase, engineOverride: "winnatural");
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

    /// <summary>One tick of trail-following: work out where we are on the route, cue the
    /// drift sound, and speak a turn only when the instruction actually changes.</summary>
    private void FollowTrailTick(EchoesUnseen.Models.MumbleLinkData data, EchoesUnseen.Models.AppSettings s)
    {
        var (fx, fz) = FacingFor(data);
        var st = _follower.Update(data.AvatarX, data.AvatarZ, fx, fz, s.NavGuideFlipTurns);
        if (!st.Valid) return;

        // Record the actual numbers roughly once a second. Without this, a report of
        // "it pointed the wrong way" is unfalsifiable — with it, the log shows the
        // position, the facing, the target and the resulting bearing, so a bad space
        // or a bad sign is visible instead of guessed at.
        var nowLog = DateTime.UtcNow;
        if ((nowLog - _lastGuideLog).TotalMilliseconds > 1000)
        {
            _lastGuideLog = nowLog;
            DiagLog.Log("GUIDE",
                $"pos=({data.AvatarX:F1},{data.AvatarZ:F1}) cont=({data.PlayerX:F0},{data.PlayerY:F0}) " +
                $"face=cam({fx:F2},{fz:F2}) " +
                $"aim=({st.AimX:F1},{st.AimZ:F1}) ang={st.Guide.AngleDeg:F0} " +
                $"turn='{st.Guide.Turn}' drift={st.DriftM:F1} side={st.DriftSide} " +
                $"rem={st.RemainingM:F0} state={st.State} far={st.FarAway} guide='{_activeGuideLabel}'");
        }

        // Point the compass at the SAME spot the voice is steering you toward, so the
        // arrow and the words can never contradict each other.
        if (st.AimX != 0 || st.AimZ != 0)
        {
            double adx = st.AimX - data.AvatarX, adz = st.AimZ - data.AvatarZ;
            NavState.SetTarget((float)st.AimX, (float)st.AimZ,
                (float)Math.Sqrt(adx * adx + adz * adz), _activeGuideLabel);
        }

        if (st.Arrived)
        {
            string done = $"You've reached the end of {_activeGuideLabel}.";
            _follower.Clear();
            GuideStatus.Text = done;
            Say(done);
            return;
        }

        GuideStatus.Text = st.FarAway
            ? $"{_activeGuideLabel} — heading to the start, {(int)st.RemainingM} m away."
            : st.OffPath
                ? $"{_activeGuideLabel} — off path by {(int)st.DriftM} m."
                : $"{_activeGuideLabel} — {st.Guide.Turn}, {(int)st.RemainingM} m to go.";

        if (!s.VoiceGuideEnabled || s.QuietMode) return;

        // TRAIL RECOVERY: announce only when the debounced state actually flips, so
        // hovering near a threshold can't repeat itself. These are the only two lines
        // that interrupt you about the trail itself.
        if (st.StateChanged)
        {
            if (st.State == TrailFollower.TrailState.OffTrail)
            {
                EarconService.Shared?.WardCue(up: true);
                _ = _tts?.SpeakAsync(
                    $"You are leaving the trail. It's {(int)st.DriftM} metres to your " +
                    $"{(st.DriftSide < 0 ? "left" : "right")}.", engineOverride: "winnatural");
                _lastTrailCue = "recovery";
                _lastTrailCueAt = DateTime.UtcNow;
                return;
            }
            if (st.State == TrailFollower.TrailState.OnTrail && _wasOffTrail)
            {
                EarconService.Shared?.WardCue(up: false);
                _ = _tts?.SpeakAsync("Trail reacquired.", engineOverride: "winnatural");
                _lastTrailCue = "reacquired";
                _lastTrailCueAt = DateTime.UtcNow;
                _wasOffTrail = false;
                return;
            }
        }
        if (st.State == TrailFollower.TrailState.OffTrail) _wasOffTrail = true;

        // Speak only on change, so it's quiet while you hold the line. While walking to
        // the start we also re-announce on big distance milestones, otherwise a long
        // approach would be completely silent and feel broken.
        var now = DateTime.UtcNow;
        string cue = st.FarAway ? $"start|{st.Guide.Turn}|{(int)(st.RemainingM / 50)}"
                   : st.OffPath ? $"off|{st.DriftSide}"
                   : st.Guide.Turn;
        if (cue == _lastTrailCue || (now - _lastTrailCueAt).TotalMilliseconds < 1400) return;
        _lastTrailCue = cue; _lastTrailCueAt = now;

        string phrase = st.FarAway
            ? (st.Guide.Ahead
                ? $"Head straight on to the start, {(int)st.RemainingM} metres."
                : $"{Cap(st.Guide.Turn)} for the start, {(int)st.RemainingM} metres.")
            : st.OffPath
                ? $"Off trail. The path is {(int)st.DriftM} metres to your {(st.DriftSide < 0 ? "left" : "right")}."
                : st.Guide.Ahead
                    ? $"Straight ahead, {(int)st.RemainingM} metres to go."
                    : Cap(st.Guide.Turn) + ".";
        _ = _tts?.SpeakAsync(phrase, engineOverride: "winnatural");
    }

    // ── Guides tab ────────────────────────────────────────────────────────────
    //
    // Pack menu paths are written for a mouse-driven tick-box tree, so read aloud they
    // are mostly noise: every line starts with the pack's own name, and the leaf is
    // usually TacO's on/off label ("Toggle Trail", "Toggle Route", "01-02_2") rather
    // than anything descriptive — the actual name sits one level up. These two helpers
    // turn that into "Guild Missions → Rurik's View", which is what you'd want read out.

    private static readonly string[] NoiseWords =
    {
        "toggle trail", "toggle trails", "toggle route", "toggle routes",
        "toggle", "trail", "trails", "route", "routes",
    };

    private static bool IsNoise(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        foreach (var w in NoiseWords) if (t == w) return true;
        // "01-02_2" and friends carry no meaning read aloud.
        return t.All(ch => char.IsDigit(ch) || ch is '-' or '_' or ' ');
    }

    /// <summary>The topic a guide belongs under — the level below the pack's own name,
    /// e.g. "Guild Missions", "Festivals", "Hero Points Run".</summary>
    private static string TopicOf(string categoryPath)
    {
        var parts = categoryPath.Split('›', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return "Guides";
        // parts[0] is the pack name — the topic is the next level down.
        var topic = parts.Count > 1 ? parts[1] : parts[0];
        return topic.Trim('[', ']', '-', ' ');
    }

    /// <summary>The most meaningful name for a guide: walk back from the leaf, skipping
    /// TacO's "Toggle…" switch labels and bare numbers, and strip a leading "Toggle ".</summary>
    private static string PrettyTrailName(string categoryPath, string leaf)
    {
        var parts = categoryPath.Split('›', StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (!string.IsNullOrWhiteSpace(leaf)) parts.Add(leaf.Trim());

        for (int i = parts.Count - 1; i >= 1; i--)      // never fall back to the pack name
            if (!IsNoise(parts[i]))
            {
                var n = parts[i];
                if (n.StartsWith("Toggle ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
                return n.Trim();
            }
        return string.IsNullOrWhiteSpace(leaf) ? "Guide" : leaf;
    }

    // ── Guide list sizing ─────────────────────────────────────────────────────
    //
    // VIP is the high-accessibility mode, so the guide list scales up there rather than
    // asking someone to zoom the whole screen to tell two rows apart. Bigger type, far
    // more space between rows so they are distinct targets, and stronger contrast: pure
    // white on near-black, with a vivid plate behind each heading.
    private static bool Vip => string.Equals(App.Settings.Current.AccessMode, "vip",
                                             StringComparison.OrdinalIgnoreCase);

    private static double GuideRowFont   => Vip ? 22 : 17;
    private static double GuideTopicFont => Vip ? 26 : 18;
    private static double GuideWpFont    => Vip ? 17 : 14;
    private static Thickness GuideRowPad => Vip ? new Thickness(18, 16, 18, 16) : new Thickness(12, 10, 12, 10);
    private static Thickness GuideRowGap => Vip ? new Thickness(0, 0, 0, 14)    : new Thickness(0, 0, 0, 6);
    private static Thickness GuideWpGap  => Vip ? new Thickness(22, 0, 0, 20)   : new Thickness(16, 0, 0, 10);

    /// <summary>Rebuild the guide list for the current map, grouped under the pack's own
    /// menu headings so a big pack stays readable instead of a wall of names.</summary>
    private void RefreshGuideList()
    {
        if (GuideList == null) return;
        GuideList.Children.Clear();
        _guideRows.Clear();
        _mapTrails = MergeFragments(_taco.TrailsForMap(_lastMapId));

        string filter = GuideFilter?.Text?.Trim() ?? "";
        var shown = string.IsNullOrEmpty(filter)
            ? _mapTrails
            : _mapTrails.Where(t => (t.Name + " " + t.Category).Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // Header names the AREA you're in, the way Map Completion does, so it's obvious
        // the list is about here and now.
        string area = _currentMap?.MapName is { Length: > 0 } mn ? mn : $"map {_lastMapId}";
        GuideIntro.Text = _mapTrails.Count == 0
            ? $"No guides here ({area})."
            : $"{_mapTrails.Count} guides in {area}. Pick one and I'll walk you along it.";

        if (shown.Count == 0)
        {
            GuideList.Children.Add(new TextBlock
            {
                Text = _mapTrails.Count == 0
                    ? "No guides for this area. Import a marker pack below, or travel to a map your packs cover."
                    : "No guides match that search.",
                Foreground = Brushes.White,
                FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // Group by TOPIC (Guild Missions, Festivals, Hero Points…) rather than the full
        // pack path, which repeats the pack's name on every single line.
        foreach (var group in shown.GroupBy(t => TopicOf(t.Category)).OrderBy(g => g.Key))
        {
            var header = new Border
            {
                // A solid vivid plate in VIP rather than a faint wash, so the heading
                // separates the groups at a glance instead of blending into the list.
                Background = new SolidColorBrush(Vip ? Color.FromRgb(0xFF, 0xC4, 0x00)
                                                     : Color.FromArgb(0x38, 0xFF, 0xC4, 0x00)),
                CornerRadius = new CornerRadius(Vip ? 6 : 4),
                Padding = Vip ? new Thickness(14, 10, 14, 10) : new Thickness(8, 5, 8, 5),
                Margin = Vip ? new Thickness(0, 22, 0, 12) : new Thickness(0, 12, 0, 6),
                Child = new TextBlock
                {
                    Text = $"{group.Key}  ({group.Count()})",
                    // Black on bright gold is the strongest pairing available here.
                    Foreground = Vip ? Brushes.Black : Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = GuideTopicFont,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            System.Windows.Automation.AutomationProperties.SetName(header,
                $"{group.Key}, {group.Count()} guides");
            GuideList.Children.Add(header);

            foreach (var t in Disambiguate(group, area).OrderBy(x => x.Label))
            {
                double metres = TrailLengthM(t.Trail);
                var btn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = $"{t.Label}   —   {(int)metres} m",
                        Foreground = Brushes.White,
                        FontSize = GuideRowFont,
                        FontWeight = FontWeights.Bold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    Template = RowTemplate,
                    // Fully opaque near-black under pure white: maximum contrast, and no
                    // panel background bleeding through to wash the text out.
                    Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x12)),
                    Padding = GuideRowPad,
                    Margin = GuideRowGap,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                System.Windows.Automation.AutomationProperties.SetName(btn,
                    $"{t.Label}, {(int)metres} metres, in {group.Key}. Select to be guided along it.");
                var trail = t.Trail;
                var label = t.Label;
                btn.Click += (_, _) => StartGuide(trail, label);
                GuideList.Children.Add(btn);
                _guideRows.Add(new GuideRow(btn, trail,
                    $"{t.Label}   —   {(int)metres} m",
                    $"{t.Label}, {(int)metres} metres, in {group.Key}."));

                // Travel button: grabs the waypoint nearest this guide's start.
                var wpBtn = new Button
                {
                    Content = new TextBlock
                    {
                        Text = "⧉  Copy waypoint to get here",
                        Foreground = Brushes.White,
                        FontSize = GuideWpFont,
                        FontWeight = FontWeights.SemiBold,
                    },
                    Template = RowTemplate,
                    // Solid vivid blue, clearly a different control from the guide row
                    // above it — distinguished by shape and position, not colour alone.
                    Background = new SolidColorBrush(Vip ? Color.FromRgb(0x1B, 0x5A, 0xD8)
                                                         : Color.FromArgb(0x66, 0x29, 0x79, 0xFF)),
                    Padding = Vip ? new Thickness(16, 10, 16, 10) : new Thickness(12, 6, 12, 6),
                    Margin = GuideWpGap,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                System.Windows.Automation.AutomationProperties.SetName(wpBtn,
                    $"Copy the waypoint code nearest the start of {label}, to paste into chat and travel there");
                wpBtn.Click += (_, _) => CopyWaypointNearTrail(trail);
                GuideList.Children.Add(wpBtn);
            }
        }

        // The list is rebuilt on every filter keystroke, so re-mark the running guide
        // here as well - otherwise typing in the filter box would wipe the highlight.
        ApplyActiveHighlight();
    }

    /// <summary>
    /// Packs often chop ONE journey into a pile of numbered fragments — a map can hold
    /// eight separate files called "01-02", "02-03", "03-04"… that are really successive
    /// legs of the same walk. Listing them separately is both repetitive to read and
    /// useless to follow, so any set of trails sharing a parent category is stitched back
    /// into a single guide, its legs in order.
    /// </summary>
    private static List<TacoService.Trail> MergeFragments(List<TacoService.Trail> trails)
    {
        static string Parent(string category)
        {
            var i = category.LastIndexOf('›');
            return i > 0 ? category.Substring(0, i).Trim() : category;
        }

        var outp = new List<TacoService.Trail>();
        foreach (var grp in trails.GroupBy(t => Parent(t.Category)))
        {
            var members = grp.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Count == 1) { outp.Add(members[0]); continue; }

            // Keep each leg as its own segment so the drawing still breaks between them
            // rather than inventing a line across the gap.
            var segs = new List<List<double[]>>();
            foreach (var m in members) segs.AddRange(m.Segments);

            var leaf = Parent(members[0].Category);
            var name = leaf.Contains('›') ? leaf.Substring(leaf.LastIndexOf('›') + 1).Trim() : leaf;
            if (name.StartsWith("Toggle ", StringComparison.OrdinalIgnoreCase)) name = name.Substring(7).Trim();

            outp.Add(new TacoService.Trail(members[0].MapId, segs, leaf, name));
        }
        return outp;
    }

    private static List<string> PathParts(TacoService.Trail t)
    {
        var parts = t.Category.Split('›', StringSplitOptions.RemoveEmptyEntries)
                              .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (!string.IsNullOrWhiteSpace(t.Name)) parts.Add(t.Name.Trim());
        return parts;
    }

    private static string CleanSeg(string s) =>
        s.StartsWith("Toggle ", StringComparison.OrdinalIgnoreCase) ? s.Substring(7).Trim() : s.Trim();

    /// <summary>
    /// Make every label in a group unique. Packs reuse the same wording constantly — a
    /// map can hold eight guides all called "Travel Trails" — and eight identical rows
    /// are useless when they're being read out one after another. So where labels
    /// collide we find the deepest point in the pack's path where those guides actually
    /// differ, and add just that ("Travel Trails (01-02)", "Divinity's Reach (Routes -
    /// On Foot)").
    /// </summary>
    private static List<(TacoService.Trail Trail, string Label)> Disambiguate(
        IEnumerable<TacoService.Trail> items, string area)
    {
        var based = items.Select(t => (Trail: t, Label: PrettyTrailName(t.Category, t.Name))).ToList();
        var result = new List<(TacoService.Trail, string)>();

        foreach (var grp in based.GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
        {
            var members = grp.ToList();
            if (members.Count == 1) { result.Add((members[0].Trail, Tidy(grp.Key, area))); continue; }

            var parts = members.Select(m => PathParts(m.Trail)).ToList();
            int maxLen = parts.Max(p => p.Count);
            int at = -1;
            for (int i = maxLen - 1; i >= 1; i--)
            {
                var vals = parts.Select(p => i < p.Count ? p[i] : "").ToList();
                if (vals.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) { at = i; break; }
            }

            for (int k = 0; k < members.Count; k++)
            {
                string extra = at >= 0 && at < parts[k].Count ? CleanSeg(parts[k][at]) : (k + 1).ToString();
                result.Add((members[k].Trail, Tidy($"{grp.Key} ({extra})", area)));
            }
        }
        return result;
    }

    /// <summary>Drop the map's own name out of a label — you already know where you are,
    /// so "Eastlurk Alley | Divinity's Reach | Dwayna WP" reads better without it.</summary>
    private static string Tidy(string label, string area)
    {
        if (!label.Contains('|')) return label;
        var keep = label.Split('|')
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0 && !p.Equals(area, StringComparison.OrdinalIgnoreCase))
                        .ToList();
        return keep.Count > 0 ? string.Join(" · ", keep) : label;
    }

    /// <summary>
    /// Copy the chat code of the waypoint closest to where a guide BEGINS, so you can
    /// paste it into chat, click it, and travel there instead of walking across the map.
    /// This is what makes the jumping-puzzle guides usable: the puzzle is the easy part,
    /// getting to it blind is the wall.
    ///
    /// Waypoints come from the API in continent coords while trails are world metres, so
    /// each candidate is converted before comparing.
    /// </summary>
    private void CopyWaypointNearTrail(TacoService.Trail t)
    {
        var startPt = t.Segments.FirstOrDefault()?.FirstOrDefault();
        if (startPt == null || _mumble?.Read() is not { } data)
        { Say("Couldn't work out where that guide starts."); return; }

        Obj? best = null; double bestD = double.MaxValue;
        foreach (var wp in AllWaypoints())
        {
            if (!ComputeWorldTarget(data, wp.X, wp.Y, out double wx, out double wz)) continue;
            double dx = wx - startPt[0], dz = wz - startPt[2];
            double d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = wp; }
        }

        if (best == null) { Say("No waypoint found on this map."); return; }
        CopyWaypoint(best);
    }

    /// <summary>Walking length of a trail in metres — far more use than a point count.</summary>
    private static double TrailLengthM(TacoService.Trail t)
    {
        double total = 0;
        foreach (var seg in t.Segments)
            for (int i = 1; i < seg.Count; i++)
            {
                double dx = seg[i][0] - seg[i - 1][0], dz = seg[i][2] - seg[i - 1][2];
                total += Math.Sqrt(dx * dx + dz * dz);
            }
        return total;
    }

    private string _activeGuideLabel = "guide";

    // -- Showing WHICH guide is running ---------------------------------------
    //
    // The list gave no sign of which guide you had picked: every row looked identical
    // before and after choosing one, so the only confirmation was a spoken line you might
    // have missed. The running guide now gets a green plate, and the Stop button turns
    // red and says what it will stop.
    //
    // Colour is never the only signal - the row text gains a marker and both controls
    // rewrite their screen-reader names - because a green row is no use to someone who
    // cannot pick green out, which is the entire point of this app.

    private sealed record GuideRow(Button Button, TacoService.Trail Trail, string Text, string Name);
    private readonly List<GuideRow> _guideRows = new();

    /// <summary>Green plate on the guide being followed, red on the Stop button.</summary>
    private void ApplyActiveHighlight()
    {
        var active = TrailFollower.Current;

        foreach (var row in _guideRows)
        {
            bool on = active != null && SameTrail(row.Trail, active);

            row.Button.Background = new SolidColorBrush(
                on ? (Vip ? Color.FromRgb(0x22, 0xC5, 0x5E)     // bright green, black text
                          : Color.FromRgb(0x0E, 0x7A, 0x3C))    // deep green, white text
                   : Color.FromRgb(0x0A, 0x0A, 0x12));

            if (row.Button.Content is TextBlock tb)
            {
                tb.Text = on ? "\u25B6  " + row.Text + "   \u2014   following" : row.Text;
                tb.Foreground = on && Vip ? Brushes.Black : Brushes.White;
            }

            System.Windows.Automation.AutomationProperties.SetName(row.Button,
                on ? $"{row.Name} Currently following this guide. Select to restart it."
                   : $"{row.Name} Select to be guided along it.");
        }

        if (StopGuideBtn != null)
        {
            bool running = active != null;
            // Clearing the value rather than assigning the theme colour back: the style
            // setter then takes over again, so the button keeps following the theme if it
            // is changed later instead of being frozen at whatever it was tonight.
            if (running)
                StopGuideBtn.Background = new SolidColorBrush(Color.FromRgb(0xD9, 0x2D, 0x20));
            else
                StopGuideBtn.ClearValue(BackgroundProperty);
            StopGuideBtn.Content = running ? "\u23F9 Stop guide" : "\u2716 Stop guide";
            System.Windows.Automation.AutomationProperties.SetName(StopGuideBtn,
                running ? $"Stop guide. Currently following {_activeGuideLabel}."
                        : "Stop guide. No guide is running.");
        }
    }

    /// <summary>Is this the same guide? Reference equality normally settles it, but the
    /// list is rebuilt on every filter keystroke, so fall back to identity by content -
    /// otherwise the highlight would vanish the moment you typed in the filter box.</summary>
    private static bool SameTrail(TacoService.Trail a, TacoService.Trail b) =>
        ReferenceEquals(a, b) ||
        (a.MapId == b.MapId && a.Name == b.Name && a.Category == b.Category &&
         a.Segments.Count == b.Segments.Count);

    private void StartGuide(TacoService.Trail t, string? label = null)
    {
        _follower.SetTrail(t);
        _lastTrailCue = "";
        _activeGuideLabel = string.IsNullOrWhiteSpace(label) ? PrettyTrailName(t.Category, t.Name) : label;
        string msg = $"Following {_activeGuideLabel}.";
        GuideStatus.Text = msg;
        ApplyActiveHighlight();
        _tts?.StopSpeaking();
        Say(msg + " I'll call the turns.");
    }

    private void StopGuide_Click(object sender, RoutedEventArgs e)
    {
        _follower.Clear();
        _lastTrailCue = "";
        if (GuideStatus != null) GuideStatus.Text = "No guide selected.";
        ApplyActiveHighlight();
        Say("Guide stopped.");
    }

    private void GuideFilter_Changed(object sender, TextChangedEventArgs e) => RefreshGuideList();

    /// <summary>On-demand "which way?" — the clock check (hotkey). Speaks direction and
    /// distance to the current target using the camera facing as 12 o'clock. Works as a
    /// manual read, so it ignores quiet mode.</summary>
    public void SpeakGuideDirection()
    {
        var s = App.Settings.Current;
        if (_mumble == null) { Say("Navigation isn't ready yet."); return; }
        var data = _mumble.Read();
        if (data == null) { Say("Guild Wars 2 isn't running."); return; }
        if (!_worldTarget.valid)
        {
            Say("No objective to guide to yet. Open Trail Navigator, or import a marker pack and pick one.");
            return;
        }
        var g = NavGuide.Compute(data.AvatarX, data.AvatarZ, data.CameraFrontX, data.CameraFrontZ,
                                 _worldTarget.wx, _worldTarget.wz, s.NavGuideFlipTurns);
        _tts?.StopSpeaking();
        Say(NavGuide.ClockLine(g, _worldTarget.name));
    }

    /// <summary>
    /// Self-check (hotkey). Speaks — and logs — what every part of the guidance believes
    /// RIGHT NOW: where you are, which way you face, where the target is, and the bearing
    /// each system computes. If the compass and the voice ever disagree again, this says
    /// so in one sentence instead of costing another play session to discover.
    /// </summary>
    public void SpeakSelfCheck()
    {
        if (_mumble?.Read() is not { } d) { Say("Guild Wars 2 isn't running."); return; }
        var s = App.Settings.Current;
        var (fx, fz) = FacingFor(d);

        bool haveTarget = NavState.TryGetTarget(out float tx, out float tz, out float tdist, out string tname);
        if (!haveTarget)
        {
            Say("Nothing is being guided to right now. Pick a guide, or stand in a map with objectives.");
            DiagLog.Log("SELFCHECK", $"no target. pos=({d.AvatarX:F1},{d.AvatarZ:F1}) face=({fx:F2},{fz:F2})");
            return;
        }

        var g = NavGuide.Compute(d.AvatarX, d.AvatarZ, fx, fz, tx, tz, s.NavGuideFlipTurns);
        string facing = "camera";

        DiagLog.Log("SELFCHECK",
            $"pos=({d.AvatarX:F1},{d.AvatarZ:F1}) face={facing}({fx:F2},{fz:F2}) " +
            $"target=({tx:F1},{tz:F1}) '{tname}' dist={g.DistanceMetres:F1} " +
            $"ang={g.AngleDeg:F0} clock={g.ClockHour} turn='{g.Turn}' " +
            $"trail={(_follower.HasTrail ? _activeGuideLabel : "none")} flip={s.NavGuideFlipTurns}");

        _tts?.StopSpeaking();
        Say($"Check. Target {tname}, {(int)g.DistanceMetres} metres, {g.ClockHour} o'clock, {g.Turn}. " +
            $"Using {facing} facing. The compass and the voice are both using this same target. " +
            $"If that direction is wrong, the details are in the log.");
    }

    /// <summary>Toggle spoken turn-by-turn steering (hotkey), with a spoken confirmation.</summary>
    public void ToggleVoiceGuide()
    {
        var s = App.Settings.Current;
        s.VoiceGuideEnabled = !s.VoiceGuideEnabled;
        App.Settings.NotifyChanged();
        _lastTurnSpoken = "";
        _tts?.StopSpeaking();
        Say(s.VoiceGuideEnabled ? "Turn by turn guide on." : "Turn by turn guide off.");
    }

    private void Say(string t) => _ = _tts?.SpeakAsync(t, engineOverride: "winnatural");

    // ── Waypoint chat-link "auto-clipper" ─────────────────────────────────────
    /// <summary>Build a GW2 chat link for a point of interest / waypoint from its id.
    /// Format: a 0x04 type byte + the id as a little-endian uint32, base64-encoded and
    /// wrapped in [&amp;...]. (POI id 588 → "[&amp;BEwCAAA=]", which the game turns into a
    /// clickable waypoint link when pasted into chat.)</summary>
    public static string WaypointChatCode(int poiId)
    {
        var bytes = new byte[]
        {
            0x04,
            (byte)(poiId & 0xFF), (byte)((poiId >> 8) & 0xFF),
            (byte)((poiId >> 16) & 0xFF), (byte)((poiId >> 24) & 0xFF),
        };
        return "[&" + Convert.ToBase64String(bytes) + "]";
    }

    /// <summary>Copy a waypoint's chat link to the clipboard and say so, so it can be
    /// pasted into GW2 chat and clicked to travel — the way around the map-clicking
    /// wall. The clipboard is occasionally locked by another app, so we retry once.</summary>
    private void CopyWaypoint(Obj wp)
    {
        if (wp.PoiId <= 0) { Say($"{wp.Name} has no waypoint code."); return; }
        var code = WaypointChatCode(wp.PoiId);
        bool ok = false;
        for (int attempt = 0; attempt < 2 && !ok; attempt++)
        {
            try { System.Windows.Clipboard.SetText(code); ok = true; }
            catch { System.Threading.Thread.Sleep(60); }
        }
        _tts?.StopSpeaking();
        Say(ok ? $"{wp.Name} copied. Open chat, paste, and click the link to travel."
               : $"Couldn't copy {wp.Name} — the clipboard was busy. Try again.");
        DiagLog.Log("WAYPOINT", $"copy {wp.Name} id={wp.PoiId} -> {code} ok={ok}");
    }

    /// <summary>Hotkey: copy the nearest waypoint to the player. The fast "get me out
    /// of here / regroup" path — no list navigation needed.</summary>
    public void CopyNearestWaypoint()
    {
        var wp = AllWaypoints().OrderBy(o => o.DistSq).FirstOrDefault();
        if (wp == null) { Say("No waypoint found on this map yet. Stand in a map and try again."); return; }
        CopyWaypoint(wp);
    }

    /// <summary>Every waypoint on the current map (not just the nearest five in the
    /// list), so the hotkey can always reach one.</summary>
    private IEnumerable<Obj> AllWaypoints()
    {
        if (_currentMap?.PointsOfInterest == null || _mumble?.Read() is not { } d) return Enumerable.Empty<Obj>();
        return _currentMap.PointsOfInterest.Values
            .Where(p => p.Type == "waypoint" && p.Id > 0)
            .Select(p => new Obj(p.Name ?? "Waypoint", "Waypoint", p.X, p.Y,
                (p.X - d.PlayerX) * (p.X - d.PlayerX) + (p.Y - d.PlayerY) * (p.Y - d.PlayerY), p.Id));
    }

    // ── Marker-pack (TacO) guidance ───────────────────────────────────────────
    private void ImportMarkers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose marker pack(s) — .taco, .xml or .trl",
                Filter = "Marker packs (*.taco;*.xml;*.trl)|*.taco;*.xml;*.trl|All files (*.*)|*.*",
                Multiselect = true, CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            int total = 0, trails = 0;
            foreach (var f in dlg.FileNames) { total += _taco.Import(f); trails += _taco.LastTrailsAdded; }
            RefreshMarkerCategories(_lastMapId);
            (System.Windows.Application.Current?.MainWindow as EchoesUnseen.MainWindow)?.ReloadMarkerOverlay();
            string trailBit = trails > 0 ? $" and {trails} painted trail{(trails == 1 ? "" : "s")}" : "";
            MarkerStatus.Text = $"Imported {total} markers{trailBit}. Choose a category to be guided to it.";
            _tts?.SpeakAsync($"Imported {total} markers{trailBit}.");
        }
        catch (Exception ex) { CrashLogger.Log("ImportMarkers", ex); MarkerStatus.Text = "Import failed: " + ex.Message; }
    }

    private void MarkerCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        var sel = MarkerCategoryCombo.SelectedItem as string;
        _markerCategory = (sel == null || sel.StartsWith("(off")) ? null : sel;
        // The on-screen overlay shows whatever you're guiding to.
        App.Settings.Current.MarkerVisualCategory = _markerCategory ?? "";
        App.Settings.NotifyChanged();
        if (!string.IsNullOrEmpty(_markerCategory))
        {
            MarkerStatus.Text = $"Guiding to {_markerCategory} by sound.";
            _tts?.SpeakAsync($"Guiding to {_markerCategory}.");
        }
    }

    private void MarkerVisual_Changed(object sender, RoutedEventArgs e)
    {
        if (MarkerVisualToggle == null) return;
        App.Settings.Current.MarkerVisualEnabled = MarkerVisualToggle.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void MarkerSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MarkerSizeLabel == null) return;
        App.Settings.Current.MarkerSize = MarkerSizeSlider.Value;
        // The slider is the accessibility WIDTH multiplier: 100 on the dial = 1.0x, so
        // the trail is TacO's normal 1.016 m wide; 400 = 4x for a bold, easy-to-see band.
        App.Settings.Current.TrailWidthMultiplier = Math.Clamp(MarkerSizeSlider.Value / 100.0, 0.25, 6.0);
        MarkerSizeLabel.Text = $"{App.Settings.Current.TrailWidthMultiplier:0.0}x";
        App.Settings.NotifyChanged();
    }

    private void RefreshMarkerCategories(int mapId)
    {
        if (MarkerCategoryCombo == null) return;
        var cats = _taco.CategoriesForMap(mapId);
        var keep = _markerCategory;
        MarkerCategoryCombo.Items.Clear();
        MarkerCategoryCombo.Items.Add("(off — use map objectives)");
        foreach (var c in cats) MarkerCategoryCombo.Items.Add(c);
        if (keep != null && cats.Contains(keep)) MarkerCategoryCombo.SelectedItem = keep;
        else { MarkerCategoryCombo.SelectedIndex = 0; _markerCategory = null; }
    }

    /// <summary>Guide to the nearest imported marker in the chosen category on this
    /// map, by sonar plus a throttled spoken direction. Marker coords and the player
    /// are both world-space metres; an inch-based pack is auto-detected and scaled.</summary>
    private void DoMarkerNav(EchoesUnseen.Models.MumbleLinkData data)
    {
        var markers = _taco.ForMap(data.MapId).Where(m => m.Category == _markerCategory).ToList();
        if (markers.Count == 0)
        {
            MarkerStatus.Text = $"No '{_markerCategory}' markers on this map.";
            _sonar.UpdateDistance(9999f, (float)(SonarVolumeSlider.Value / 100.0));
            return;
        }

        double bestD = double.MaxValue, twx = 0, twz = 0;
        foreach (var m in markers)
        {
            // Marker world position in metres. Inch-based packs (coords in the tens
            // of thousands) are auto-scaled so a marker sits where it really is.
            double mxw = m.X, mzw = m.Z;
            double dx = mxw - data.AvatarX, dz = mzw - data.AvatarZ;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d > 8000)   // pack stored in inches, not metres
            {
                mxw = m.X / 39.3701; mzw = m.Z / 39.3701;
                dx = mxw - data.AvatarX; dz = mzw - data.AvatarZ;
                d = Math.Sqrt(dx * dx + dz * dz);
            }
            if (d < bestD) { bestD = d; twx = mxw; twz = mzw; }
        }

        float distM = (float)bestD;
        _sonar.UpdateDistance(distM, (float)(SonarVolumeSlider.Value / 100.0));

        // Markers are already world-space, so the spoken guide can steer to them
        // directly — this is the cleanest path (no continent conversion at all).
        _worldTarget = (true, twx, twz, _markerCategory ?? "marker");

        var dir = RelativeDir(twx - data.AvatarX, twz - data.AvatarZ, data.FrontX, data.FrontY);
        MarkerStatus.Text = $"{_markerCategory}: {dir}, {(int)distM} m ({markers.Count} on map).";
        var now = DateTime.UtcNow;
        if ((now - _lastMarkerDir).TotalSeconds >= 6 && (dir != _lastMarkerDirSpoken || distM < 20))
        {
            _lastMarkerDir = now; _lastMarkerDirSpoken = dir;
            _ = _tts?.SpeakAsync(distM <= 12 ? $"You're at the {_markerCategory} marker."
                                             : $"{_markerCategory}, {dir}, {(int)distM} metres.",
                                 engineOverride: "winnatural");
        }
        DiagLog.Log("MARKER", $"cat={_markerCategory} nearest={(int)distM}m dir={dir} pcs=({data.AvatarX:F0},{data.AvatarZ:F0})");
    }

    /// <summary>Target direction relative to where the player faces: ahead / left /
    /// right / behind. Player facing (FrontX east, FrontY south) and the target
    /// offset (dx east, dz south) share the map plane.</summary>
    private static string RelativeDir(double dx, double dz, double fx, double fz)
    {
        double tAng = Math.Atan2(dx, -dz);
        double fAng = Math.Atan2(fx, -fz);
        double rel = (tAng - fAng) * 180.0 / Math.PI;
        while (rel > 180) rel -= 360;
        while (rel < -180) rel += 360;
        double a = Math.Abs(rel);
        if (a < 25) return "straight ahead";
        if (a > 155) return "behind you";
        if (a < 70) return rel > 0 ? "ahead and right" : "ahead and left";
        return rel > 0 ? "to your right" : "to your left";
    }

    private const float ArrivalUnits = 160f;

    // ── Progress / "been there" tracking (local; API has no unlock data) ──────
    private static string VisitKey(int mapId, Obj o) => $"{mapId}|{o.Type}|{o.Name}";
    private bool IsVisited(int mapId, Obj o) => App.Settings.Current.VisitedObjectives.Contains(VisitKey(mapId, o));

    private void MarkVisited(int mapId, Obj o)
    {
        var key = VisitKey(mapId, o);
        if (App.Settings.Current.VisitedObjectives.Contains(key)) return;
        App.Settings.Current.VisitedObjectives.Add(key);
        App.Settings.Save();
    }

    private Obj? NearestUnvisited(List<Obj> objs, int mapId) =>
        objs.Where(o => !IsVisited(mapId, o)).OrderBy(o => o.DistSq).FirstOrDefault();

    /// <summary>Show how many objectives on this map you've found so far.</summary>
    private void UpdateProgress(int mapId)
    {
        if (_currentMap == null) return;
        var all = CollectAll(_currentMap);
        int total = all.Count;
        int found = all.Count(o => IsVisited(mapId, o));
        ProgressText.Text = total > 0
            ? $"Found with Echoes Unseen: {found} of {total} on this map."
            : "";
    }

    /// <summary>Every objective on the map, ignoring the type filters — for the
    /// progress total.</summary>
    private List<Obj> CollectAll(MapObjectives m)
    {
        var list = new List<Obj>();
        if (m.PointsOfInterest != null)
            foreach (var p in m.PointsOfInterest.Values)
                list.Add(new Obj(p.Name ?? "Unnamed", NiceType(p.Type ?? "poi"), p.X, p.Y, 0));
        if (m.SkillChallenges != null)
            foreach (var s in m.SkillChallenges) list.Add(new Obj("Hero Challenge", "Hero", s.X, s.Y, 0));
        if (m.Tasks != null)
            foreach (var t in m.Tasks.Values) list.Add(new Obj(t.Objective ?? "Heart", "Heart", t.X, t.Y, 0));
        return list;
    }

    // ── Find a place or NPC (wiki-powered location lookup) ────────────────────
    private void FindBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = FindPlaceAsync(); }
    }

    private void Find_Click(object sender, RoutedEventArgs e) => _ = FindPlaceAsync();

    /// <summary>Look a place/NPC up on the GW2 wiki and read where it is. The game
    /// API exposes no NPC coordinates, so the wiki's location text is the honest
    /// source; we read the opening lines, which almost always state the location.</summary>
    private async Task FindPlaceAsync()
    {
        var q = FindBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;

        FindResult.Text = $"Searching the wiki for “{q}”…";
        _ = _tts?.SpeakAsync($"Searching for {q}.");
        try
        {
            var article = await _wiki.SearchAndFetchAsync(q);
            if (article == null || string.IsNullOrWhiteSpace(article.Extract))
            {
                FindResult.Text = $"I couldn't find “{q}” on the wiki. Try a different name.";
                _ = _tts?.SpeakAsync($"I couldn't find {q} on the wiki.");
                return;
            }
            // Smart location-first: lead with the sentence that states WHERE it is,
            // then a short bit of what it is. Fall back to the intro if there's no
            // clean location line.
            string spoken = LocationFirst(article.Title, article.Extract);
            FindResult.Text = spoken;
            _ = _tts?.SpeakAsync(spoken);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("TrailNavigator.FindPlace", ex);
            FindResult.Text = "The wiki lookup failed. Check your connection and try again.";
            _ = _tts?.SpeakAsync("The wiki lookup failed.");
        }
    }

    /// <summary>Lead with the location. Scans the wiki extract for a sentence that
    /// states where the thing is; if found, reads that first, then the opening
    /// line of what it is. Falls back to the intro when there's no clean location.</summary>
    private static string LocationFirst(string title, string extract)
    {
        string text = extract.Replace("\n", " ").Trim();
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=\.)\s+");
        string[] cues = { "located in", "found in", "can be found", "resides", "is located", "stands in", "situated in" };
        string? loc = sentences.FirstOrDefault(s => cues.Any(c => s.ToLowerInvariant().Contains(c)));
        string intro = sentences.FirstOrDefault() ?? "";
        if (!string.IsNullOrWhiteSpace(loc) && !loc.Equals(intro, StringComparison.Ordinal))
            return $"{title}. {intro} {loc}".Trim();
        return $"{title}. {TrimToSentences(text, 2, 320)}";
    }

    private static string TrimToSentences(string text, int maxSentences, int maxChars)
    {
        text = text.Replace("\n", " ").Trim();
        int count = 0, i = 0;
        for (; i < text.Length && i < maxChars; i++)
        {
            if (text[i] == '.' && ++count >= maxSentences) { i++; break; }
        }
        var s = text[..Math.Min(i, text.Length)].Trim();
        return s.Length < text.Length ? s : text.Length <= maxChars ? text : text[..maxChars] + "…";
    }

    /// <summary>The user picked a row: lock guidance onto THIS objective.</summary>
    private void SelectTarget(Obj o)
    {
        _locked = o;
        _arrivedName = "";
        if (_mumble?.Read() is { } md && ComputeWorldTarget(md, o.X, o.Y, out double sx, out double sz))
        {
            double sdx = sx - md.AvatarX, sdz = sz - md.AvatarZ;
            NavState.SetTarget((float)sx, (float)sz, (float)Math.Sqrt(sdx * sdx + sdz * sdz), o.Name);
        }
        _ = _tts?.SpeakAsync($"Guiding you to {o.Name}, {o.Type}.");
        RenderList();
    }

    private static (int wp, int poi, int vista, int hero, int heart) CountObjectives(MapObjectives m)
    {
        int wp = 0, poi = 0, vista = 0;
        if (m.PointsOfInterest != null)
            foreach (var p in m.PointsOfInterest.Values)
                switch (p.Type)
                {
                    case "waypoint": wp++; break;
                    case "landmark": poi++; break;
                    case "vista": vista++; break;
                }
        int hero = m.SkillChallenges?.Count ?? 0;
        int heart = m.Tasks?.Count ?? 0;
        return (wp, poi, vista, hero, heart);
    }

    private List<Obj> CollectObjects(MapObjectives m, float px, float py)
    {
        var list = new List<Obj>();
        bool wpOn = FilterWaypoints.IsChecked == true;
        bool poiOn = FilterPOIs.IsChecked == true;
        bool vistaOn = FilterVistas.IsChecked == true;
        bool heroOn = FilterHero.IsChecked == true;
        bool heartOn = FilterHearts.IsChecked == true;

        if (m.PointsOfInterest != null)
        {
            foreach (var p in m.PointsOfInterest.Values)
            {
                bool include = p.Type switch
                {
                    "waypoint" => wpOn,
                    "landmark" => poiOn,
                    "vista" => vistaOn,
                    _ => false,
                };
                if (!include) continue;
                var dx = p.X - px; var dy = p.Y - py;
                list.Add(new Obj(p.Name ?? "Unnamed", NiceType(p.Type ?? "poi"), p.X, p.Y, dx * dx + dy * dy, p.Id));
            }
        }
        if (heroOn && m.SkillChallenges != null)
        {
            foreach (var s in m.SkillChallenges)
            {
                var dx = s.X - px; var dy = s.Y - py;
                list.Add(new Obj("Hero Challenge", "Hero", s.X, s.Y, dx * dx + dy * dy));
            }
        }
        if (heartOn && m.Tasks != null)
        {
            foreach (var t in m.Tasks.Values)
            {
                var dx = t.X - px; var dy = t.Y - py;
                list.Add(new Obj(t.Objective ?? "Heart", "Heart", t.X, t.Y, dx * dx + dy * dy));
            }
        }
        return list;
    }

    private static string NiceType(string t) => t switch
    {
        "waypoint" => "Waypoint",
        "landmark" => "POI",
        "vista" => "Vista",
        _ => t,
    };

    // ── UI rendering ─────────────────────────────────────────────────────────
    private string _renderSig = "";

    private void RenderList()
    {
        // Only rebuild the buttons when the SET of objectives or the active target
        // changes — not every 2-second poll. Rebuilding constantly replaced the
        // row buttons out from under the cursor, so clicks to pick a target were
        // eaten (the "glitchy, bouncing" list). Live distances still update via
        // the compass and voice; the list order is stable so it's easy to click.
        var sig = string.Join("|", _nearest.Select(o => o.Name)) + "#" + (_active?.Name ?? "");
        if (sig == _renderSig && NearestList.Children.Count > 0) return;
        _renderSig = sig;

        NearestList.Children.Clear();
        if (_nearest.Count == 0)
        {
            NearestList.Children.Add(new TextBlock
            {
                Text = "(No objectives match your filters.)",
                Foreground = (Brush)FindResource("MutedBrush"),
            });
            return;
        }

        if (_mumble?.Read() is not { } data) return;

        foreach (var obj in _nearest)
        {
            bool isActive = _active != null && _active.Name == obj.Name
                && Math.Abs(_active.X - obj.X) < 1 && Math.Abs(_active.Y - obj.Y) < 1;

            var textBrush = isActive ? (Brush)FindResource("OnPrimaryBrush") : Brushes.White;
            var dir = Compass(data.PlayerX, data.PlayerY, obj.X, obj.Y);

            var grid = new Grid { IsHitTestVisible = false };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            var typeText = new TextBlock
            {
                Text = obj.Type,
                Foreground = isActive ? textBrush : (Brush)FindResource("PrimaryBrush"),
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            };
            Grid.SetColumn(typeText, 0); grid.Children.Add(typeText);

            var name = new TextBlock
            {
                Text = (isActive ? "▶ " : "") + obj.Name,
                Foreground = textBrush, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(name, 1); grid.Children.Add(name);

            var dist = new TextBlock
            {
                Text = $"{(int)obj.Distance}",
                Foreground = isActive ? textBrush : (Brush)FindResource("MutedBrush"),
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
            };
            Grid.SetColumn(dist, 2); grid.Children.Add(dist);

            var compass = new TextBlock
            {
                Text = dir, Foreground = textBrush, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 14, FontWeight = FontWeights.Bold,
            };
            Grid.SetColumn(compass, 3); grid.Children.Add(compass);

            // A Button so it's keyboard-focusable and announced by NVDA. Clicking
            // (or Enter/Space) picks this objective as the one to guide to.
            var row = new Button
            {
                Content = grid,
                Template = RowTemplate,
                Background = isActive
                    ? (Brush)FindResource("PrimaryBrush")
                    : new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            System.Windows.Automation.AutomationProperties.SetName(row,
                $"{obj.Type}, {obj.Name}, {(int)obj.Distance} units, {dir}. {(isActive ? "Currently guiding here." : "Select to guide here.")}");
            row.Click += (_, _) => SelectTarget(obj);

            // Waypoints also get a small "copy code" button, so you can grab the chat
            // link to paste-and-click for travel without hunting the world map. It's a
            // separate focusable control (NVDA reads it), so Enter still guides.
            if (obj.Type == "Waypoint" && obj.PoiId > 0)
            {
                var copyBtn = new Button
                {
                    Content = "⧉ Copy",
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                System.Windows.Automation.AutomationProperties.SetName(copyBtn,
                    $"Copy {obj.Name} waypoint code to clipboard");
                var wp = obj;
                copyBtn.Click += (_, _) => CopyWaypoint(wp);
                NearestList.Children.Add(row);
                NearestList.Children.Add(copyBtn);
            }
            else NearestList.Children.Add(row);
        }
    }

    // Flat button chrome for the nearest-objective rows: just the background and
    // content, no default button styling.
    private static readonly ControlTemplate RowTemplate =
        (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>" +
            "<ContentPresenter HorizontalAlignment='Stretch'/></Border></ControlTemplate>");

    /// <summary>Direction label N / NE / E / SE / S / SW / W / NW from player to target.</summary>
    private static string Compass(float px, float py, float tx, float ty)
    {
        var dx = tx - px;
        var dy = ty - py;
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        // Normalize to 0..360 with 0 = East
        if (angle < 0) angle += 360;
        // Convert so 0 = North (subtract 90, flip)
        var fromNorth = (90 - angle + 360) % 360;
        string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        int idx = (int)Math.Round(fromNorth / 45.0) % 8;
        return dirs[idx];
    }

    // ── Action handlers ──────────────────────────────────────────────────────
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_mumble == null) return;
        _ = Dispatcher.InvokeAsync(async () => await PollAsync());
    }

    private void Sonar_Changed(object sender, RoutedEventArgs e)
    {
        // Guard: WPF can fire this while the panel is still being BUILT,
        // before services are attached (v21.4 construction-crash sweep).
        if (_sonar == null || SonarEnabled == null) return;
        if (SonarEnabled.IsChecked == true)
        {
            // Fresh start: re-lock to nearest and allow arrivals to re-announce.
            _locked = null;
            _arrivedName = "";
            _sonar.SetHeartbeat(App.Settings.Current.SonarHeartbeat);
            _sonar.Start();
        }
        else _sonar.Stop();
    }

    private void SonarSound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_sonar == null || SonarSoundCombo?.SelectedItem is not ComboBoxItem it) return;
        var id = (string)it.Tag;
        App.Settings.Current.SonarSound = id;
        App.Settings.NotifyChanged();
        _sonar.SetSound(id);
    }

    private void SonarPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_sonar == null || SonarSoundCombo?.SelectedItem is not ComboBoxItem it) return;
        _sonar.PreviewOnce((string)it.Tag, (float)(SonarVolumeSlider.Value / 100.0));
    }

    private void SonarVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists — this was
        // the NullReferenceException that killed the panel on open (v21.4).
        if (SonarVolumeLabel == null) return;
        App.Settings.Current.SonarVolume = (float)(SonarVolumeSlider.Value / 100.0);
        App.Settings.NotifyChanged();
        SonarVolumeLabel.Text = $"{(int)SonarVolumeSlider.Value}%";
    }

    private void Heartbeat_Changed(object sender, RoutedEventArgs e)
    {
        if (_sonar == null || SonarHeartbeat == null) return;
        App.Settings.Current.SonarHeartbeat = SonarHeartbeat.IsChecked == true;
        App.Settings.NotifyChanged();
        _sonar.SetHeartbeat(SonarHeartbeat.IsChecked == true);
    }

    private async void ReadNearest_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null) return;
        // Always give audible feedback so the button never feels dead.
        if (_mumble?.Read() is not { } data)
        {
            await _tts.SpeakAsync("Waiting for Guild Wars 2. Launch the game and enter a map.");
            return;
        }
        if (_nearest.Count == 0)
        {
            await _tts.SpeakAsync("No nearby objectives yet. Check your filters, or press refresh.");
            return;
        }

        var top3 = _nearest.Take(3).ToList();
        var speech = "Nearest: " + string.Join(" ... ", top3.Select((o, i) =>
            $"{i + 1}: {o.Name}, {o.Type}, {(int)o.Distance} units {Compass(data.PlayerX, data.PlayerY, o.X, o.Y)}"));
        await _tts.SpeakAsync(speech);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_gw2Api == null) return;
        if (_lastMapId <= 0)
        {
            // No map yet — try a fresh read/poll rather than doing nothing.
            _ = _tts?.SpeakAsync("Refreshing.");
            await PollAsync();
            if (_lastMapId <= 0) _ = _tts?.SpeakAsync("Still waiting for Guild Wars 2 to load a map.");
            return;
        }
        _gw2Api.InvalidateMap(_lastMapId);
        _lastMapId = -1; // force refetch next poll
        _ = _tts?.SpeakAsync("Refreshing objectives.");
        await PollAsync();
        _ = _tts?.SpeakAsync(_nearest.Count > 0
            ? $"Refreshed. {_nearest.Count} nearby."
            : "Refreshed, but no objectives match your filters.");
    }
}
