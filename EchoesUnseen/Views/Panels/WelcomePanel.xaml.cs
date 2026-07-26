using System.Windows;
using System.Windows.Controls;
using EchoesUnseen.Models;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// A short first-run onboarding card: the handful of controls a new user needs,
/// spoken aloud automatically, plus a note that everything is rebindable.
///
/// It's built from the SAME keybind data the app actually registers, so it can
/// never drift out of date with the real shortcuts — if a bind changes, this
/// screen changes with it.
///
/// Shown once on first launch (MainWindow), and reopenable any time from
/// Settings → About.
/// </summary>
public partial class WelcomePanel : UserControl, IPanel
{
    private TtsService? _tts;

    public string PanelId => "welcome";
    public string PanelTitle => "Welcome";

    /// <summary>Raised when the user presses "Got it".</summary>
    public event EventHandler? Dismissed;

    public WelcomePanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts,
        GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api) => _tts = tts;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildControlsList();
        // Speak it automatically — a welcome screen a blind user has to find
        // and trigger isn't a welcome.
        _ = _tts?.SpeakAsync(SpokenScript());
    }

    /// <summary>The starter controls, drawn from the live keybinds.</summary>
    private static (string Keys, string What)[] Basics()
    {
        var kb = App.Settings.Current.Keybinds;
        return new[]
        {
            ("Alt + Arrow keys",        "Move around the wheel. I'll name each tool as you land on it."),
            (Pretty(kb.OpenSelected),   "Open the tool you've landed on."),
            ("Escape",                  "Close the open tool."),
            (Pretty(kb.StopSpeaking),   "Stop me talking, any time."),
            ("Ctrl + Shift + Arrows",   "Move the wheel itself around the screen."),
            (Pretty(kb.RecenterHud),    "Bring the wheel back to the middle if it gets lost."),
            (Pretty(kb.Quit),           "Close Echoes Unseen."),
        };
    }

    private static string Pretty(string spec) => spec.Replace("+", " + ");

    private void BuildControlsList()
    {
        ControlsList.Children.Clear();
        foreach (var (keys, what) in Basics())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var k = new TextBlock
            {
                Text = keys,
                FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)FindResource("PrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(k, 0);
            row.Children.Add(k);

            var v = new TextBlock
            {
                Text = what,
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);

            // Screen readers get the pair as one sentence rather than two columns.
            System.Windows.Automation.AutomationProperties.SetName(row, $"{keys}: {what}");
            ControlsList.Children.Add(row);
        }
    }

    private static string SpokenScript()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Welcome, Commander. Here are the controls you need to start. ");
        foreach (var (keys, what) in Basics())
            sb.Append($"{keys.Replace("Ctrl", "Control")}. {what} ");
        sb.Append("You can change any of these in Settings, under Keybinds. ");
        sb.Append("Press Escape, or the Got it button, to close this and begin.");
        return sb.ToString();
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        _tts?.StopSpeaking();
        _ = _tts?.SpeakAsync(SpokenScript());
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _tts?.StopSpeaking();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
