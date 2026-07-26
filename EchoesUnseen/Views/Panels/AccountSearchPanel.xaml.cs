using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Account Search — searches bank, material storage, shared inventory,
/// character inventories, and wallet for any item by name.
///
/// WHY WE FETCH-ONCE-AND-FILTER:
///   These endpoints are cheap and stable — your bank doesn't change 10
///   times a second. Fetching all the data at panel open and filtering
///   client-side gives us instant search-as-you-type without hammering
///   the API, and handles offline cases gracefully.
///
/// SCOPES REQUIRED on the API key:
///   account, inventories, wallet, characters (for character bags)
///
/// LIMITATIONS:
///   This is the MVP version. For the full experience we'd also scan each
///   character's inventory. For the beta we focus on bank + materials which
///   is where 90% of "where's my X?" questions resolve.
/// </summary>
public partial class AccountSearchPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private record AccountItem(string Name, string Rarity, int Count, string Location);
    private List<AccountItem> _all = new();
    private List<AccountItem> _filtered = new();

    public AccountSearchPanel()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    private async Task LoadAsync()
    {
        if (_gw2Api == null) { StatusText.Text = "API service not available."; return; }
        if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey))
        {
            StatusText.Text = "⚠ No GW2 API key configured. Open Settings > API Keys to add one.";
            return;
        }

        StatusText.Text = "Loading account data...";
        _all = new List<AccountItem>();

        try
        {
            // Every location below is POSITIONAL in the API — the bank is a flat
            // array of 30-slot tabs, bags list their slots in order — so we can
            // report exactly where something sits rather than just that you own
            // it. That's the difference between "you have 47 Mystic Coins" and
            // "they're in bank tab 2, slot 15."
            var ids = new HashSet<int>();

            var bank = await _gw2Api.GetBankAsync();
            var materials = await _gw2Api.GetMaterialsAsync();
            var categories = await _gw2Api.GetMaterialCategoriesAsync();

            if (bank != null)
                foreach (var s in bank) if (s != null) ids.Add(s.Id);
            if (materials != null)
                foreach (var m in materials) if (m.Count > 0) ids.Add(m.Id);

            // Character bags — one call per character, so cap it to keep the
            // load quick and stay well inside the API's rate limits.
            var characters = await _gw2Api.GetCharactersAsync() ?? new List<string>();
            var inventories = new List<(string Character, CharacterInventory Inv)>();
            foreach (var name in characters.Take(8))
            {
                var inv = await _gw2Api.GetCharacterInventoryAsync(name);
                if (inv?.Bags == null) continue;
                inventories.Add((name, inv));
                foreach (var bag in inv.Bags)
                    if (bag?.Inventory != null)
                        foreach (var slot in bag.Inventory) if (slot != null) ids.Add(slot.Id);
            }

            // One batched lookup resolves every id to a real name and description.
            var lookup = (await _gw2Api.GetItemsAsync(ids)).ToDictionary(i => i.Id);
            string NameOf(int id) => lookup.TryGetValue(id, out var it) ? (it.Name ?? $"Item #{id}") : $"Item #{id}";
            string RarityOf(int id) => lookup.TryGetValue(id, out var it) ? (it.Rarity ?? "") : "";

            // ── Bank: 30 slots per tab ──
            if (bank != null)
            {
                for (int i = 0; i < bank.Count; i++)
                {
                    var s = bank[i];
                    if (s == null) continue;
                    _all.Add(new AccountItem(NameOf(s.Id), RarityOf(s.Id), s.Count,
                        $"Bank — tab {i / 30 + 1}, slot {i % 30 + 1}"));
                }
            }

            // ── Material storage: grouped by category ──
            if (materials != null)
            {
                var catNames = categories?.ToDictionary(c => c.Id, c => c.Name ?? "Materials")
                               ?? new Dictionary<int, string>();
                foreach (var m in materials)
                {
                    if (m.Count <= 0) continue;
                    var cat = catNames.TryGetValue(m.Category, out var cn) ? cn : "Materials";
                    _all.Add(new AccountItem(NameOf(m.Id), RarityOf(m.Id), m.Count,
                        $"Material Storage — {cat}"));
                }
            }

            // ── Character bags ──
            foreach (var (character, inv) in inventories)
            {
                var bags = inv.Bags!;
                for (int b = 0; b < bags.Count; b++)
                {
                    var bag = bags[b];
                    if (bag?.Inventory == null) continue;
                    for (int s = 0; s < bag.Inventory.Count; s++)
                    {
                        var slot = bag.Inventory[s];
                        if (slot == null) continue;
                        _all.Add(new AccountItem(NameOf(slot.Id), RarityOf(slot.Id), slot.Count,
                            $"{character} — bag {b + 1}, slot {s + 1}"));
                    }
                }
            }

            // ── Wallet last, so items rank above currencies when searching ──
            var wallet = await _gw2Api.GetWalletAsync();
            if (wallet != null)
                foreach (var c in wallet)
                    if (c.Value > 0)
                        _all.Add(new AccountItem($"Currency #{c.Id}", "Wallet",
                            (int)Math.Min(c.Value, int.MaxValue), "Wallet"));

            StatusText.Text = $"Loaded {_all.Count} entries. Type to search.";
            _tts?.SpeakAsync($"Loaded {_all.Count} items from your bank, bags and material storage. Type or dictate an item name.");
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading account: {ex.Message}";
            CrashLogger.Log("AccountSearchPanel.LoadAsync", ex);
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q))
        {
            _filtered = _all.Take(50).ToList();
        }
        else
        {
            _filtered = _all
                .Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(100)
                .ToList();
        }

        ResultCount.Text = $"{_filtered.Count} result{(_filtered.Count == 1 ? "" : "s")}";
        RenderResults();
    }

    private void RenderResults()
    {
        ResultsList.Children.Clear();
        foreach (var item in _filtered)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x1A, 0x8A)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 4),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var name = new TextBlock
            {
                Text = item.Name,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var count = new TextBlock
            {
                Text = $"×{item.Count}",
                Foreground = (Brush)FindResource("PrimaryBrush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(count, 1);
            grid.Children.Add(count);

            var loc = new TextBlock
            {
                Text = item.Location,
                Foreground = (Brush)FindResource("MutedBrush"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            Grid.SetColumn(loc, 2);
            grid.Children.Add(loc);

            row.Child = grid;

            // Make every row a real focus target that speaks directions. Knowing
            // you own something isn't the hard part when you can't see the grid —
            // getting to it is. So each row reads out WHERE it is and how to make
            // the game surface it.
            row.Focusable = true;
            row.Cursor = System.Windows.Input.Cursors.Hand;
            var directions = Directions(item);
            System.Windows.Automation.AutomationProperties.SetName(row, directions);
            row.MouseLeftButtonUp += (_, _) => _tts?.SpeakAsync(directions);
            row.GotKeyboardFocus += (_, _) => _tts?.SpeakAsync(directions);
            row.KeyDown += (s2, e2) =>
            {
                if (e2.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
                {
                    _tts?.SpeakAsync(directions);
                    e2.Handled = true;
                }
            };

            ResultsList.Children.Add(row);
        }
    }

    /// <summary>
    /// Turn a result into something you can act on without seeing the screen:
    /// what it is, how many, exactly where it sits, and the fastest way to make
    /// the game show it to you.
    ///
    /// The search-box tip is the important half. Guild Wars 2's bank, inventory
    /// and material storage each have a filter field — typing the exact name
    /// collapses hundreds of slots down to the one item, in the first position.
    /// That turns "hunt across a grid" into "look at one known spot", which is
    /// the difference between usable and not when your vision is limited.
    /// </summary>
    private static string Directions(AccountItem item)
    {
        var where = item.Location;
        string how;

        if (where.StartsWith("Bank", StringComparison.OrdinalIgnoreCase))
            how = $"Open your bank and type {item.Name} into its search box — it will be the only item left, in the first slot.";
        else if (where.StartsWith("Material Storage", StringComparison.OrdinalIgnoreCase))
            how = $"Open material storage and type {item.Name} into the search box to isolate it.";
        else if (where.Equals("Wallet", StringComparison.OrdinalIgnoreCase))
            how = "This is a currency, found on the Wallet tab of your inventory.";
        else
            how = $"Open your inventory and type {item.Name} into the search box — it will be the only item shown.";

        var rarity = string.IsNullOrWhiteSpace(item.Rarity) || item.Rarity == "Wallet"
            ? "" : $"{item.Rarity}. ";

        return $"{item.Name}. {rarity}Quantity {item.Count}. Located in {where}. {how}";
    }

    // ── Actions ──────────────────────────────────────────────────────────────
    private void SearchBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => ApplyFilter();

    private async void ReadResults_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null) return;
        if (_filtered.Count == 0) { await _tts.SpeakAsync("No results."); return; }

        // A single match is the common case after typing a name — so read the
        // full directions straight away instead of making the user drill in.
        if (_filtered.Count == 1)
        {
            await _tts.SpeakAsync(Directions(_filtered[0]));
            return;
        }

        var speech = $"{_filtered.Count} results. " +
            string.Join(". ", _filtered.Take(6).Select(r => $"{r.Name}, {r.Count}, in {r.Location}")) +
            ". Use Tab to step through them and hear how to find each one.";
        await _tts.SpeakAsync(speech);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}
