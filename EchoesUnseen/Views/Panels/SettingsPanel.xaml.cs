using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using EchoesUnseen.Models;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Settings panel. Tab-based configuration for Voice, API Keys, HUD, and About.
///
/// BINDING STRATEGY:
///   Rather than wire up full two-way XAML bindings (which would require an
///   explicit ViewModel with INotifyPropertyChanged for every field), we load
///   settings into the controls on Loaded, and write back to settings on each
///   change event. This is more code per field but is dead simple to debug
///   and adjust — useful while the settings surface is still stabilizing.
///
///   App.Settings.NotifyChanged() is called after every write so the debounced
///   save kicks in and other services (e.g. the HUD) can react to changes.
///
/// MUMBLELINK LIVE STATUS:
///   A 1-second DispatcherTimer polls the reader and updates the About tab so
///   the user can verify GW2 detection without having to open another panel.
/// </summary>
public partial class SettingsPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;
    private DispatcherTimer? _mumbleTimer;
    private bool _loaded;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    // ── Loading current settings into UI ─────────────────────────────────────
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await PopulateFromSettingsAsync();

        // Start MumbleLink status timer (once)
        _mumbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mumbleTimer.Tick += (_, _) => UpdateMumbleStatus();
        _mumbleTimer.Start();
        UpdateMumbleStatus();

        _loaded = true;
    }

    /// <summary>
    /// Fill every control from the current settings. Extracted so the Reset
    /// button can rebuild the whole panel after restoring defaults.
    /// </summary>
    private async System.Threading.Tasks.Task PopulateFromSettingsAsync()
    {
        _loaded = false;
        var s = App.Settings.Current;

        // Voice engine
        foreach (ComboBoxItem item in EngineCombo.Items)
            if ((string)item.Tag == s.VoiceEngine) { EngineCombo.SelectedItem = item; break; }
        if (EngineCombo.SelectedIndex < 0) EngineCombo.SelectedIndex = 0;

        // ElevenLabs is a paid cloud service — greyed out until the user has
        // actually supplied a key, so it can't be selected into a dead end.
        UpdateElevenLabsAvailability();

        // Speech-to-text engine + Whisper model
        foreach (ComboBoxItem item in SttEngineCombo.Items)
            if ((string)item.Tag == s.SttEngine) { SttEngineCombo.SelectedItem = item; break; }
        if (SttEngineCombo.SelectedIndex < 0) SttEngineCombo.SelectedIndex = 0;
        foreach (ComboBoxItem item in WhisperModelCombo.Items)
            if ((string)item.Tag == s.WhisperModel) { WhisperModelCombo.SelectedItem = item; break; }
        if (WhisperModelCombo.SelectedIndex < 0) WhisperModelCombo.SelectedIndex = 1; // base.en
        UpdateWhisperOptionsVisibility();

        // Voice list — populate ALL voices from ALL engines into one combo
        await LoadAllVoicesAsync();

        // Sliders
        SpeedSlider.Value = s.TtsSpeed;
        SpeedValue.Text = $"{s.TtsSpeed:0.0}x";
        VolumeSlider.Value = s.Volume;
        VolumeValue.Text = $"{(int)(s.Volume * 100)}%";

        // API keys
        Gw2KeyBox.Password = s.Gw2ApiKey;
        ElevenLabsKeyBox.Password = s.ElevenLabsApiKey;

        // HUD tab
        // Populate theme dropdown from the ThemeService catalog
        ThemeCombo.Items.Clear();
        foreach (var theme in ThemeService.BuiltInThemes)
            ThemeCombo.Items.Add(theme);

        // Select the user's current theme
        var currentTheme = ThemeService.GetById(s.ThemeId);
        ThemeCombo.SelectedItem = ThemeCombo.Items
            .Cast<Models.Theme>()
            .FirstOrDefault(t => t.Id == currentTheme.Id) ?? ThemeCombo.Items[0];
        ThemeDescription.Text = currentTheme.Description;

        foreach (ComboBoxItem item in AccessModeCombo.Items)
            if ((string)item.Tag == s.AccessMode) { AccessModeCombo.SelectedItem = item; break; }
        HudScaleSlider.Value = s.HudScale;
        FontSizeSlider.Value = s.FontSize;
        HighContrastCheck.IsChecked = s.HighContrast;
        EarconsCheck.IsChecked = s.PanelEarcons;
        AnnounceHudCheck.IsChecked = s.AnnounceHudActivation;
        BuildHudButtonsList(); // v21: HUD button visibility checkboxes
        BuildFeatureToggles();
        BuildKeybindsList();

        _loaded = true;
    }

    // ── Features tab ──────────────────────────────────────────────────────────
    /// <summary>
    /// One switch per optional behaviour, described in plain language. Built from
    /// a table rather than hand-written XAML so a new feature is one line here and
    /// automatically gets a label, help text, spoken confirmation and persistence.
    /// </summary>
    private void BuildFeatureToggles()
    {
        FeatureToggles.Children.Clear();
        var s = App.Settings.Current;

        (string Label, string Help, Func<bool> Get, Action<bool> Set)[] features =
        {
            ("Minimise the HUD to its logo",
             "When the mouse moves away, the wheel shrinks to just the centre logo and unfolds again when you hover it. Keyboard navigation still works while minimised.",
             () => s.MinimiseHud, v => s.MinimiseHud = v),

            ("Read what the mouse rests on",
             "Speaks buttons, labels and values inside panels as you hover them.",
             () => s.HoverToRead, v => s.HoverToRead = v),

            ("Panel open and close sounds",
             "Soft audio cues when a tool opens or closes.",
             () => s.PanelEarcons, v => s.PanelEarcons = v),

            ("Announce when the HUD becomes active",
             "Says \"HUD active\" when the pointer reaches the wheel.",
             () => s.AnnounceHudActivation, v => s.AnnounceHudActivation = v),

            ("High contrast",
             "White on black with heavier outlines, overriding the theme colours.",
             () => s.HighContrast, v => s.HighContrast = v),
        };

        foreach (var (label, help, get, set) in features)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Content = label,
                IsChecked = get(),
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 2),
            };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            System.Windows.Automation.AutomationProperties.SetHelpText(box, help);

            void Changed(bool on)
            {
                if (!_loaded) return;
                set(on);
                App.Settings.NotifyChanged();
                _tts?.SpeakAsync($"{label}: {(on ? "on" : "off")}.");
            }
            box.Checked   += (_, _) => Changed(true);
            box.Unchecked += (_, _) => Changed(false);

            FeatureToggles.Children.Add(box);
            FeatureToggles.Children.Add(new TextBlock
            {
                Text = help,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24, 0, 0, 12),
            });
        }
    }

    // ── Keybinds tab ──────────────────────────────────────────────────────────
    private KeyBindings.Entry? _capturing;

    private void BuildKeybindsList()
    {
        KeybindsList.Children.Clear();
        foreach (var entry in App.Settings.Current.Keybinds.Editable())
            KeybindsList.Children.Add(BuildKeybindRow(entry));

        FixedKeybindsList.Children.Clear();
        foreach (var (keys, action) in KeyBindings.FixedInfo)
        {
            var row = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            };
            row.Inlines.Add(new System.Windows.Documents.Run(keys + "  ") { FontWeight = FontWeights.Bold });
            row.Inlines.Add(new System.Windows.Documents.Run("— " + action));
            System.Windows.Automation.AutomationProperties.SetName(row, $"{keys}: {action}");
            FixedKeybindsList.Children.Add(row);
        }
    }

    private Grid BuildKeybindRow(KeyBindings.Entry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel();
        labels.Children.Add(new TextBlock
        {
            Text = entry.Label,
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        labels.Children.Add(new TextBlock
        {
            Text = entry.Description,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(labels, 0);
        row.Children.Add(labels);

        var current = new TextBlock
        {
            Text = SpeakableSpec(entry.Get()),
            Foreground = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(6, 0, 8, 0),
        };
        Grid.SetColumn(current, 1);
        row.Children.Add(current);

        var change = new Button
        {
            Content = "Change",
            Style = (Style)FindResource("HCButton"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(change,
            $"Change the shortcut for {entry.Label}. Currently {SpeakableSpec(entry.Get())}.");
        change.Click += (_, _) => BeginCapture(entry, change);
        Grid.SetColumn(change, 2);
        row.Children.Add(change);

        return row;
    }

    private void BeginCapture(KeyBindings.Entry entry, Button change)
    {
        _capturing = entry;
        change.Content = "Press keys…";
        Focusable = true;
        Focus();
        PreviewKeyDown -= OnKeybindCapture;
        PreviewKeyDown += OnKeybindCapture;
        _tts?.SpeakAsync($"Press the new shortcut for {entry.Label}. Press Escape to cancel.");
    }

    private void OnKeybindCapture(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capturing == null) return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // Ignore bare modifier presses — wait for the actual key.
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
            return;

        e.Handled = true;
        PreviewKeyDown -= OnKeybindCapture;
        var entry = _capturing;
        _capturing = null;

        if (key == System.Windows.Input.Key.Escape)
        {
            _tts?.SpeakAsync("Cancelled.");
            BuildKeybindsList();
            return;
        }

        var spec = BuildSpec(System.Windows.Input.Keyboard.Modifiers, key);
        if (spec == null)
        {
            _tts?.SpeakAsync("That key can't be used. Try a letter or function key with Control, Shift or Alt.");
            BuildKeybindsList();
            return;
        }

        entry.Set(spec);
        App.Settings.NotifyChanged();
        if (Window.GetWindow(this) is MainWindow mw) mw.RegisterHotkeys();
        BuildKeybindsList();
        SetSaveStatus($"{entry.Label} set to {SpeakableSpec(spec)}.");
        _tts?.SpeakAsync($"{entry.Label} is now {SpeakableSpec(spec)}.");
    }

    private void ResetKeybinds_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.Keybinds = new KeyBindings();
        App.Settings.NotifyChanged();
        if (Window.GetWindow(this) is MainWindow mw) mw.RegisterHotkeys();
        BuildKeybindsList();
        SetSaveStatus("Shortcuts reset to defaults.");
        _tts?.SpeakAsync("Shortcuts reset to defaults.");
    }

    /// <summary>Build a hotkey spec ("Ctrl+Shift+S") from WPF modifiers + key, or null if unsupported.</summary>
    private static string? BuildSpec(System.Windows.Input.ModifierKeys mods, System.Windows.Input.Key key)
    {
        string? main = KeyToken(key);
        if (main == null) return null;

        var parts = new List<string>();
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift))   parts.Add("Shift");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");

        // A modifier-less letter/digit would clash with typing/gameplay; require
        // at least one modifier for letters and digits (function/arrow/nav keys
        // are allowed on their own).
        var isPlain = (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                   || (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9);
        if (parts.Count == 0 && isPlain) return null;

        parts.Add(main);
        return string.Join("+", parts);
    }

    private static string? KeyToken(System.Windows.Input.Key key)
    {
        if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z) return key.ToString();
        if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9) return key.ToString()[1..];
        if (key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F12) return key.ToString();
        return key switch
        {
            System.Windows.Input.Key.Left => "Left",
            System.Windows.Input.Key.Right => "Right",
            System.Windows.Input.Key.Up => "Up",
            System.Windows.Input.Key.Down => "Down",
            System.Windows.Input.Key.Enter => "Enter",
            System.Windows.Input.Key.Space => "Space",
            System.Windows.Input.Key.Tab => "Tab",
            _ => null,
        };
    }

    /// <summary>Speak a spec more naturally, e.g. "Control Shift S".</summary>
    private static string SpeakableSpec(string spec) =>
        spec.Replace("Ctrl", "Control").Replace("+", " ");

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _mumbleTimer?.Stop();
        _mumbleTimer = null;
    }

    // ── Voice tab ────────────────────────────────────────────────────────────
    /// <summary>
    /// Load voices from EVERY engine into the single combined dropdown.
    /// Each item is labeled with its engine in parentheses (e.g. "Lessac (Piper)",
    /// "Rachel (ElevenLabs)", "Microsoft Zira Desktop (Windows)"). Picking a voice
    /// sets both <c>VoiceEngine</c> and <c>VoiceId</c> in one step.
    /// </summary>
    private async System.Threading.Tasks.Task LoadAllVoicesAsync()
    {
        // Populate the voice list for whatever engine is currently selected,
        // then build the Piper download/preview manager beneath it.
        var engine = (EngineCombo.SelectedItem as ComboBoxItem)?.Tag as string
                     ?? App.Settings.Current.VoiceEngine;
        await PopulateVoicesForEngineAsync(engine);
        BuildPiperManager();
        UpdatePiperManagerVisibility(engine);
    }

    /// <summary>Fill the Voice combo with just the voices belonging to one engine.</summary>
    private async Task PopulateVoicesForEngineAsync(string engine)
    {
        if (_tts == null) return;
        VoiceCombo.Items.Clear();
        try
        {
            var all = await _tts.GetAllVoicesAsync();
            var voices = all.Where(v => v.Engine == engine).OrderBy(v => v.Name).ToList();
            foreach (var v in voices) VoiceCombo.Items.Add(v);

            foreach (VoiceInfo v in VoiceCombo.Items)
                if (v.Id == App.Settings.Current.VoiceId) { VoiceCombo.SelectedItem = v; break; }
            if (VoiceCombo.SelectedIndex < 0 && VoiceCombo.Items.Count > 0)
                VoiceCombo.SelectedIndex = 0;

            SetVoiceStatus(voices.Count > 0
                ? $"{voices.Count} {engine} voice(s) available."
                : engine == "elevenlabs"
                    ? "No ElevenLabs voices — add your API key in the API Keys tab."
                    : "No voices installed yet.");
        }
        catch (Exception ex)
        {
            SetVoiceStatus($"Error loading voices: {ex.Message}");
            CrashLogger.Log("SettingsPanel PopulateVoices", ex);
        }
    }

    private async void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (EngineCombo.SelectedItem is ComboBoxItem item)
        {
            var engine = (string)item.Tag;
            App.Settings.Current.VoiceEngine = engine;
            App.Settings.NotifyChanged();
            await PopulateVoicesForEngineAsync(engine);
            BuildPiperManager();
            UpdatePiperManagerVisibility(engine);
        }
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (VoiceCombo.SelectedItem is VoiceInfo v)
        {
            // Engine is fixed by the engine combo now, so a voice choice only
            // sets the voice — it can never flip the engine underneath the user.
            App.Settings.Current.VoiceId = v.Id;
            App.Settings.Current.VoiceName = v.Name;
            App.Settings.NotifyChanged();
        }
    }

    /// <summary>
    /// Enable the ElevenLabs engine only when a key exists. Selecting a cloud
    /// engine with no credentials would just fail silently and fall back, which
    /// is confusing — better that it reads as unavailable and says why.
    /// </summary>
    private void UpdateElevenLabsAvailability()
    {
        var hasKey = !string.IsNullOrWhiteSpace(App.Settings.Current.ElevenLabsApiKey);
        ElevenLabsEngineItem.IsEnabled = hasKey;
        ElevenLabsEngineItem.Content = hasKey
            ? "ElevenLabs — cloud, premium"
            : "ElevenLabs — add an API key in the API Keys tab to enable";
        System.Windows.Automation.AutomationProperties.SetName(ElevenLabsEngineItem,
            hasKey ? "ElevenLabs, cloud premium voices"
                   : "ElevenLabs, unavailable. Add an API key in the API Keys tab to enable this engine.");

        // If the key was removed while ElevenLabs was active, fall back so the
        // app isn't left pointing at an engine it can't use.
        if (!hasKey && App.Settings.Current.VoiceEngine == "elevenlabs")
        {
            App.Settings.Current.VoiceEngine = "piper";
            App.Settings.NotifyChanged();
            foreach (ComboBoxItem item in EngineCombo.Items)
                if ((string)item.Tag == "piper") { EngineCombo.SelectedItem = item; break; }
        }
    }

    private void ShowWelcome_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw) mw.OpenPanel("welcome");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Settings.Save();
            SetSaveStatus("Settings saved.");
            _tts?.SpeakAsync("Settings saved.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsPanel.SaveSettings", ex);
            SetSaveStatus($"Save failed: {ex.Message}");
        }
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Settings.ResetToDefaults();                         // keeps API keys + first-run flag
            ThemeService.ApplyCurrent(App.Settings.Current);        // repaint immediately
            await PopulateFromSettingsAsync();                      // rebuild every control
            _loaded = true;
            SetSaveStatus("Settings reset to defaults.");
            _tts?.SpeakAsync("Settings reset to defaults.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsPanel.ResetSettings", ex);
            SetSaveStatus($"Reset failed: {ex.Message}");
        }
    }

    private void SetSaveStatus(string text)
    {
        SaveStatus.Text = text;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(
            SaveStatus, System.Windows.Automation.AutomationLiveSetting.Assertive);
        System.Windows.Automation.AutomationProperties.SetName(SaveStatus, text);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(SaveStatus)
                   ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(SaveStatus);
        peer?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void UpdatePiperManagerVisibility(string engine) =>
        PiperManager.Visibility = engine == "piper"
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// Build one row per catalog Piper voice: a bright check if it's downloaded,
    /// the voice name, and high-contrast Preview / Download buttons on the right.
    /// </summary>
    private void BuildPiperManager()
    {
        PiperVoiceList.Children.Clear();
        if (_tts == null) return;

        foreach (var voice in PiperVoiceCatalog.Voices)
        {
            var downloaded = _tts.Piper.IsVoiceDownloaded(voice.Id);

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ✓ bright check when downloaded
            var check = new TextBlock
            {
                Text = downloaded ? "✓" : "",
                Foreground = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x3A, 0xE6, 0x7B)), // bright green
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(check,
                downloaded ? $"{voice.Name} downloaded" : "");
            Grid.SetColumn(check, 0);
            row.Children.Add(check);

            var label = new TextBlock
            {
                Text = $"{voice.Display}  ·  {voice.SizeLabel}",
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            Grid.SetColumn(actions, 2);

            var preview = new Button
            {
                Content = "▶ Preview",
                Style = (Style)FindResource("HCButton"),
                Margin = new Thickness(6, 0, 0, 0),
                IsEnabled = downloaded,
            };
            // Say WHY it's unavailable — a silently greyed button is a dead end
            // for a screen-reader user.
            System.Windows.Automation.AutomationProperties.SetName(preview,
                downloaded
                    ? $"Preview the {voice.PrettyName} voice"
                    : $"Preview {voice.PrettyName} — unavailable until the voice is downloaded");
            System.Windows.Automation.AutomationProperties.SetHelpText(preview,
                downloaded ? "Plays a short sample in this voice."
                           : "Download this voice first, then Preview becomes available.");
            preview.Click += (_, _) => _ = _tts.PreviewAsync("piper", voice.Id);
            actions.Children.Add(preview);

            if (!downloaded)
            {
                var download = new Button
                {
                    Content = "⬇ Download",
                    Style = (Style)FindResource("HCButton"),
                    Margin = new Thickness(6, 0, 0, 0),
                };
                System.Windows.Automation.AutomationProperties.SetName(download, $"Download the {voice.Name} voice, {voice.SizeLabel}");
                download.Click += async (_, _) =>
                {
                    download.IsEnabled = false;
                    download.Content = "Downloading…";
                    var progress = new Progress<string>(msg => SetVoiceStatus(msg));
                    var ok = await _tts.Piper.DownloadVoiceAsync(voice.Id, progress);
                    if (ok)
                    {
                        // Make the just-downloaded voice the ACTIVE voice, so the
                        // user doesn't have to hunt for it in the list afterward
                        // (the reported "can't select it after downloading").
                        App.Settings.Current.VoiceId = voice.Id;
                        App.Settings.Current.VoiceName = voice.Name;
                        App.Settings.NotifyChanged();

                        BuildPiperManager();                 // refresh: check + enable preview
                        await PopulateVoicesForEngineAsync("piper"); // add + select in the combo
                        SetVoiceStatus($"{voice.Name} downloaded and now selected.");
                        _ = _tts.PreviewAsync("piper", voice.Id); // hear it immediately
                    }
                    else
                    {
                        download.IsEnabled = true;
                        download.Content = "⬇ Download";
                    }
                };
                actions.Children.Add(download);
            }

            row.Children.Add(actions);
            PiperVoiceList.Children.Add(row);
        }
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        App.Settings.Current.TtsSpeed = (float)SpeedSlider.Value;
        SpeedValue.Text = $"{SpeedSlider.Value:0.0}x";
        App.Settings.NotifyChanged();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        App.Settings.Current.Volume = (float)VolumeSlider.Value;
        VolumeValue.Text = $"{(int)(VolumeSlider.Value * 100)}%";
        App.Settings.NotifyChanged();
    }

    private void SttEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (SttEngineCombo.SelectedItem is ComboBoxItem item)
        {
            App.Settings.Current.SttEngine = (string)item.Tag;
            App.Settings.NotifyChanged();
            UpdateWhisperOptionsVisibility();

            var whisper = (string)item.Tag == "whisper";
            SetVoiceStatus(whisper
                ? "Dictation set to Whisper. The model downloads once on first use; " +
                  "everything runs locally."
                : "Dictation set to Windows Speech. No AI, no download.");
        }
    }

    private void WhisperModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (WhisperModelCombo.SelectedItem is ComboBoxItem item)
        {
            App.Settings.Current.WhisperModel = (string)item.Tag;
            App.Settings.NotifyChanged();
            SetVoiceStatus($"Whisper model set to {item.Content}.");
        }
    }

    private void UpdateWhisperOptionsVisibility()
    {
        var whisper = (SttEngineCombo.SelectedItem as ComboBoxItem)?.Tag as string == "whisper";
        WhisperOptions.Visibility = whisper
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private async void TestVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null) return;

        // The result MUST be audible, not just printed. This status line is the
        // one place a user diagnoses a broken voice engine — and the users most
        // likely to need it cannot read a TextBlock. Every outcome below is
        // therefore spoken as well as displayed, and the status element is a
        // live region so NVDA announces it even without focus.
        SetVoiceStatus("Speaking...");
        try
        {
            await _tts.SpeakAsync("Hello, Commander. This is how I will sound.");
            SetVoiceStatus($"Done. Voice engine: {App.Settings.Current.VoiceEngine}.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsPanel.TestVoice", ex);
            var msg = $"Voice test failed: {ex.Message}";
            SetVoiceStatus(msg);
            // Speak the failure through whatever engine still works (SpeakAsync
            // falls back to Windows SAPI), so the user is never left guessing.
            try { await _tts.SpeakAsync(msg); } catch { /* nothing left to try */ }
        }
    }

    /// <summary>
    /// Update the voice status line and announce it to screen readers. Marked
    /// as an assertive live region so NVDA reads the change immediately.
    /// </summary>
    private void SetVoiceStatus(string text)
    {
        VoiceStatus.Text = text;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(
            VoiceStatus, System.Windows.Automation.AutomationLiveSetting.Assertive);
        System.Windows.Automation.AutomationProperties.SetName(VoiceStatus, text);
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(VoiceStatus)
                   ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(VoiceStatus);
        peer?.RaiseAutomationEvent(
            System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    // ── API Keys tab ─────────────────────────────────────────────────────────
    private void Gw2KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Current.Gw2ApiKey = Gw2KeyBox.Password;
        App.Settings.NotifyChanged();
    }

    private void ElevenLabsKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Current.ElevenLabsApiKey = ElevenLabsKeyBox.Password;
        App.Settings.NotifyChanged();
        UpdateElevenLabsAvailability(); // enable/disable the engine option live
    }

    // ── API key validation (result is shown AND spoken) ─────────────────────
    private static readonly System.Net.Http.HttpClient _keyTestHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private async void TestGw2Key_Click(object sender, RoutedEventArgs e)
    {
        var key = Gw2KeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            await ReportKeyResult(Gw2KeyStatus, "No key entered.", success: false);
            return;
        }

        Gw2KeyStatus.Text = "Testing…";
        try
        {
            // /v2/tokeninfo validates the key and reports its name + scopes.
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                "https://api.guildwars2.com/v2/tokeninfo");
            req.Headers.Add("Authorization", $"Bearer {key}");
            using var resp = await _keyTestHttp.SendAsync(req);

            if (resp.IsSuccessStatusCode)
                await ReportKeyResult(Gw2KeyStatus, "Guild Wars 2 key is valid.", success: true);
            else
                await ReportKeyResult(Gw2KeyStatus,
                    "Key was rejected by the Guild Wars 2 API. Check it was copied completely.",
                    success: false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("TestGw2Key", ex);
            await ReportKeyResult(Gw2KeyStatus,
                "Could not reach the Guild Wars 2 API. Check your internet connection.",
                success: false);
        }
    }

    private async void TestElevenLabsKey_Click(object sender, RoutedEventArgs e)
    {
        var key = ElevenLabsKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            await ReportKeyResult(ElevenKeyStatus, "No key entered.", success: false);
            return;
        }

        ElevenKeyStatus.Text = "Testing…";
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get,
                "https://api.elevenlabs.io/v1/voices");
            req.Headers.Add("xi-api-key", key);
            using var resp = await _keyTestHttp.SendAsync(req);

            if (resp.IsSuccessStatusCode)
                await ReportKeyResult(ElevenKeyStatus,
                    "ElevenLabs key is valid. Reopen the voice list to see your cloud voices.",
                    success: true);
            else
                await ReportKeyResult(ElevenKeyStatus,
                    "Key was rejected by ElevenLabs. Check it was copied completely.",
                    success: false);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("TestElevenLabsKey", ex);
            await ReportKeyResult(ElevenKeyStatus,
                "Could not reach ElevenLabs. Check your internet connection.",
                success: false);
        }
    }

    /// <summary>Show the result on screen AND speak it, so the outcome is
    /// equally clear to sighted and non-sighted users.</summary>
    private async System.Threading.Tasks.Task ReportKeyResult(TextBlock target, string message, bool success)
    {
        target.Text = (success ? "✓ " : "✗ ") + message;
        System.Windows.Automation.AutomationProperties.SetName(target, message);
        if (_tts != null)
        {
            try { await _tts.SpeakAsync(message); }
            catch { /* result is still visible on screen */ }
        }
    }

    // ── HUD tab ──────────────────────────────────────────────────────────────
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (ThemeCombo.SelectedItem is not Models.Theme theme) return;

        // Persist the choice
        App.Settings.Current.ThemeId = theme.Id;
        App.Settings.NotifyChanged();

        // Apply it live. DynamicResource bindings in every panel's XAML pick
        // up the new colors automatically — no reload required.
        ThemeService.Apply(theme);

        // Update the description text under the dropdown
        ThemeDescription.Text = theme.Description;
    }

    private void AccessModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (AccessModeCombo.SelectedItem is not ComboBoxItem item) return;

        var mode = (string)item.Tag;
        var s = App.Settings.Current;
        s.AccessMode = mode;

        // v21: Access Mode is a PRESET, not a lock — it sets sensible defaults
        // and the user can adjust every individual setting afterward.
        // Accessibility itself (keyboard nav, UI Automation names, TTS buttons)
        // is never removed in either mode.
        if (mode == "vip")
        {
            s.HighContrast = true;
            s.FontSize = Math.Max(22, s.FontSize);
            s.AnnounceHudActivation = true;
            s.PanelEarcons = true;
            s.FirstRunIntroSpoken = false; // spoken orientation replays on next launch
        }
        else // standard — quieter guidance, never LESS accessible
        {
            s.FontSize = 18;
            s.AnnounceHudActivation = false;
            s.PanelEarcons = false;
            // HighContrast intentionally untouched: optional in Standard.
        }

        // Reflect the preset in the visible controls without re-triggering
        // their change handlers.
        _loaded = false;
        HudScaleSlider.Value = s.HudScale;
        FontSizeSlider.Value = s.FontSize;
        HighContrastCheck.IsChecked = s.HighContrast;
        EarconsCheck.IsChecked = s.PanelEarcons;
        AnnounceHudCheck.IsChecked = s.AnnounceHudActivation;
        _loaded = true;

        App.Settings.NotifyChanged();

        _tts?.SpeakAsync(mode == "vip"
            ? "VIP mode applied. High contrast on, large text, audio cues and spoken guidance enabled."
            : "Standard mode applied. Quieter guidance. Keyboard navigation, screen reader support and speech buttons remain fully available.");
    }

    // ── v21: HUD button visibility management ────────────────────────────────

    /// <summary>Build one "Show X" checkbox per HUD button, checked = visible.
    /// The Settings entry is always on and disabled so the user can never
    /// lock themselves out of this panel.</summary>
    private void BuildHudButtonsList()
    {
        HudButtonsList.Children.Clear();
        var hidden = App.Settings.Current.HiddenButtons;

        foreach (var def in Views.RadialHud.ButtonDefs)
        {
            // Fully qualified: this project loads BOTH WPF and Windows Forms
            // (Forms is needed for screen capture), and both define a
            // "CheckBox" — the compiler needs to be told which one we mean.
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = $"Show {def.Label}",
                IsChecked = !hidden.Contains(def.Id),
                Tag = def.Id,
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 6),
            };
            System.Windows.Automation.AutomationProperties.SetName(cb, $"Show {def.Label} button on the HUD");

            if (def.Id == "settings")
            {
                cb.IsChecked = true;
                cb.IsEnabled = false;
                System.Windows.Automation.AutomationProperties.SetHelpText(cb,
                    "The Settings button cannot be hidden, so you can always restore the others.");
            }
            else
            {
                System.Windows.Automation.AutomationProperties.SetHelpText(cb,
                    $"Unchecking removes {def.Label} from the wheel. It stays available on its hotkey.");
                cb.Checked += HudButtonCheck_Changed;
                cb.Unchecked += HudButtonCheck_Changed;
            }

            HudButtonsList.Children.Add(cb);
        }
    }

    private void HudButtonCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not string id) return;

        var hidden = App.Settings.Current.HiddenButtons;
        var show = cb.IsChecked == true;
        if (show) hidden.Remove(id);
        else if (!hidden.Contains(id)) hidden.Add(id);

        App.Settings.NotifyChanged();

        var label = Views.RadialHud.ButtonDefs.FirstOrDefault(b => b.Id == id).Label ?? id;
        _tts?.SpeakAsync(show ? $"{label} shown on the wheel." : $"{label} hidden. Still available on its hotkey.");
    }

    private void RestoreButtons_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.HiddenButtons.Clear();
        BuildHudButtonsList();
        App.Settings.NotifyChanged();
        _tts?.SpeakAsync("All HUD buttons restored.");
    }

    private void HudScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        App.Settings.Current.HudScale = (float)HudScaleSlider.Value;
        App.Settings.NotifyChanged();
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        App.Settings.Current.FontSize = (int)FontSizeSlider.Value;
        App.Settings.NotifyChanged();
    }

    private void HighContrastCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Current.HighContrast = HighContrastCheck.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void EarconsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Current.PanelEarcons = EarconsCheck.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void AnnounceHudCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        App.Settings.Current.AnnounceHudActivation = AnnounceHudCheck.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private void ResetHud_Click(object sender, RoutedEventArgs e)
    {
        // Find the RadialHud through the MainWindow and reset it
        if (Window.GetWindow(this) is MainWindow mw)
            mw.RadialHud.ResetPosition();
    }

    // ── About tab ────────────────────────────────────────────────────────────
    private void UpdateMumbleStatus()
    {
        if (_mumble == null)
        {
            MumbleStatus.Text = "MumbleLink reader not available.";
            return;
        }
        try
        {
            var data = _mumble.Read();
            if (data == null)
            {
                MumbleStatus.Text = "GW2: not detected\n(Game not running, at character select, or loading screen.)";
            }
            else
            {
                MumbleStatus.Text =
                    $"GW2: detected ✓\nMap ID: {data.MapId}\nPosition: ({data.PlayerX:F1}, {data.PlayerY:F1})\nTick: {data.UiTick}";
            }
        }
        catch (Exception ex)
        {
            MumbleStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ── Link handler ─────────────────────────────────────────────────────────
    private void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SettingsPanel OpenLink", ex);
        }
        e.Handled = true;
    }
}
