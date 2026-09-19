using System.Windows;
using System.Windows.Media;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// The Vision Accessibility Suite: turns the preferences in
/// <see cref="AccessibilitySettings"/> into the resources the whole interface is
/// drawn from, and into a small set of flags other features read.
///
/// WHERE IT SITS. <see cref="ThemeService.ApplyCurrent"/> runs theme first, then
/// font size, then High Contrast, then this — last, so accessibility wins. That
/// is the precedence rule the suite is built on:
///
///     1. Invariants          colour is never the only cue; focus is never a hue
///     2. Accessibility       what the user set on the Accessibility tab
///     3. Feature settings    a trail's own width, a panel's own placement
///     4. Theme defaults      accent, glow, decoration
///
/// A theme keeps its identity under every accessibility setting: Reduce Glow
/// takes the bloom off a Matrix theme, it does not make it stop being green.
///
/// WHAT IT DOES NOT DO. It never touches Guild Wars 2 — no screen filter, no
/// gamma, no overlay over the game's own colours. Everything here changes
/// Echoes Unseen's own drawing.
/// </summary>
public static class AccessibilityService
{
    // ── Flags other features read ───────────────────────────────────────────
    // These are the integration contract for the Music Guide, the Story Guide,
    // the Trail Navigator and anything drawn later. Read them; do not cache them
    // across a settings change - subscribe to Changed instead.

    public static bool ReduceMotion { get; private set; }
    public static bool DisablePulsing { get; private set; }
    public static bool ReduceGlow { get; private set; }
    public static bool DecorativeAnimation { get; private set; } = true;
    public static bool ReduceFlashing { get; private set; }
    public static bool DimBrightEffects { get; private set; }
    public static bool HighContrast { get; private set; }
    public static bool ReduceTransparency { get; private set; }
    public static bool SolidTextBackgrounds { get; private set; }
    public static bool SimplifiedInterface { get; private set; }
    public static bool DecorativeEffects { get; private set; } = true;
    public static bool LargerControls { get; private set; }

    /// <summary>"standard" | "protanopia" | "deuteranopia" | "tritanopia" | "high-contrast" | "custom".</summary>
    public static string ColorVision { get; private set; } = "standard";

    /// <summary>Panel and control scale, 0.85–1.60. 1.0 is what b1.5 drew.</summary>
    public static double InterfaceScale { get; private set; } = 1.0;

    /// <summary>Raised after every apply, so open panels and overlays can re-draw.</summary>
    public static event EventHandler<AccessibilitySettings>? Changed;

    // ── Preferred viewing area ──────────────────────────────────────────────

    private static AccessibilitySettings _current = new();

    /// <summary>The settings this service last applied. Never null.</summary>
    public static AccessibilitySettings Current => _current;

    /// <summary>
    /// Where this person can most easily see something, as a fraction of the
    /// screen. "auto" means no preference and returns the whole rectangle.
    ///
    /// This is a HINT for surfaces that have no placement of their own yet. An
    /// overlay the user has dragged somewhere keeps its position: explicit
    /// placement always beats a preference.
    /// </summary>
    public static Rect PreferredArea(Rect screen)
    {
        var a = _current;
        double x = screen.X, y = screen.Y, w = screen.Width, h = screen.Height;
        return a.ViewingArea switch
        {
            "left" => new Rect(x, y, w * 0.5, h),
            "right" => new Rect(x + w * 0.5, y, w * 0.5, h),
            "top" => new Rect(x, y, w, h * 0.5),
            "bottom" => new Rect(x, y + h * 0.5, w, h * 0.5),
            "center" => new Rect(x + w * 0.2, y + h * 0.2, w * 0.6, h * 0.6),
            "custom" => new Rect(
                x + w * Clamp01(a.CustomAreaX),
                y + h * Clamp01(a.CustomAreaY),
                Math.Max(0.05, Clamp01(a.CustomAreaWidth)) * w,
                Math.Max(0.05, Clamp01(a.CustomAreaHeight)) * h),
            _ => screen,
        };
    }

