using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Map Completion — shows all completion objectives on the current map with
/// a running distance list and category counts.
///
/// REUSES the Gw2ApiService's per-map cache: if Trail Navigator or Heart
/// Quests already loaded the current map, this panel shows instantly.
///
/// TTS SUMMARIES:
///   - "Read Summary" announces the category totals
///   - "Read Next 5 Nearest" announces the 5 closest objectives with type,
///     distance, and direction — ideal for planning the next few minutes
///     of gameplay without opening the map.
/// </summary>
public partial class MapCompletionPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private DispatcherTimer? _poll;
    private int _lastMapId = -1;
    private MapObjectives? _currentMap;
    private List<ObjRow> _objectives = new();

    private record ObjRow(string Name, string Type, string ColorHex, float X, float Y, float DistSq)
    {
        public float Distance => (float)Math.Sqrt(DistSq);
    }

    public MapCompletionPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => StartPolling();
        Unloaded += (_, _) => _poll?.Stop();
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

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
            TotalProgress.Text = "Launch GW2 and enter a map.";
            ClearCounts();
            return;
        }

        if (data.MapId != _lastMapId)
        {
            _lastMapId = data.MapId;
            StatusText.Text = "Loading map data...";
            _currentMap = await _gw2Api.GetMapObjectivesAsync(data.MapId);
            if (_currentMap != null) MapName.Text = _currentMap.MapName;
            else { MapName.Text = $"Map {data.MapId}"; ClearCounts(); return; }
            StatusText.Text = "";
        }

        if (_currentMap == null) return;

        // Count categories
        int wp = 0, poi = 0, vista = 0;
        if (_currentMap.PointsOfInterest != null)
        {
            foreach (var p in _currentMap.PointsOfInterest.Values)
            {
                switch (p.Type)
                {
                    case "waypoint": wp++; break;
                    case "landmark": poi++; break;
                    case "vista": vista++; break;
                }
            }
        }
        int hero = _currentMap.SkillChallenges?.Count ?? 0;
        int hearts = _currentMap.Tasks?.Count ?? 0;
        int total = wp + poi + vista + hero + hearts;

        WaypointCount.Text = wp.ToString();
        PoiCount.Text = poi.ToString();
        VistaCount.Text = vista.ToString();
        HeroCount.Text = hero.ToString();
        HeartCount.Text = hearts.ToString();
        TotalProgress.Text = $"{total} objectives total on this map.";

        // Build unified list with distances for the nearest-5 feature
        var list = new List<ObjRow>();
        if (_currentMap.PointsOfInterest != null)
        {
            foreach (var p in _currentMap.PointsOfInterest.Values)
            {
                var dx = p.X - data.PlayerX; var dy = p.Y - data.PlayerY;
                var (type, color) = p.Type switch
                {
                    "waypoint" => ("Waypoint", "#60A5FA"),
                    "landmark" => ("POI", "#FACC15"),
                    "vista" => ("Vista", "#4ADE80"),
                    _ => ("POI", "#FACC15"),
                };
                list.Add(new ObjRow(p.Name ?? "Unnamed", type, color, p.X, p.Y, dx * dx + dy * dy));
            }
        }
        if (_currentMap.SkillChallenges != null)
        {
            foreach (var s in _currentMap.SkillChallenges)
            {
                var dx = s.X - data.PlayerX; var dy = s.Y - data.PlayerY;
                list.Add(new ObjRow("Hero Challenge", "Hero", "#C084FC", s.X, s.Y, dx * dx + dy * dy));
            }
        }
        if (_currentMap.Tasks != null)
        {
            foreach (var t in _currentMap.Tasks.Values)
            {
                var dx = t.X - data.PlayerX; var dy = t.Y - data.PlayerY;
                list.Add(new ObjRow(t.Objective ?? "Heart", "Heart", "#F87171", t.X, t.Y, dx * dx + dy * dy));
            }
        }
        _objectives = list.OrderBy(o => o.DistSq).ToList();
        RenderObjectives(data.PlayerX, data.PlayerY);
    }

    private void ClearCounts()
    {
        WaypointCount.Text = PoiCount.Text = VistaCount.Text = HeroCount.Text = HeartCount.Text = "—";
        ObjectiveList.Children.Clear();
    }

    private void RenderObjectives(float px, float py)
    {
        ObjectiveList.Children.Clear();
        foreach (var obj in _objectives.Take(50)) // cap for perf
        {
            var color = (Color)ColorConverter.ConvertFromString(obj.ColorHex);
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var type = new TextBlock
            {
                Text = obj.Type,
                Foreground = new SolidColorBrush(color),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(type, 0);
            grid.Children.Add(type);

            var name = new TextBlock
            {
                Text = obj.Name,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
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
                Text = CompassHelper.Direction(px, py, obj.X, obj.Y),
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
            };
            Grid.SetColumn(compass, 3);
            grid.Children.Add(compass);

            row.Child = grid;
            ObjectiveList.Children.Add(row);
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────
    private async void ReadSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _currentMap == null) return;
        var speech = $"{MapName.Text}. " +
                     $"{WaypointCount.Text} waypoints, " +
                     $"{PoiCount.Text} points of interest, " +
                     $"{VistaCount.Text} vistas, " +
                     $"{HeroCount.Text} hero challenges, " +
                     $"{HeartCount.Text} hearts.";
        await _tts.SpeakAsync(speech);
    }

    private async void ReadNext_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _objectives.Count == 0) return;
        if (_mumble?.Read() is not { } data) return;
        var top5 = _objectives.Take(5).ToList();
        var speech = string.Join(" ... ", top5.Select((o, i) =>
            $"{i + 1}: {o.Name}, {o.Type}, {(int)o.Distance} units {CompassHelper.Direction(data.PlayerX, data.PlayerY, o.X, o.Y)}"));
        await _tts.SpeakAsync(speech);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_gw2Api == null || _lastMapId <= 0) return;
        _gw2Api.InvalidateMap(_lastMapId);
        _lastMapId = -1;
        await PollAsync();
    }
}
