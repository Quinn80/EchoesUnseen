using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Input;
using EchoesUnseen.Models;
using EchoesUnseen.Services;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// The Accessibility tab — the Vision Accessibility Suite's front end.
///
/// It is its own file because the rest of SettingsPanel is already long, and
/// because everything here follows one shape:
///
///     the user changes one control
///       -> write that one value
///       -> the profile becomes "Custom" (never silently re-applied)
///       -> settings are saved and the whole interface re-draws at once
///       -> the change is spoken, because the person who needs it most may not
///          be able to see that it happened
///
/// Nothing on this tab duplicates another tab. Text size, high contrast and how
/// large long lists are drawn MOVED here from HUD rather than being copied.
/// </summary>
public partial class SettingsPanel
{
    // Set while the tab is being filled in, so writing a control's value does not
    // look like the user changing it.
    private bool _a11ySyncing;

    // ── Filling the tab in ──────────────────────────────────────────────────

    private void PopulateAccessibilityTab()
    {
        var s = App.Settings.Current;
        var a = s.Accessibility;
        _a11ySyncing = true;
        try
        {
            A11yProfileCombo.Items.Clear();
            foreach (var (id, name, _) in AccessibilityService.Profiles)
                A11yProfileCombo.Items.Add(new ComboItem(id, name));
            Select(A11yProfileCombo, a.Profile);
            A11yProfileDescription.Text = DescriptionOf(a.Profile);

            Fill(A11yScaleCombo, ("standard", "Standard"), ("large", "Large (15% bigger)"),
                 ("extra-large", "Extra large (30% bigger)"), ("custom", "Custom"));
            Select(A11yScaleCombo, a.InterfaceScale);
            A11yCustomScaleSlider.Value = Math.Clamp(a.CustomInterfaceScale, 0.85, 1.6);
            A11yCustomScaleRow.Visibility = a.InterfaceScale == "custom" ? Visibility.Visible : Visibility.Collapsed;
            A11yCustomScaleLabel.Text = $"Custom scale: {a.CustomInterfaceScale * 100:0}%";

            Fill(A11yTextSizeCombo, ("standard", "Standard (22 point)"), ("large", "Large (26 point)"),
                 ("extra-large", "Extra large (30 point)"), ("custom", "Exact size"));
            Select(A11yTextSizeCombo, a.TextSize);
            A11yCustomTextRow.Visibility = a.TextSize == "custom" ? Visibility.Visible : Visibility.Collapsed;
            FontSizeSlider.Value = s.FontSize;
            FontSizeLabel.Text = $"Exact size: {s.FontSize} point";

            A11yBoldText.IsChecked = a.BoldText;
            A11yLargerControls.IsChecked = a.LargerControls;
            A11yStrongerBorders.IsChecked = a.StrongerBorders;

            Fill(A11yTooltipCombo, ("standard", "Same as body text"), ("large", "Large"),
                 ("extra-large", "Extra large"));
            Select(A11yTooltipCombo, a.TooltipSize);

            foreach (ComboBoxItem item in AccessModeCombo.Items)
                if ((string)item.Tag == s.AccessMode) { AccessModeCombo.SelectedItem = item; break; }

            Fill(A11yColourVisionCombo,
                 ("standard", "Standard"),
                 ("protanopia", "Protanopia friendly (red-blind)"),
                 ("deuteranopia", "Deuteranopia friendly (green-blind)"),
                 ("tritanopia", "Tritanopia friendly (blue-blind)"),
                 ("high-contrast", "High contrast"));
            Select(A11yColourVisionCombo, a.ColorVision);

            HighContrastCheck.IsChecked = s.HighContrast;
            A11yReduceTransparency.IsChecked = a.ReduceTransparency;
            A11ySolidTextBackgrounds.IsChecked = a.SolidTextBackgrounds;

            A11yAccentCombo.Items.Clear();
            foreach (var (id, name, _) in AccessibilityService.Accents)
                A11yAccentCombo.Items.Add(new ComboItem(id, name));
            Select(A11yAccentCombo, a.AccentColor);
            A11yCustomAccentBox.Text = a.CustomAccent;
            A11yAccentCheckLabel.Text = AccessibilityService.CheckAccent(a.CustomAccent);

            A11yReduceMotion.IsChecked = a.ReduceMotion;
            A11yDisablePulsing.IsChecked = a.DisablePulsing;
            A11yReduceGlow.IsChecked = a.ReduceGlow;
            A11yReduceDecorative.IsChecked = a.ReduceDecorativeAnimation;
            A11yReduceFlashing.IsChecked = a.ReduceFlashing;
            A11yDimBright.IsChecked = a.DimBrightEffects;

            Fill(A11yFocusCombo, ("standard", "Standard"), ("strong", "Strong"),
                 ("extra-strong", "Extra strong"));
            Select(A11yFocusCombo, a.FocusStrength);
            A11yHighlightSelected.IsChecked = a.HighlightSelected;
            A11ySimplified.IsChecked = a.SimplifiedInterface;
            A11yHideDecorative.IsChecked = a.HideDecorativeEffects;

            Fill(A11yAreaCombo, ("auto", "Anywhere (no preference)"), ("center", "Middle of the screen"),
                 ("left", "Left half"), ("right", "Right half"), ("top", "Top half"),
                 ("bottom", "Bottom half"), ("custom", "A part I choose"));
            Select(A11yAreaCombo, a.ViewingArea);
            A11yAreaNote.Text = AreaNote(a);

            A11yAreaX.Text = a.CustomAreaX.ToString("0.##", CultureInfo.InvariantCulture);
            A11yAreaY.Text = a.CustomAreaY.ToString("0.##", CultureInfo.InvariantCulture);
            A11yAreaW.Text = a.CustomAreaWidth.ToString("0.##", CultureInfo.InvariantCulture);
            A11yAreaH.Text = a.CustomAreaHeight.ToString("0.##", CultureInfo.InvariantCulture);
            A11yAreaSummary.Text = AreaSummary(a);
        }
        finally { _a11ySyncing = false; }
    }