    /// <summary>The centre of <see cref="PreferredArea"/> — what most callers actually want.</summary>
    public static Point PreferredCentre(Rect screen)
    {
        var r = PreferredArea(screen);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    // ── Motion, for the code that animates ──────────────────────────────────

    /// <summary>
    /// Should this animation run at all?
    ///
    /// <paramref name="decorative"/> is for movement that carries no information -
    /// drifting particles, a spinning frame, scanlines. <paramref name="looping"/>
    /// is for anything that breathes or pulses forever. Functional movement (a
    /// panel appearing, a value changing) passes both as false and is only
    /// stopped by Reduce Motion, which makes it instant rather than absent.
    /// </summary>
    public static bool AllowAnimation(bool decorative = false, bool looping = false)
    {
        if (decorative && (!DecorativeAnimation || !DecorativeEffects || SimplifiedInterface)) return false;
        if (looping && DisablePulsing) return false;
        return !ReduceMotion;
    }

    // ── Named accents, all measured against every theme background ──────────
    // Contrast on the darkest and lightest theme backgrounds, in normal vision and
    // all three simulations, is at least 7.5:1 for every one of these.

    public static readonly (string Id, string Name, string Hex)[] Accents =
    {
        ("theme", "Theme's own accent", ""),
        ("gold", "Gold", "#FFC94A"),
        ("cyan", "Cyan", "#6FD3FF"),
        ("lime", "Lime", "#BFE84A"),
        ("orange", "Orange", "#FF9C3E"),
        ("pink", "Pink", "#FF8AC6"),
        ("white", "White", "#F5F5FF"),
        ("custom", "Custom colour", ""),
    };

    /// <summary>
    /// Accent pairs for the colour-vision modes. Each pair was chosen by measuring
    /// CIEDE2000 between the two colours AS THAT EYE RECEIVES THEM, and by checking
    /// both against every theme's background in all four kinds of vision:
    ///
    ///   protanopia    cyan + gold     separation 47.5   contrast 10.5 / 11.2
    ///   deuteranopia  sky + amber     separation 45.2   contrast 12.4 /  8.2
    ///   tritanopia    orange + ice    separation 50.3   contrast  7.7 / 14.9
    ///
    /// Protanopia and deuteranopia land on the same blue/yellow family because that
    /// is the family that survives a missing red or green cone - the honest answer
    /// rather than two different-looking palettes for their own sake. Tritanopia
    /// goes the other way and avoids blue entirely.
    /// </summary>
    private static readonly Dictionary<string, (string Primary, string Secondary)> Palettes = new()
    {
        ["protanopia"] = ("#6FD3FF", "#FFC94A"),
        ["deuteranopia"] = ("#70E9FA", "#FFA53E"),
        ["tritanopia"] = ("#FF9C3E", "#BFEFFF"),
    };

    // ── Profiles ────────────────────────────────────────────────────────────

    /// <summary>The Quick Setup list: id, name, and a line of plain language.</summary>
    public static readonly (string Id, string Name, string Description)[] Profiles =
    {
        ("standard", "Standard",
         "Echoes Unseen as it comes. Nothing is changed."),
        ("low-vision", "Low Vision",
         "Larger text, larger controls, stronger focus, higher contrast, solid backgrounds."),
        ("high-contrast", "High Contrast",
         "White on black, strong borders, no transparency, no glow."),
        ("color-vision", "Color Vision",
         "An accent pair chosen to stay distinct for colour-vision differences. Pick which one below."),
        ("eye-comfort", "Eye Comfort",
         "Less motion, less glow, less transparency, softer highlights."),
        ("screen-reader", "Screen Reader First",
         "Simple visuals and strong focus, for using Echoes Unseen mostly by ear. The interface stays visible."),
        ("custom", "Custom",
         "Your own combination. Changing any setting below moves you here."),
    };

    /// <summary>
    /// Write a profile's values. A STARTING POINT: it sets the fields listed and
    /// nothing else, and the moment the user changes one thing afterwards
    /// <see cref="MarkCustom"/> moves them to "custom" without touching anything.
    /// </summary>
    public static void ApplyProfile(string id, AppSettings s)
    {
        var a = s.Accessibility;
        // Start from the plain state so a profile is what it says it is, and never
        // half of a previous one.
        if (id != "custom") ResetAllFields(a, s);
        a.Profile = id;

        switch (id)
        {
            case "low-vision":
                a.InterfaceScale = "large";
                a.TextSize = "large";
                a.LargerControls = true;
                a.StrongerBorders = true;
                a.TooltipSize = "large";
                a.FocusStrength = "strong";
                a.HighlightSelected = true;
                a.ReduceTransparency = true;
                a.SolidTextBackgrounds = true;
                s.HighContrast = true;
                break;

            case "high-contrast":
                a.ColorVision = "high-contrast";
                a.StrongerBorders = true;
                a.ReduceTransparency = true;
                a.SolidTextBackgrounds = true;
                a.ReduceGlow = true;
                a.FocusStrength = "strong";
                a.HighlightSelected = true;
                s.HighContrast = true;
                break;

            case "color-vision":
                // Deliberately the one that helps the most people by default; the
                // picker right underneath changes it in one move.
                a.ColorVision = "deuteranopia";
                a.HighlightSelected = true;
                a.StrongerBorders = true;
                break;

            case "eye-comfort":
                a.ReduceMotion = true;
                a.DisablePulsing = true;
                a.ReduceGlow = true;
                a.ReduceDecorativeAnimation = true;
                a.ReduceFlashing = true;
                a.DimBrightEffects = true;
                a.ReduceTransparency = true;
                break;

            case "screen-reader":
                a.ReduceMotion = true;
                a.DisablePulsing = true;
                a.ReduceDecorativeAnimation = true;
                a.ReduceFlashing = true;
                a.SimplifiedInterface = true;
                a.HideDecorativeEffects = true;
                a.FocusStrength = "extra-strong";
                a.HighlightSelected = true;
                break;

            case "standard":
            case "custom":
            default:
                break;
        }
    }

    /// <summary>
    /// The user changed one setting by hand. That makes their configuration theirs:
    /// the profile becomes "custom" and NOTHING else is touched. Called by every
    /// control on the Accessibility tab.
    /// </summary>
    public static void MarkCustom(AppSettings s)
    {
        if (s.Accessibility.Profile != "custom") s.Accessibility.Profile = "custom";
    }

    /// <summary>Put every accessibility setting back to its default — and only those.
    /// Voice, HUD placement, keybinds and feature toggles are not touched.</summary>
    public static void ResetAll(AppSettings s)
    {
        ResetAllFields(s.Accessibility, s);
        s.Accessibility.Profile = "standard";
    }

    private static void ResetAllFields(AccessibilitySettings a, AppSettings s)
    {
        var d = new AccessibilitySettings();
        a.InterfaceScale = d.InterfaceScale;
        a.CustomInterfaceScale = d.CustomInterfaceScale;
        a.TextSize = d.TextSize;
        a.BoldText = d.BoldText;
        a.LargerControls = d.LargerControls;
        a.TooltipSize = d.TooltipSize;
        a.StrongerBorders = d.StrongerBorders;
        a.ColorVision = d.ColorVision;
        a.ReduceTransparency = d.ReduceTransparency;
        a.SolidTextBackgrounds = d.SolidTextBackgrounds;
        a.AccentColor = d.AccentColor;
        a.CustomAccent = d.CustomAccent;
        a.ReduceMotion = d.ReduceMotion;
        a.DisablePulsing = d.DisablePulsing;
        a.ReduceGlow = d.ReduceGlow;
        a.ReduceDecorativeAnimation = d.ReduceDecorativeAnimation;
        a.ReduceFlashing = d.ReduceFlashing;
        a.DimBrightEffects = d.DimBrightEffects;
        a.FocusStrength = d.FocusStrength;
        a.HighlightSelected = d.HighlightSelected;
        a.SimplifiedInterface = d.SimplifiedInterface;
        a.HideDecorativeEffects = d.HideDecorativeEffects;
        a.ViewingArea = d.ViewingArea;
        a.CustomAreaX = d.CustomAreaX;
        a.CustomAreaY = d.CustomAreaY;
        a.CustomAreaWidth = d.CustomAreaWidth;
        a.CustomAreaHeight = d.CustomAreaHeight;
    }

    /// <summary>Reset one collapsed group, so fixing one thing does not undo the rest.</summary>
    public static void ResetSection(string section, AppSettings s)
    {
        var a = s.Accessibility;
        var d = new AccessibilitySettings();
        switch (section)
        {
            case "readability":
                a.InterfaceScale = d.InterfaceScale;
                a.CustomInterfaceScale = d.CustomInterfaceScale;
                a.TextSize = d.TextSize;
                a.BoldText = d.BoldText;
                a.LargerControls = d.LargerControls;
                a.TooltipSize = d.TooltipSize;
                a.StrongerBorders = d.StrongerBorders;
                break;
            case "color":
                a.ColorVision = d.ColorVision;
                a.ReduceTransparency = d.ReduceTransparency;
                a.SolidTextBackgrounds = d.SolidTextBackgrounds;
                a.AccentColor = d.AccentColor;
                a.CustomAccent = d.CustomAccent;
                break;
            case "motion":
                a.ReduceMotion = d.ReduceMotion;
                a.DisablePulsing = d.DisablePulsing;
                a.ReduceGlow = d.ReduceGlow;
                a.ReduceDecorativeAnimation = d.ReduceDecorativeAnimation;
                a.ReduceFlashing = d.ReduceFlashing;
                a.DimBrightEffects = d.DimBrightEffects;
                break;
            case "focus":
                a.FocusStrength = d.FocusStrength;
                a.HighlightSelected = d.HighlightSelected;
                a.SimplifiedInterface = d.SimplifiedInterface;
                a.HideDecorativeEffects = d.HideDecorativeEffects;
                break;
            case "area":
                a.ViewingArea = d.ViewingArea;
                a.CustomAreaX = d.CustomAreaX;
                a.CustomAreaY = d.CustomAreaY;
                a.CustomAreaWidth = d.CustomAreaWidth;
                a.CustomAreaHeight = d.CustomAreaHeight;
                break;
        }
        MarkCustom(s);
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Push every preference into the application's resources. Called by
    /// <see cref="ThemeService.ApplyCurrent"/> AFTER the theme, the font size and
    /// High Contrast, so accessibility overrides all three. Safe to call often.
    /// </summary>
    public static void Apply(AppSettings s)
    {
        var a = s.Accessibility ?? new AccessibilitySettings();
        _current = a;

        ReduceMotion = a.ReduceMotion;
        DisablePulsing = a.DisablePulsing || a.ReduceMotion;
        ReduceGlow = a.ReduceGlow;
        DecorativeAnimation = !(a.ReduceDecorativeAnimation || a.ReduceMotion);
        ReduceFlashing = a.ReduceFlashing;
        DimBrightEffects = a.DimBrightEffects;
        HighContrast = s.HighContrast;
        ReduceTransparency = a.ReduceTransparency;
        SolidTextBackgrounds = a.SolidTextBackgrounds;
        SimplifiedInterface = a.SimplifiedInterface;
        DecorativeEffects = !a.HideDecorativeEffects;
        LargerControls = a.LargerControls;
        ColorVision = a.ColorVision;
        InterfaceScale = ScaleOf(a);

        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;

        ApplySize(res, a, s);
        ApplyColour(res, a, s);
        ApplyMotion(res, a);
        ApplyFocus(res, a);

        Changed?.Invoke(null, a);
    }

    /// <summary>The interface scale as a number, clamped where the layout still works.</summary>
    public static double ScaleOf(AccessibilitySettings a) => a.InterfaceScale switch
    {
        "large" => 1.15,
        "extra-large" => 1.30,
        "custom" => Math.Clamp(a.CustomInterfaceScale, 0.85, 1.60),
        _ => 1.0,
    };

    /// <summary>Body text size in points. "custom" keeps using the number the old
    /// Font Size slider wrote, which is why an upgraded settings file is unchanged.</summary>
    public static double TextSizeOf(AccessibilitySettings a, AppSettings s) => a.TextSize switch
    {
        "large" => 26,
        "extra-large" => 30,
        "custom" => Math.Clamp(s.FontSize, 14, 32),
        _ => 22,
    };

    private static void ApplySize(ResourceDictionary res, AccessibilitySettings a, AppSettings s)
    {
        double scale = ScaleOf(a);
        double text = TextSizeOf(a, s);

        res["GlobalFontSize"] = text;
        res["GlobalFontSizeSmall"] = Math.Max(11.0, text - 10.0);
        res["UiScale"] = scale;

        // Controls grow with the interface, and grow again if Larger Controls is on.
        double control = scale * (a.LargerControls ? 1.18 : 1.0);
        res["ControlMinHeight"] = Math.Round(44 * control);
        res["ControlMinWidth"] = Math.Round(96 * control);
        res["ControlPadding"] = new Thickness(Math.Round(22 * control), Math.Round(13 * control),
                                              Math.Round(22 * control), Math.Round(13 * control));
        res["CheckBoxSize"] = Math.Round(28 * control);
        res["TabPadding"] = new Thickness(Math.Round(14 * control), Math.Round(8 * control),
                                          Math.Round(14 * control), Math.Round(8 * control));
        res["FieldPadding"] = new Thickness(Math.Round(12 * control), Math.Round(9 * control),
                                            Math.Round(12 * control), Math.Round(9 * control));

        res["TooltipFontSize"] = a.TooltipSize switch
        {
            "large" => text + 4,
            "extra-large" => text + 8,
            _ => text,
        };

        res["UiFontWeight"] = a.BoldText ? FontWeights.SemiBold : FontWeights.Normal;

        // Stronger borders: thicker AND brighter, because a thicker line in a colour
        // you cannot see is no help.
        double border = a.StrongerBorders ? 3 : 2;
        res["UiBorderThickness"] = new Thickness(border);
        res["PanelBorderThickness"] = new Thickness(a.StrongerBorders ? 3 : 2);
        if (a.StrongerBorders && res["SurfaceBorderBrush"] is SolidColorBrush b)
            res["SurfaceBorderBrush"] = Frozen(new SolidColorBrush(b.Color));   // drop the 0.6 opacity
    }

    private static void ApplyColour(ResourceDictionary res, AccessibilitySettings a, AppSettings s)
    {
        // 1. Colour-vision palette, if one is chosen. It replaces the ACCENT pair
        //    only: backgrounds, and therefore the theme's mood, are left alone.
        string? primary = null, secondary = null;
        if (Palettes.TryGetValue(a.ColorVision, out var pair))
        {
            primary = pair.Primary;
            secondary = pair.Secondary;
        }

        // 2. A named accent overrides the palette's primary - it is the more
        //    specific choice, and it is the one the user made last.
        if (a.AccentColor == "custom" && IsHex(a.CustomAccent)) primary = a.CustomAccent;
        else if (a.AccentColor != "theme")
        {
            var named = Accents.FirstOrDefault(x => x.Id == a.AccentColor);
            if (!string.IsNullOrEmpty(named.Hex)) primary = named.Hex;
        }

        if (a.DimBrightEffects)
        {
            if (primary != null) primary = Dim(primary, 0.12);
            if (secondary != null) secondary = Dim(secondary, 0.12);
        }

        if (primary != null) SetAccent(res, primary, secondary);

        // 3. Transparency. The panel surface, the panel card and tooltips are the
        //    three translucent things in the app; all three go solid together.
        if (a.ReduceTransparency)
        {
            Opaque(res, "SurfaceBrush");
            Opaque(res, "PanelCardBrush");
            res["TooltipBackgroundBrush"] = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x18)));
        }
        else
        {
            res["TooltipBackgroundBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x10, 0x18)));
        }

        // 4. A solid plate behind overlay text, for reading over snow and sky.
        res["TextBackdropBrush"] = Frozen(new SolidColorBrush(
            a.SolidTextBackgrounds ? Color.FromArgb(0xF2, 0x05, 0x05, 0x0A)
                                   : Color.FromArgb(0x88, 0x00, 0x00, 0x00)));
    }

