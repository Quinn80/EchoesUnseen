using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Trading Post — look up current market prices for any item.
///
/// DATA FLOW:
///   1. User enters an item name or numeric item ID
///   2. If numeric, fetch prices directly
///   3. If text, we'd ideally use /items search — but the GW2 API doesn't
///      have a true name search endpoint. For the MVP we accept item ID
///      only (users can find IDs from wiki.guildwars2.com URLs) and defer
///      name search to a later pass that'd require an offline item index.
///
/// PRICE FORMAT:
///   API returns prices in copper. We split into gold/silver/copper for
///   display (100 copper = 1 silver, 100 silver = 1 gold).
/// </summary>
public partial class TradingPostPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    // Keep HttpClient for the simple /commerce/prices call
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private long _lastBuyCopper;
    private long _lastSellCopper;
    private string _lastItemName = "";

    public TradingPostPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadWalletAsync();
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    private async Task LoadWalletAsync()
    {
        if (_gw2Api == null) return;
        if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey)) return;

        try
        {
            var wallet = await _gw2Api.GetWalletAsync();
            if (wallet == null) return;
            // Currency ID 1 = Coin (copper)
            var coin = wallet.FirstOrDefault(c => c.Id == 1);
            if (coin != null) SetCoins(coin.Value);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("TradingPostPanel.LoadWalletAsync", ex);
        }
    }

    private void SetCoins(long copperTotal)
    {
        long gold = copperTotal / 10000;
        long silver = (copperTotal / 100) % 100;
        long copper = copperTotal % 100;
        Gold.Text = gold.ToString("N0");
        Silver.Text = silver.ToString();
        Copper.Text = copper.ToString();
    }

    // ── Price lookup ─────────────────────────────────────────────────────────
    private void ItemSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Search_Click(sender, e); e.Handled = true; }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var input = ItemSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(input)) return;

        if (!int.TryParse(input, out var itemId))
        {
            StatusText.Text = "Enter a numeric item ID. (Find it in the wiki URL: wiki.guildwars2.com/wiki/...?id=NNNN)";
            return;
        }

        StatusText.Text = $"Looking up item {itemId}...";
        DetailCard.Visibility = Visibility.Collapsed;

        try
        {
            // Fetch item info + prices in parallel
            var itemTask = _gw2Api?.GetItemAsync(itemId);
            var pricesTask = _http.GetStringAsync($"https://api.guildwars2.com/v2/commerce/prices/{itemId}");
            await Task.WhenAll(itemTask ?? Task.FromResult<Item?>(null), pricesTask);

            var item = await (itemTask ?? Task.FromResult<Item?>(null));
            var pricesJson = await pricesTask;
            using var doc = JsonDocument.Parse(pricesJson);
            long buy = doc.RootElement.TryGetProperty("buys", out var b) && b.TryGetProperty("unit_price", out var bp) ? bp.GetInt64() : 0;
            long sell = doc.RootElement.TryGetProperty("sells", out var s) && s.TryGetProperty("unit_price", out var sp) ? sp.GetInt64() : 0;

            _lastBuyCopper = buy;
            _lastSellCopper = sell;
            _lastItemName = item?.Name ?? $"Item #{itemId}";

            ItemName.Text = _lastItemName;
            ItemRarity.Text = item?.Rarity ?? "";
            ItemRarity.Foreground = new SolidColorBrush(RarityColor(item?.Rarity));
            BuyPrice.Text = FormatCoin(buy);
            SellPrice.Text = FormatCoin(sell);
            DetailCard.Visibility = Visibility.Visible;
            StatusText.Text = "";
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            StatusText.Text = $"Item {itemId} is not tradeable or doesn't exist on the Trading Post.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            CrashLogger.Log("TradingPostPanel.Search_Click", ex);
        }
    }

    private static string FormatCoin(long copper)
    {
        if (copper <= 0) return "—";
        long g = copper / 10000;
        long s = (copper / 100) % 100;
        long c = copper % 100;
        var parts = new List<string>();
        if (g > 0) parts.Add($"{g}g");
        if (s > 0 || g > 0) parts.Add($"{s}s");
        parts.Add($"{c}c");
        return string.Join(" ", parts);
    }

    private static Color RarityColor(string? rarity) => rarity switch
    {
        "Junk"       => Color.FromRgb(0xA0, 0xA0, 0xA0),
        "Basic"      => Color.FromRgb(0xFF, 0xFF, 0xFF),
        "Fine"       => Color.FromRgb(0x62, 0xA4, 0xDA),
        "Masterwork" => Color.FromRgb(0x1A, 0x93, 0x06),
        "Rare"       => Color.FromRgb(0xFC, 0xD0, 0x0B),
        "Exotic"     => Color.FromRgb(0xFF, 0xA4, 0x05),
        "Ascended"   => Color.FromRgb(0xFB, 0x3E, 0x8D),
        "Legendary"  => Color.FromRgb(0x4C, 0x13, 0x9D),
        _            => Color.FromRgb(0xB8, 0xB8, 0xC8),
    };

    // ── TTS actions ──────────────────────────────────────────────────────────
    private async void ReadPrice_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null) return;
        var speech = $"{_lastItemName}. Buy price {FormatCoinSpoken(_lastBuyCopper)}. Sell price {FormatCoinSpoken(_lastSellCopper)}.";
        await _tts.SpeakAsync(speech);
    }

    private async void ReadWallet_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null) return;
        var g = Gold.Text;
        var s = Silver.Text;
        var c = Copper.Text;
        await _tts.SpeakAsync($"Wallet: {g} gold, {s} silver, {c} copper.");
    }

    private static string FormatCoinSpoken(long copper)
    {
        if (copper <= 0) return "no listings";
        long g = copper / 10000;
        long s = (copper / 100) % 100;
        long c = copper % 100;
        var parts = new List<string>();
        if (g > 0) parts.Add($"{g} gold");
        if (s > 0) parts.Add($"{s} silver");
        if (c > 0 || (g == 0 && s == 0)) parts.Add($"{c} copper");
        return string.Join(", ", parts);
    }
}