    /// <summary>A dropdown row that remembers the id behind the words it shows.</summary>
    private sealed record ComboItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static void Fill(ComboBox box, params (string Id, string Name)[] options)
    {
        box.Items.Clear();
        foreach (var (id, name) in options) box.Items.Add(new ComboItem(id, name));
    }

    private static void Select(ComboBox box, string id)
    {
        foreach (var item in box.Items)
            if (item is ComboItem c && c.Id == id) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string IdOf(ComboBox box) => box.SelectedItem is ComboItem c ? c.Id : "standard";

    private static string DescriptionOf(string profileId) =>
        AccessibilityService.Profiles.FirstOrDefault(p => p.Id == profileId).Description ?? "";

    private static string AreaNote(AccessibilitySettings a) => a.ViewingArea switch
    {
        "auto" => "Messages appear wherever each feature normally puts them.",
        "center" => "New accessibility messages start in the middle of the screen.",
        "left" => "New accessibility messages start in the left half of the screen.",
        "right" => "New accessibility messages start in the right half of the screen.",
        "top" => "New accessibility messages start in the top half of the screen.",
        "bottom" => "New accessibility messages start in the bottom half of the screen.",
        "custom" => "New accessibility messages start in the area set under Advanced.",
        _ => "",
    };

    private static string AreaSummary(AccessibilitySettings a) =>
        $"That is a box starting {a.CustomAreaX * 100:0}% across and {a.CustomAreaY * 100:0}% down, " +
        $"{a.CustomAreaWidth * 100:0}% of the screen wide and {a.CustomAreaHeight * 100:0}% tall.";

    // ── One place where every change lands ──────────────────────────────────

    /// <summary>
    /// Apply one change the user made by hand. The profile becomes "Custom",
    /// because from here on the settings are theirs and a profile must never
    /// quietly overwrite them again.
    /// </summary>
    private void A11yEdit(Action<AppSettings> change, string spoken)
    {
        if (!_loaded || _a11ySyncing) return;
        var s = App.Settings.Current;
        change(s);
        AccessibilityService.MarkCustom(s);
        App.Settings.NotifyChanged();      // applies theme + accessibility, then saves
        App.Settings.Save();               // and immediately, so a crash cannot lose it

        _a11ySyncing = true;
        try
        {
            Select(A11yProfileCombo, s.Accessibility.Profile);
            A11yProfileDescription.Text = DescriptionOf(s.Accessibility.Profile);
        }
        finally { _a11ySyncing = false; }

        SetSaveStatus(spoken);
        _tts?.SpeakAsync(spoken);
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    // ── Quick setup ─────────────────────────────────────────────────────────

    private async void A11yProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _a11ySyncing) return;
        var id = IdOf(A11yProfileCombo);
        var s = App.Settings.Current;

        AccessibilityService.ApplyProfile(id, s);
        App.Settings.NotifyChanged();
        App.Settings.Save();
        PopulateAccessibilityTab();

        var name = AccessibilityService.Profiles.FirstOrDefault(p => p.Id == id).Name ?? id;
        SetSaveStatus($"{name} applied.");
        _tts?.SpeakAsync($"{name}. {DescriptionOf(id)} You can change anything from here.");
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void A11yResetAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Put every setting on the Accessibility tab back to its default?\n\n" +
            "Your voice, HUD placement, shortcuts and feature switches are not touched.",
            "Reset accessibility settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            _tts?.SpeakAsync("Nothing was changed.");
            return;
        }

