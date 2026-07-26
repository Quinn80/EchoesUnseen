using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Heart Quests (Renown Hearts) panel.
///
/// Shows every heart on the current map with:
///   - Name (e.g. "Assist the farmers in clearing the invaders")
///   - Distance from the player in game units
///   - Compass direction (N / NE / E / SE / S / SW / W / NW)
///   - Completion status from the user's account (if an API key is set)
///
/// DATA SOURCES:
///   1. MumbleLink → current map ID + player position
///   2. /continents/1/floors/1/regions/{r}/maps/{m} → list of tasks (hearts)
///   3. (optional) /account/achievements → which hearts the account has completed
///      [NOTE: GW2 hearts don't actually appear in the achievements API as you
///       might expect. There's no official "completed hearts" endpoint — that's
///       tracked internally in the map-completion system. For the MVP we show
///       distance and direction; completion tracking would require the player
///       to manually mark them. This is the honest limitation.]
///
/// REUSES:
///   Same polling + compass helper pattern as TrailNavigatorPanel. This panel
///   is a focused view filtered to just hearts, with a simpler UI.
/// </summary>
public partial class HeartQuestPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private DispatcherTimer? _poll;
    private int _lastMapId = -1;
    private MapObjectives? _currentMap;
    private List<HeartRow> _hearts = new();

    private record HeartRow(string Name, float X, float Y, float DistSq)
    {
        public float Distance => (float)Math.Sqrt(DistSq);
    }

    public HeartQuestPanel()
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
            HeartSummary.Text = "Launch GW2 and enter a map.";
            StatusText.Text = "";
            return;
        }

        if (data.MapId != _lastMapId)
        {
            _lastMapId = data.MapId;
            StatusText.Text = "Loading hearts...";
            _currentMap = await _gw2Api.GetMapObjectivesAsync(data.MapId);
            if (_currentMap != null)
            {
                MapName.Text = _currentMap.MapName;
            }
            else
            {
                MapName.Text = $"Map {data.MapId}";
                HeartSummary.Text = "Could not load map data.";
                StatusText.Text = "";
                return;
            }
        }

        if (_currentMap?.Tasks == null || _currentMap.Tasks.Count == 0)
        {
            HeartSummary.Text = "This map has no renown hearts.";
            HeartsList.Children.Clear();
            StatusText.Text = "";
            return;
        }

        // Build rows with current distances
        _hearts = _currentMap.Tasks.Values.Select(t =>
        {
            var dx = t.X - data.PlayerX;
            var dy = t.Y - data.PlayerY;
            return new HeartRow(t.Objective ?? "Unknown heart", t.X, t.Y, dx * dx + dy * dy);
        })
        .OrderBy(h => h.DistSq)
        .ToList();

        HeartSummary.Text = $"{_hearts.Count} heart{(_hearts.Count == 1 ? "" : "s")} on this map.";
        StatusText.Text = "";
        RenderHearts(data.PlayerX, data.PlayerY);
    }

    private void RenderHearts(float px, float py)
    {
        HeartsList.Children.Clear();
        foreach (var h in _hearts)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF8, 0x71, 0x71)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF8, 0x71, 0x71)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

            var icon = new TextBlock
            {
                Text = "❤",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var name = new TextBlock
            {
                Text = h.Name,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var dist = new TextBlock
            {
                Text = $"{(int)h.Distance} u",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            Grid.SetColumn(dist, 2);
            grid.Children.Add(dist);

            var compass = new TextBlock
            {
                Text = CompassHelper.Direction(px, py, h.X, h.Y),
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
            };
            Grid.SetColumn(compass, 3);
            grid.Children.Add(compass);

            row.Child = grid;
            HeartsList.Children.Add(row);
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────
    private async void ReadAll_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _hearts.Count == 0) return;
        if (_mumble?.Read() is not { } data) return;
        var speech = $"{_hearts.Count} hearts on this map. ";
        speech += string.Join(" ... ", _hearts.Select((h, i) =>
            $"{i + 1}: {h.Name}, {(int)h.Distance} units {CompassHelper.Direction(data.PlayerX, data.PlayerY, h.X, h.Y)}"));
        await _tts.SpeakAsync(speech);
    }

    private async void ReadNearest_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _hearts.Count == 0) return;
        if (_mumble?.Read() is not { } data) return;
        var h = _hearts[0]; // sorted by distance
        var speech = $"Nearest heart: {h.Name}. {(int)h.Distance} units {CompassHelper.Direction(data.PlayerX, data.PlayerY, h.X, h.Y)}.";
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

// ─────────────────────────────────────────────────────────────────────────────
// Shared compass helper (used by HeartQuestPanel + MapCompletionPanel + etc.)
// Duplicated from TrailNavigatorPanel here for clarity; could be extracted
// to a shared helper if more panels need it.
// ─────────────────────────────────────────────────────────────────────────────
internal static class CompassHelper
{
    public static string Direction(float px, float py, float tx, float ty)
    {
        var dx = tx - px;
        var dy = ty - py;
        var angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (angle < 0) angle += 360;
        var fromNorth = (90 - angle + 360) % 360;
        string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        int idx = (int)Math.Round(fromNorth / 45.0) % 8;
        return dirs[idx];
    }
}
