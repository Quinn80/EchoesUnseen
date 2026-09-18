using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// The Wizard's Vault, read from the game's own API rather than off the screen.
///
/// Quinn asked for this the honest way round: "hover is not reading items in the vault
/// where I collect astral acclaim and trade for items, I would if I could read what they
/// were." The hover reader can only work with what is drawn, and the Vault draws its
/// names in a stylised face over an animated background — a *correct* read of "Tropical
/// Leaf Cape Set" still came back at 42% confidence.
///
/// So this does not read the screen at all. It asks Guild Wars 2 what the rewards are and
/// gets exact names, exact prices, and how much Astral Acclaim is actually on the account.
/// </summary>
public partial class WizardsVaultPanel : UserControl, IPanel
{
    private TtsService? _tts;
    private Gw2ApiService? _api;

    private readonly WizardsVaultService _vault = new();
    private WizardsVaultService.Season? _season;
    private int? _balance;

    public WizardsVaultPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts,
                               GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _tts = tts;
        _api = gw2Api;
    }

    private void Say(string s) => _tts?.SpeakAsync(s);

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading the Wizard's Vault…";

        _season = await _vault.GetSeasonAsync();
        if (_season is null)
        {
            StatusText.Text = "Couldn't reach the Guild Wars 2 API. Check your connection and press Refresh.";
            Say("I couldn't reach the Guild Wars 2 servers.");
            return;
        }

        // The balance needs an API key; the rewards themselves don't, so a missing key
        // costs you the "what can I afford" figure and nothing else.
        _balance = _api != null ? await WizardsVaultService.GetBalanceAsync(_api) : null;

        var ends = _season.Ends is { } e
            ? $" Season ends in {Math.Max(0, (e - DateTime.UtcNow).Days)} days."
            : "";
        BalanceText.Text = _balance is { } bal
            ? $"Astral Acclaim: {bal:N0}"
            : "Astral Acclaim: add an API key in Settings";
        System.Windows.Automation.AutomationProperties.SetName(BalanceText, BalanceText.Text);

        StatusText.Text = $"{_season.Title}. {_season.Rewards.Count} rewards.{ends}";

        Build();
    }

    private void Build()
    {
        RewardList.Children.Clear();
        if (_season is null) return;

        var filter = Filter?.Text?.Trim() ?? "";
        var shown = _season.Rewards
            .Where(r => filter.Length == 0 ||
                        r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (shown.Count == 0)
        {
            RewardList.Children.Add(new TextBlock
            {
                Text = "Nothing matches that.",
                Foreground = Brushes.White, FontSize = 16,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        string lastSection = "";
        foreach (var r in shown.OrderBy(x => SectionOrder(x.Type)).ThenBy(x => x.Cost))
        {
            // A heading per section, so the list reads in the same order as the tabs in
            // the game rather than as ninety-one items in a row.
            if (r.Section != lastSection)
            {
                lastSection = r.Section;
                int n = shown.Count(x => x.Section == lastSection);
                var head = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x00)),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 14, 0, 8),
                    Child = new TextBlock
                    {
                        Text = $"{lastSection}  ({n})",
                        Foreground = Brushes.Black,
                        FontWeight = FontWeights.Bold, FontSize = 19,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                System.Windows.Automation.AutomationProperties.SetName(head, $"{lastSection}, {n} rewards");
                RewardList.Children.Add(head);
            }

            bool affordable = _balance is { } b && b >= r.Cost;

            var btn = new Button
            {
                Content = new TextBlock
                {
                    // The price sits in the label rather than a separate column, because
                    // a screen reader reads a row as one string either way.
                    Text = $"{(affordable ? "✔ " : "")}{r.Name}   —   {r.Cost} Astral Acclaim"
                           + (r.Count > 1 ? $"   (×{r.Count})" : ""),
                    Foreground = affordable ? Brushes.Black : Brushes.White,
                    FontSize = 17, FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                },
                // Affordable rewards get a green plate — the same signal the running
                // guide uses, and never colour on its own: there is a tick in the text
                // and the screen-reader name says so outright.
                Background = new SolidColorBrush(affordable
                    ? Color.FromRgb(0x22, 0xC5, 0x5E)
                    : Color.FromRgb(0x0A, 0x0A, 0x12)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            var afford = _balance is { } bal
                ? (affordable ? " You can afford this." : $" You need {r.Cost - bal} more.")
                : "";
            System.Windows.Automation.AutomationProperties.SetName(btn,
                $"{r.Spoken}. In {r.Section}.{afford} Select to hear what it is.");

            var reward = r;
            btn.Click += (_, _) => Say(
                reward.Spoken
                + $". You'll find it under {reward.Section}."
                + (_balance is { } bb
                    ? (bb >= reward.Cost ? " You can afford it." : $" You need {reward.Cost - bb} more.")
                    : "")
                + (string.IsNullOrWhiteSpace(reward.Description) ? "" : " " + reward.Description));

            RewardList.Children.Add(btn);
        }
    }

    /// <summary>Sections in the order the game shows its tabs, not alphabetically.</summary>
    private static int SectionOrder(string type) => type switch
    {
        "Featured" => 0,
        "Normal"   => 1,
        "Legacy"   => 2,
        _          => 3,
    };

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Build();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Say("Reloading the vault.");
        await LoadAsync();
    }

    /// <summary>Read out what the current balance actually covers — the question worth
    /// asking when you open the Vault.</summary>
    private void ReadAffordable_Click(object sender, RoutedEventArgs e)
    {
        if (_season is null) { Say("The vault hasn't loaded yet."); return; }
        if (_balance is not { } bal)
        {
            Say("I don't know your Astral Acclaim. Add an API key in Settings, under API Keys.");
            return;
        }

        var can = _season.Rewards.Where(r => r.Cost <= bal).OrderByDescending(r => r.Cost).ToList();
        if (can.Count == 0)
        {
            var cheapest = _season.Rewards.OrderBy(r => r.Cost).FirstOrDefault();
            Say(cheapest is null
                ? $"You have {bal} Astral Acclaim."
                : $"You have {bal} Astral Acclaim. Nothing is in reach yet — the cheapest is {cheapest.Spoken}.");
            return;
        }

        // The dearest few first: with a big balance the affordable list is most of the
        // vault, and reading all of it is worse than reading none.
        var top = can.Take(6).Select(r => $"{r.Spoken}, in {r.Section}");
        Say($"You have {bal} Astral Acclaim. {can.Count} rewards are in reach. The dearest are: "
            + string.Join(". ", top) + ".");
    }
}
