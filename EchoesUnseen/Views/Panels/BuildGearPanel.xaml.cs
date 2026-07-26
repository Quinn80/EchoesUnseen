using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Build &amp; Gear panel — the GOLD-STANDARD reference for every API-only panel.
///
/// This is the simplest API panel in the whole app and exists primarily to
/// validate the pipeline: API key → HttpClient → JSON deserialization →
/// item ID resolution → UI render → TTS read-aloud. If this works end-to-end,
/// every other API panel (Account Search, Trading Post, Heart Quests, Map
/// Completion) follows the same pattern.
///
/// DATA FLOW:
///   1. On load: GET /characters → populate character dropdown
///   2. On character selection: GET /characters/{name} → get equipment array
///   3. Collect all item IDs from the equipment → GET /items?ids=...
///   4. Render each equipment slot with its resolved item name and rarity
///   5. On "Read Aloud": dictate the build as spoken sentences
///
/// ERROR HANDLING:
///   - No API key → friendly message directing to Settings
///   - Network error → friendly message + error details in crash log
///   - Character has no equipment (e.g. bank character) → friendly empty state
/// </summary>
public partial class BuildGearPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private Character? _currentCharacter;

    public BuildGearPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_gw2Api == null)
        {
            StatusText.Text = "API service not available.";
            return;
        }
        if (string.IsNullOrWhiteSpace(App.Settings.Current.Gw2ApiKey))
        {
            StatusText.Text = "⚠ No GW2 API key configured. Open Settings > API Keys to add one.";
            return;
        }

        await LoadCharactersAsync();
    }

    /// <summary>Populate the character dropdown from /characters.</summary>
    private async System.Threading.Tasks.Task LoadCharactersAsync()
    {
        StatusText.Text = "Loading characters...";
        CharacterCombo.Items.Clear();
        try
        {
            var names = await _gw2Api!.GetCharactersAsync();
            if (names == null || names.Count == 0)
            {
                StatusText.Text = "No characters returned. Verify your API key has the 'characters' scope.";
                return;
            }
            foreach (var n in names) CharacterCombo.Items.Add(n);
            CharacterCombo.SelectedIndex = 0; // triggers SelectionChanged
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading characters: {ex.Message}";
            CrashLogger.Log("BuildGearPanel LoadCharacters", ex);
        }
    }

    private async void CharacterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CharacterCombo.SelectedItem is string name)
            await LoadCharacterDetailsAsync(name);
    }

    /// <summary>Fetch equipment + stats for the selected character and render the list.</summary>
    private async System.Threading.Tasks.Task LoadCharacterDetailsAsync(string name)
    {
        if (_gw2Api == null) return;
        StatusText.Text = $"Loading {name}...";
        EquipmentList.Children.Clear();

        try
        {
            var c = await _gw2Api.GetCharacterAsync(name);
            if (c == null)
            {
                StatusText.Text = $"Could not load {name}.";
                return;
            }
            _currentCharacter = c;

            if (c.Equipment == null || c.Equipment.Count == 0)
            {
                StatusText.Text = $"{c.Name} has no equipped items.";
                return;
            }

            StatusText.Text = $"{c.Name} · Level {c.Level} {c.Race} {c.Profession}";

            // Resolve all item IDs in one batch call (fast, cached)
            var ids = c.Equipment.Where(eq => eq.Id > 0).Select(eq => eq.Id).Distinct().ToList();
            var items = await _gw2Api.GetItemsAsync(ids);
            var itemMap = items.ToDictionary(i => i.Id);

            foreach (var eq in c.Equipment)
            {
                if (!itemMap.TryGetValue(eq.Id, out var item)) continue;
                EquipmentList.Children.Add(BuildEquipmentRow(eq, item));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading character: {ex.Message}";
            CrashLogger.Log("BuildGearPanel LoadCharacterDetails", ex);
        }
    }

    /// <summary>
    /// Build a single row showing one piece of equipment: slot name, item name,
    /// and a rarity-colored pip on the left.
    /// </summary>
    private static Border BuildEquipmentRow(Equipment eq, Item item)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x1A, 0x8A)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Rarity pip (color-coded bar on the left)
        var rarityColor = RarityColor(item.Rarity);
        var pip = new Rectangle { Fill = new SolidColorBrush(rarityColor) };
        Grid.SetColumn(pip, 0);
        grid.Children.Add(pip);

        // Slot name (muted)
        var slot = new TextBlock
        {
            Text = eq.Slot ?? "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC8)),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(slot, 1);
        grid.Children.Add(slot);

        // Item name (bright, main text)
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameText = new TextBlock
        {
            Text = item.Name ?? "(unknown)",
            Foreground = new SolidColorBrush(rarityColor),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
        };
        stack.Children.Add(nameText);
        if (!string.IsNullOrWhiteSpace(item.Rarity))
        {
            stack.Children.Add(new TextBlock
            {
                Text = item.Rarity,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC8)),
                FontSize = 11,
            });
        }
        Grid.SetColumn(stack, 2);
        grid.Children.Add(stack);

        row.Child = grid;
        return row;
    }

    /// <summary>Map GW2 rarity string to a UI color.</summary>
    private static Color RarityColor(string? rarity) => rarity switch
    {
        "Junk"      => Color.FromRgb(0xA0, 0xA0, 0xA0),
        "Basic"     => Color.FromRgb(0xFF, 0xFF, 0xFF),
        "Fine"      => Color.FromRgb(0x62, 0xA4, 0xDA),
        "Masterwork"=> Color.FromRgb(0x1A, 0x93, 0x06),
        "Rare"      => Color.FromRgb(0xFC, 0xD0, 0x0B),
        "Exotic"    => Color.FromRgb(0xFF, 0xA4, 0x05),
        "Ascended"  => Color.FromRgb(0xFB, 0x3E, 0x8D),
        "Legendary" => Color.FromRgb(0x4C, 0x13, 0x9D),
        _           => Color.FromRgb(0xFF, 0xFF, 0xFF),
    };

    // ── Actions ──────────────────────────────────────────────────────────────
    private async void ReadAloud_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _currentCharacter == null) return;
        var c = _currentCharacter;
        var speech = $"Build for {c.Name}, level {c.Level} {c.Race} {c.Profession}. Equipment: ";
        if (c.Equipment != null)
        {
            var names = c.Equipment
                .Where(eq => eq.Id > 0)
                .Select(eq => eq.Slot)
                .Where(s => !string.IsNullOrWhiteSpace(s));
            speech += string.Join(", ", names) + ".";
        }
        await _tts.SpeakAsync(speech);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadCharactersAsync();
    }
}
