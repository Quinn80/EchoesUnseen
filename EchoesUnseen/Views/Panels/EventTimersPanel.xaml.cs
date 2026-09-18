using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Event Timers — an audio schedule board for GW2's fixed-clock world bosses and
/// meta events. Lists what's coming next with live countdowns; the actual alarm
/// (chime + announcement a few minutes out) runs in MetaEventService whether this
/// panel is open or not. Fully keyboard/NVDA accessible.
/// </summary>
public partial class EventTimersPanel : UserControl, IPanel
{
    private TtsService? _tts;
    private DispatcherTimer? _refresh;

    public EventTimersPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var s = App.Settings.Current;
            AlertsToggle.IsChecked = s.MetaAlertsEnabled;
            LeadSlider.Value = Math.Clamp(s.MetaLeadMinutes, 1, 15);
            LeadLabel.Text = $"{(int)LeadSlider.Value} minutes";

            RenderList();
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _refresh.Tick += (_, _) => RenderList();
            _refresh.Start();
        };
        Unloaded += (_, _) => { _refresh?.Stop(); _refresh = null; };
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
        => _tts = tts;

    private void RenderList()
    {
        EventsList.Children.Clear();
        var svc = MetaEventService.Shared;
        if (svc == null) { StatusText.Text = "Event schedule is still starting up."; return; }

        var up = svc.Upcoming(12);
        StatusText.Text = up.Count == 0 ? "No events scheduled." : $"{up.Count} upcoming.";

        foreach (var (name, map, sec) in up)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock
            {
                Text = name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            left.Children.Add(new TextBlock
            {
                Text = map, Foreground = (Brush)FindResource("MutedBrush"), FontSize = 12,
            });
            Grid.SetColumn(left, 0); grid.Children.Add(left);

            bool soon = sec <= 300;
            var countdown = new TextBlock
            {
                Text = "in " + MetaEventService.FmtSpan(sec),
                Foreground = soon ? (Brush)FindResource("PrimaryBrush") : Brushes.White,
                FontWeight = soon ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(countdown, 1); grid.Children.Add(countdown);

            System.Windows.Automation.AutomationProperties.SetName(grid,
                $"{name} in {map}, starting in {MetaEventService.FmtSpan(sec)}.");
            EventsList.Children.Add(grid);
        }
    }

    private void AnnounceNext_Click(object sender, RoutedEventArgs e) => MetaEventService.Shared?.AnnounceNext(3);

    private void Refresh_Click(object sender, RoutedEventArgs e) => RenderList();

    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        if (AlertsToggle == null) return;
        App.Settings.Current.MetaAlertsEnabled = AlertsToggle.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void Lead_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LeadLabel == null) return;
        int mins = (int)LeadSlider.Value;
        LeadLabel.Text = $"{mins} minutes";
        App.Settings.Current.MetaLeadMinutes = mins;
        App.Settings.NotifyChanged();
    }
}