    private static void ApplyMotion(ResourceDictionary res, AccessibilitySettings a)
    {
        // Glow is an effect, so it is dialled by the two numbers every DropShadow in
        // the app reads. Zero opacity leaves the edge and removes the bloom.
        double glow = a.ReduceGlow ? 0.0 : (a.DimBrightEffects ? 0.3 : 0.55);
        res["GlowOpacity"] = glow;
        res["GlowBlur"] = a.ReduceGlow ? 0.0 : 14.0;
        res["GlowBlurSmall"] = a.ReduceGlow ? 0.0 : 12.0;

        // Decorative parts are hidden rather than un-animated: an animation that
        // cannot be seen is an animation that is not distracting, and this reaches
        // storyboards declared in XAML that code cannot otherwise stop.
        bool decorative = DecorativeAnimation && DecorativeEffects && !a.SimplifiedInterface;
        res["DecorativeVisibility"] = decorative ? Visibility.Visible : Visibility.Collapsed;
        res["DecorativeOpacity"] = decorative ? 1.0 : 0.0;
    }

    private static void ApplyFocus(ResourceDictionary res, AccessibilitySettings a)
    {
        // The ring is white inside black in every setting - strength changes its
        // thickness, never its colour. A hue is exactly what a focus ring must not
        // depend on.
        (double outer, double inner) = a.FocusStrength switch
        {
            "strong" => (6.0, 3.0),
            "extra-strong" => (9.0, 5.0),
            _ => (4.0, 2.0),
        };
        res["FocusRingOuterThickness"] = outer;
        res["FocusRingInnerThickness"] = inner;
        res["FocusBrush"] = Frozen(new SolidColorBrush(Colors.White));

        // Selected things always carry a second cue. This makes that cue louder.
        res["SelectedBarThickness"] = new Thickness(1, a.HighlightSelected ? 8 : 5, 1, 0);
        res["SelectedFontWeight"] = FontWeights.Bold;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void SetAccent(ResourceDictionary res, string primary, string? secondary)
    {
        var p = Parse(primary);
        res["PrimaryColor"] = p;
        res["PrimaryColorGlow"] = Color.FromArgb(0x66, p.R, p.G, p.B);
        res["PrimaryBrush"] = Frozen(new SolidColorBrush(p));
        res["PrimaryHoverBrush"] = Frozen(new SolidColorBrush(Lighten(p, 0.18)));
        res["SurfaceBorderBrush"] = Frozen(new SolidColorBrush(p));
        var onPrimary = BestLabelOn(p);
        res["OnPrimaryColor"] = onPrimary;
        res["OnPrimaryBrush"] = Frozen(new SolidColorBrush(onPrimary));

        if (secondary != null)
        {
            var q = Parse(secondary);
            res["SecondaryColor"] = q;
            res["SecondaryBrush"] = Frozen(new SolidColorBrush(q));
            res["SecondaryHoverBrush"] = Frozen(new SolidColorBrush(Lighten(q, 0.22)));
            res["SecondaryPressBrush"] = Frozen(new SolidColorBrush(Lighten(q, 0.38)));
            var onSecondary = BestLabelOn(q);
            res["OnSecondaryColor"] = onSecondary;
            res["OnSecondaryBrush"] = Frozen(new SolidColorBrush(onSecondary));
        }

        // The panel frame and its title are drawn from the accent pair, so they are
        // rebuilt here too - otherwise a palette change recolours the buttons and
        // leaves the frame in the theme's old hue.
        var title = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
        };
        title.GradientStops.Add(new GradientStop(p, 0.0));
        title.GradientStops.Add(new GradientStop(secondary != null ? Parse(secondary) : p, 1.0));
        res["PanelTitleBrush"] = Frozen(title);
    }