        AccessibilityService.ResetAll(App.Settings.Current);
        App.Settings.NotifyChanged();
        App.Settings.Save();
        PopulateAccessibilityTab();
        SetSaveStatus("Accessibility settings reset.");
        _tts?.SpeakAsync("Accessibility settings are back to standard. Nothing else was changed.");
    }

    private void A11yResetSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string section) return;
        AccessibilityService.ResetSection(section, App.Settings.Current);
        App.Settings.NotifyChanged();
        App.Settings.Save();
        PopulateAccessibilityTab();
        SetSaveStatus("Section reset.");
        _tts?.SpeakAsync("That section is back to its defaults.");
    }

    // ── Readability and size ────────────────────────────────────────────────

    private void A11yScale_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yScaleCombo);
        A11yCustomScaleRow.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        A11yEdit(s => s.Accessibility.InterfaceScale = id, $"Interface scale {Spoken(id)}.");
    }

    private void A11yCustomScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = Math.Round(A11yCustomScaleSlider.Value, 2);
        A11yCustomScaleLabel.Text = $"Custom scale: {v * 100:0}%";
        A11yEdit(s => s.Accessibility.CustomInterfaceScale = v, $"Scale {v * 100:0} per cent.");
    }

    private void A11yTextSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yTextSizeCombo);
        A11yCustomTextRow.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        A11yEdit(s => s.Accessibility.TextSize = id, $"Text size {Spoken(id)}.");
    }

    private void A11yBoldText_Changed(object sender, RoutedEventArgs e)
    {
        var on = A11yBoldText.IsChecked == true;
        A11yEdit(s => s.Accessibility.BoldText = on, $"Bold text {OnOff(on)}.");
    }

    private void A11yLargerControls_Changed(object sender, RoutedEventArgs e)
    {
        var on = A11yLargerControls.IsChecked == true;
        A11yEdit(s => s.Accessibility.LargerControls = on, $"Larger controls {OnOff(on)}.");
    }

    private void A11yStrongerBorders_Changed(object sender, RoutedEventArgs e)
    {
        var on = A11yStrongerBorders.IsChecked == true;
        A11yEdit(s => s.Accessibility.StrongerBorders = on, $"Stronger borders {OnOff(on)}.");
    }

    private void A11yTooltip_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yTooltipCombo);
        A11yEdit(s => s.Accessibility.TooltipSize = id, $"Tooltip size {Spoken(id)}.");
    }

    private static string Spoken(string id) => id switch
    {
        "extra-large" => "extra large",
        "extra-strong" => "extra strong",
        "high-contrast" => "high contrast",
        "custom" => "custom",
        _ => id.Replace('-', ' '),
    };

    // ── Colour and contrast ─────────────────────────────────────────────────

    private void A11yColourVision_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yColourVisionCombo);
        A11yEdit(s =>
        {
            s.Accessibility.ColorVision = id;
            // "High contrast" in this list IS the High Contrast switch; keeping one
            // idea in one place rather than two controls that disagree.
            if (id == "high-contrast") s.HighContrast = true;
            HighContrastCheck.IsChecked = s.HighContrast;
        }, id == "standard" ? "Standard colours." : $"{Spoken(id)} colours.");
    }

    private void A11yReduceTransparency_Changed(object sender, RoutedEventArgs e)
    {
        var on = A11yReduceTransparency.IsChecked == true;
        A11yEdit(s => s.Accessibility.ReduceTransparency = on, $"Reduce transparency {OnOff(on)}.");
    }

    private void A11ySolidText_Changed(object sender, RoutedEventArgs e)
    {
        var on = A11ySolidTextBackgrounds.IsChecked == true;
        A11yEdit(s => s.Accessibility.SolidTextBackgrounds = on, $"Solid backgrounds behind text {OnOff(on)}.");
    }

    private void A11yAccent_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yAccentCombo);
        var name = AccessibilityService.Accents.FirstOrDefault(x => x.Id == id).Name ?? id;
        A11yEdit(s => s.Accessibility.AccentColor = id, $"Accent colour: {name}.");
    }

    private void A11yCustomAccent_Changed(object sender, RoutedEventArgs e)
    {
        var text = (A11yCustomAccentBox.Text ?? "").Trim();
        A11yAccentCheckLabel.Text = AccessibilityService.CheckAccent(text);
        if (!text.StartsWith('#') || (text.Length != 7 && text.Length != 9))
        {
            if (_loaded && !_a11ySyncing) _tts?.SpeakAsync("That is not a colour. Use a hash and six characters, like #FFC94A.");
            return;
        }
        A11yEdit(s => s.Accessibility.CustomAccent = text, "Custom accent colour set.");
    }

    private void A11yCustomAccent_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) A11yCustomAccent_Changed(sender, e);
    }

    // ── Motion and eye comfort ──────────────────────────────────────────────

    private void A11yMotion_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        var on = box.IsChecked == true;
        var label = box.Content?.ToString() ?? "Setting";
        A11yEdit(s =>
        {
            var a = s.Accessibility;
            if (ReferenceEquals(box, A11yReduceMotion)) a.ReduceMotion = on;
            else if (ReferenceEquals(box, A11yDisablePulsing)) a.DisablePulsing = on;
            else if (ReferenceEquals(box, A11yReduceGlow)) a.ReduceGlow = on;
            else if (ReferenceEquals(box, A11yReduceDecorative)) a.ReduceDecorativeAnimation = on;
            else if (ReferenceEquals(box, A11yReduceFlashing)) a.ReduceFlashing = on;
            else if (ReferenceEquals(box, A11yDimBright)) a.DimBrightEffects = on;
        }, $"{label} {OnOff(on)}.");
    }

    // ── Focus and visibility ────────────────────────────────────────────────

    private void A11yFocus_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yFocusCombo);
        A11yEdit(s => s.Accessibility.FocusStrength = id, $"Focus indicator {Spoken(id)}.");
    }

    private void A11yFocusCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box) return;
        var on = box.IsChecked == true;
        var label = box.Content?.ToString() ?? "Setting";
        A11yEdit(s =>
        {
            var a = s.Accessibility;
            if (ReferenceEquals(box, A11yHighlightSelected)) a.HighlightSelected = on;
            else if (ReferenceEquals(box, A11ySimplified)) a.SimplifiedInterface = on;
            else if (ReferenceEquals(box, A11yHideDecorative)) a.HideDecorativeEffects = on;
        }, $"{label} {OnOff(on)}.");
    }

    // ── Preferred viewing area ──────────────────────────────────────────────

    private void A11yArea_Changed(object sender, SelectionChangedEventArgs e)
    {
        var id = IdOf(A11yAreaCombo);
        A11yEdit(s => s.Accessibility.ViewingArea = id, AreaSpoken(id));
        A11yAreaNote.Text = AreaNote(App.Settings.Current.Accessibility);
    }

    private static string AreaSpoken(string id) => id switch
    {
        "auto" => "No preferred area. Messages appear where each feature normally puts them.",
        "custom" => "Custom area. Set the numbers under Advanced.",
        _ => $"Preferred viewing area: {Spoken(id).Replace("center", "middle")}.",
    };

    private void A11yArea_Numbers(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _a11ySyncing) return;
        double Read(TextBox box, double fallback) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? Math.Clamp(v, 0, 1) : fallback;

        var a = App.Settings.Current.Accessibility;
        var x = Read(A11yAreaX, a.CustomAreaX);
        var y = Read(A11yAreaY, a.CustomAreaY);
        var w = Math.Max(0.05, Read(A11yAreaW, a.CustomAreaWidth));
        var h = Math.Max(0.05, Read(A11yAreaH, a.CustomAreaHeight));

        A11yEdit(s =>
        {
            s.Accessibility.CustomAreaX = x;
            s.Accessibility.CustomAreaY = y;
            s.Accessibility.CustomAreaWidth = w;
            s.Accessibility.CustomAreaHeight = h;
        }, "Viewing area updated.");
        A11yAreaSummary.Text = AreaSummary(App.Settings.Current.Accessibility);
    }

    private void A11yArea_NumbersKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) A11yArea_Numbers(sender, e);
    }
}
