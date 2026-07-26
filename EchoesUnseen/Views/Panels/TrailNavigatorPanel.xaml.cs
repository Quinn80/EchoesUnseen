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

    private DispatcherTimer? _poll;
    private int _lastMapId = -1;
    private MapObjectives? _currentMap;
    private List<Obj> _nearest = new();

    /// <summary>Internal row type combining type + coords + name.</summary>
    private record Obj(string Name, string Type, float X, float Y, float DistSq)
    {
        public float Distance => (float)Math.Sqrt(DistSq);
    }

    public TrailNavigatorPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SonarVolumeSlider.Value = App.Settings.Current.SonarVolume;
            StartPolling();
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
    public void StopBackgroundWork() { _poll?.Stop(); _poll = null; _sonar.Stop(); }

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

        // Fetch map objectives if map changed
        if (data.MapId != _lastMapId)
        {
            _lastMapId = data.MapId;
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

        // Compute distances and rank
        var objs = CollectObjects(_currentMap, data.PlayerX, data.PlayerY);
        _nearest = objs.OrderBy(o => o.DistSq).Take(5).ToList();
        RenderList();

        // Sonar follows the nearest target (if enabled)
        if (_nearest.Count > 0 && SonarEnabled.IsChecked == true)
        {
            _sonar.UpdateDistance(_nearest[0].Distance, (float)SonarVolumeSlider.Value);
        }
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
                list.Add(new Obj(p.Name ?? "Unnamed", NiceType(p.Type ?? "poi"), p.X, p.Y, dx * dx + dy * dy));
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
    private void RenderList()
    {
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
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x1A, 0x8A)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            var typeText = new TextBlock
            {
                Text = obj.Type,
                Foreground = (Brush)FindResource("PrimaryBrush"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            Grid.SetColumn(typeText, 0);
            grid.Children.Add(typeText);

            var name = new TextBlock
            {
                Text = obj.Name,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var dist = new TextBlock
            {
                Text = $"{(int)obj.Distance}",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            Grid.SetColumn(dist, 2);
            grid.Children.Add(dist);

            var compass = new TextBlock
            {
                Text = Compass(data.PlayerX, data.PlayerY, obj.X, obj.Y),
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
            };
            Grid.SetColumn(compass, 3);
            grid.Children.Add(compass);

            row.Child = grid;
            NearestList.Children.Add(row);
        }
    }

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
        if (SonarEnabled.IsChecked == true) _sonar.Start();
        else _sonar.Stop();
    }

    private void SonarVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists — this was
        // the NullReferenceException that killed the panel on open (v21.4).
        if (SonarVolumeLabel == null) return;
        App.Settings.Current.SonarVolume = (float)SonarVolumeSlider.Value;
        App.Settings.NotifyChanged();
        SonarVolumeLabel.Text = $"{(int)(SonarVolumeSlider.Value * 100)}%";
    }

    private async void ReadNearest_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _nearest.Count == 0) return;
        var top3 = _nearest.Take(3).ToList();
        if (_mumble?.Read() is not { } data) return;

        var speech = string.Join(" ... ", top3.Select((o, i) =>
            $"{i + 1}: {o.Name}, {o.Type}, {(int)o.Distance} units {Compass(data.PlayerX, data.PlayerY, o.X, o.Y)}"));
        await _tts.SpeakAsync(speech);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_gw2Api == null || _lastMapId <= 0) return;
        _gw2Api.InvalidateMap(_lastMapId);
        _lastMapId = -1; // force refetch next poll
        await PollAsync();
    }
}