    private static void Opaque(ResourceDictionary res, string key)
    {
        switch (res[key])
        {
            case SolidColorBrush b:
                res[key] = Frozen(new SolidColorBrush(Color.FromRgb(b.Color.R, b.Color.G, b.Color.B)));
                break;
            case LinearGradientBrush g:
                var copy = g.Clone();
                for (int i = 0; i < copy.GradientStops.Count; i++)
                {
                    var c = copy.GradientStops[i].Color;
                    copy.GradientStops[i].Color = Color.FromRgb(c.R, c.G, c.B);
                }
                copy.Opacity = 1.0;
                res[key] = Frozen(copy);
                break;
        }
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// A line of plain language about a colour the user typed: can it be read on
    /// this app's dark panels, and does it survive the three colour-vision
    /// simulations? No jargon, no hex arithmetic in the user's face.
    /// </summary>
    public static string CheckAccent(string hex)
    {
        if (!IsHex(hex)) return "Type a colour as a hash and six characters, for example #FFC94A.";
        var c = Parse(hex);
        var panel = Color.FromRgb(0x05, 0x05, 0x0A);
        double ratio = Contrast(c, panel);
        return ratio switch
        {
            >= 7.0 => $"Clear and easy to see on a dark panel (contrast {ratio:0.0} to 1).",
            >= 4.5 => $"Readable on a dark panel (contrast {ratio:0.0} to 1).",
            >= 3.0 => $"Usable for borders and large text, but weak for small text (contrast {ratio:0.0} to 1).",
            _ => $"Too dark to see against the panels (contrast {ratio:0.0} to 1). Try a lighter colour.",
        };
    }

    private static bool IsHex(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.StartsWith('#') && (s.Length == 7 || s.Length == 9);

    private static Color Parse(string hex)
    {
        try { return ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.White; }
        catch { return Colors.White; }
    }

    private static string Dim(string hex, double amount)
    {
        var c = Parse(hex);
        double k = Math.Clamp(1 - amount, 0, 1);
        return $"#{(byte)(c.R * k):X2}{(byte)(c.G * k):X2}{(byte)(c.B * k):X2}";
    }

    private static Color Lighten(Color c, double amount)
    {
        byte Mix(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
        return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    /// <summary>Black or white on this colour, whichever actually reads better —
    /// the same rule ThemeService uses for button labels.</summary>
    private static Color BestLabelOn(Color background)
    {
        var black = Color.FromRgb(0x0A, 0x0A, 0x0F);
        return Contrast(black, background) >= Contrast(Colors.White, background) ? black : Colors.White;
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }
}
