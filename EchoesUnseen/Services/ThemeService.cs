using System.Windows;
using System.Windows.Media;
using EchoesUnseen.Models;

namespace EchoesUnseen.Services;

/// <summary>
/// Theme catalog and live-switching service.
///
/// HOW IT WORKS:
///   Every panel's XAML references colors via DynamicResource (not
///   StaticResource), pointing at keys like "PrimaryBrush", "BackgroundBrush",
///   etc. When the user picks a new theme in Settings, <see cref="Apply"/>
///   updates those exact resource keys in <c>Application.Current.Resources</c>.
///   WPF's DynamicResource binding picks up the changes automatically — every
///   open panel and the HUD re-render with new colors without any reload.
///
/// ALL THEMES ARE DARK:
///   Backgrounds are near-black (#0A0A0F to #151515), foregrounds are white
///   with lighter muted text (#A8A8B8 to #D8D8D8). We never render text on
///   light backgrounds. The user explicitly opted out of light mode.
///
/// TO ADD A THEME:
///   Add a new Theme object to <see cref="BuiltInThemes"/>. That's it. The
///   Settings panel will list it automatically and users can switch to it.
/// </summary>
public static class ThemeService
{
    // ════════════════════════════════════════════════════════════════════════
    // 10 DARK THEMES — each distinct in mood, all with full contrast
    // ════════════════════════════════════════════════════════════════════════
    // 7 THEMES — each a distinct look AND a distinct SOUND. The Sound palette
    // drives the panel open/close, startup and hover earcons, so switching theme
    // re-tunes the whole app. Frequencies climb from deep/warm themes to
    // bright/crystalline ones.
    public static readonly List<Theme> BuiltInThemes = new()
    {
        new Theme
        {
            Id = "neon-void",
            Name = "Neon Void",
            Description = "Magenta and cyan on pure black. The signature cyberpunk look.",
            Primary = "#FF2EC4", Secondary = "#00E5FF",
            Background = "#050208", Surface = "#CC120818", SurfaceBorder = "#FF2EC4",
            Foreground = "#FFEFFA", Muted = "#C89AD8",
            Sound = new ThemeSound
            {
                Open = new[] { 587.33, 880.00 }, Close = new[] { 880.00, 587.33 },
                Startup = new[] { 293.66, 440.00, 587.33, 880.00 },
                HoverIn = 987.77, HoverOut = 659.25,
            },
        },

        new Theme
        {
            Id = "ember-forge",
            Name = "Ember Forge",
            Description = "Molten orange and deep red on charcoal. Warm and heavy.",
            Primary = "#FF6A1A", Secondary = "#B21E0C",
            Background = "#140805", Surface = "#CC241009", SurfaceBorder = "#FF6A1A",
            Foreground = "#FFF3EC", Muted = "#D8A98C",
            Sound = new ThemeSound
            {
                Open = new[] { 130.81, 196.00 }, Close = new[] { 196.00, 130.81 },
                Startup = new[] { 98.00, 130.81, 196.00, 261.63 },
                HoverIn = 261.63, HoverOut = 196.00,
            },
        },

        new Theme
        {
            Id = "frost-bloom",
            Name = "Frost Bloom",
            Description = "Icy cyan and white on deep navy. Crisp and crystalline.",
            Primary = "#7FE9FF", Secondary = "#2E9BD6",
            Background = "#06111F", Surface = "#CC0C1F33", SurfaceBorder = "#7FE9FF",
            Foreground = "#F0FBFF", Muted = "#A8CEE0",
            Sound = new ThemeSound
            {
                Open = new[] { 659.25, 987.77 }, Close = new[] { 987.77, 659.25 },
                Startup = new[] { 493.88, 659.25, 987.77, 1318.51 },
                HoverIn = 1318.51, HoverOut = 880.00,
            },
        },

        new Theme
        {
            Id = "verdant",
            Name = "Verdant",
            Description = "Emerald and lime on black-green. Calm and organic.",
            Primary = "#3EE686", Secondary = "#12924E",
            Background = "#04140B", Surface = "#CC0A2213", SurfaceBorder = "#3EE686",
            Foreground = "#ECFFF3", Muted = "#9AD8B4",
            Sound = new ThemeSound
            {
                Open = new[] { 220.00, 329.63 }, Close = new[] { 329.63, 220.00 },
                Startup = new[] { 164.81, 220.00, 329.63, 440.00 },
                HoverIn = 440.00, HoverOut = 329.63,
            },
        },

        new Theme
        {
            Id = "royal-amethyst",
            Name = "Royal Amethyst",
            Description = "Amethyst and violet on charcoal. Regal and rich.",
            Primary = "#B36BFF", Secondary = "#6B21C8",
            Background = "#0E0818", Surface = "#CC1A1030", SurfaceBorder = "#B36BFF",
            Foreground = "#F6EFFF", Muted = "#C4A8E0",
            Sound = new ThemeSound
            {
                Open = new[] { 261.63, 392.00 }, Close = new[] { 392.00, 261.63 },
                Startup = new[] { 196.00, 261.63, 392.00, 523.25 },
                HoverIn = 523.25, HoverOut = 392.00,
            },
        },

        new Theme
        {
            Id = "solar-gold",
            Name = "Solar Gold",
            Description = "Gold and warm white on dark bronze. Majestic.",
            Primary = "#FFC94A", Secondary = "#B8860B",
            Background = "#14100A", Surface = "#CC241C0E", SurfaceBorder = "#FFC94A",
            Foreground = "#FFF9EC", Muted = "#D8C79A",
            Sound = new ThemeSound
            {
                Open = new[] { 174.61, 261.63 }, Close = new[] { 261.63, 174.61 },
                Startup = new[] { 130.81, 174.61, 261.63, 349.23 },
                HoverIn = 349.23, HoverOut = 261.63,
            },
        },

        new Theme
        {
            Id = "abyss-blue",
            Name = "Abyss Blue",
            Description = "Electric blue and teal on near-black. Deep and oceanic.",
            Primary = "#2E7BFF", Secondary = "#0E5AA8",
            Background = "#030812", Surface = "#CC0A1428", SurfaceBorder = "#2E7BFF",
            Foreground = "#EFF4FF", Muted = "#9AB4E0",
            Sound = new ThemeSound
            {
                Open = new[] { 110.00, 164.81 }, Close = new[] { 164.81, 110.00 },
                Startup = new[] { 82.41, 110.00, 164.81, 220.00 },
                HoverIn = 293.66, HoverOut = 220.00,
            },
        },
    };

    // ── Current theme tracking ───────────────────────────────────────────────
    private static Theme _currentTheme = BuiltInThemes[0];

    // Tracks whether High Contrast is active, so Apply() knows not to overwrite
    // the black/white HC panel chrome with theme-derived gradients.
    private static bool s_highContrastOn;

    /// <summary>Fires when the theme changes so subscribers can re-render custom visuals.</summary>
    public static event EventHandler<Theme>? ThemeChanged;

    /// <summary>The theme currently in effect.</summary>
    public static Theme Current => _currentTheme;

    /// <summary>Look up a theme by id. Falls back to the default if the id isn't known.</summary>
    public static Theme GetById(string id)
    {
        return BuiltInThemes.FirstOrDefault(t => t.Id == id) ?? BuiltInThemes[0];
    }

    // ── Apply theme at runtime ───────────────────────────────────────────────
    /// <summary>
    /// v21: apply EVERYTHING the current settings describe — theme colors,
    /// global font size, and (if enabled) the high-contrast overrides. This is
    /// the single entry point App and Settings changes should call; it is
    /// idempotent and safe to call repeatedly.
    /// Order matters: theme first (resets all standard keys), then fonts,
    /// then high contrast LAST so its overrides win.
    /// </summary>
    public static void ApplyCurrent(Models.AppSettings s)
    {
        Apply(GetById(s.ThemeId));
        ApplyFontSize(s.FontSize);
        ApplyHighContrast(s.HighContrast);
        // LAST, deliberately: the Vision Accessibility Suite overrides the theme,
        // the font size and High Contrast, because what somebody needs in order to
        // see the interface outranks how the interface prefers to look. See
        // AccessibilityService for the precedence rule in full.
        AccessibilityService.Apply(s);
    }

    /// <summary>Set the global body text size (and the small hint size that
    /// tracks it). Every panel picks this up live via DynamicResource.</summary>
    public static void ApplyFontSize(double size)
    {
        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;
        size = Math.Clamp(size, 14, 32);
        res["GlobalFontSize"] = size;
        res["GlobalFontSizeSmall"] = Math.Max(11.0, size - 10.0);
    }

    /// <summary>
    /// v21: real high-contrast mode. ON: pure white text on true black
    /// surfaces, white borders, white focus outline — information never
    /// carried by color alone. OFF: restores the active theme's colors and
    /// the ornate panel chrome (the user's saved theme is never erased —
    /// this only overrides the live resources).
    /// </summary>
    public static void ApplyHighContrast(bool on)
    {
        s_highContrastOn = on;
        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;

        if (on)
        {
            SetColor(res, "ForegroundColor", "#FFFFFF");
            SetColor(res, "MutedColor", "#F0F0F0");
            SetColor(res, "BackgroundColor", "#000000");
            SetColor(res, "SurfaceColor", "#000000");
            SetColor(res, "SurfaceBorderColor", "#FFFFFF");
            SetBrush(res, "ForegroundBrush", "#FFFFFF");
            SetBrush(res, "MutedBrush", "#F0F0F0");
            SetBrush(res, "BackgroundBrush", "#000000");
            SetBrush(res, "SurfaceBrush", "#000000");
            SetBrush(res, "SurfaceBorderBrush", "#FFFFFF");
            SetBrush(res, "FocusBrush", "#FFFFFF");   // same ring as every other mode

            // Panel chrome: solid black card, solid white frame, white title.
            res["PanelFiligreeBrush"] = Frozen(new SolidColorBrush(ParseColor("#FFFFFF")));
            res["PanelCardBrush"] = Frozen(new SolidColorBrush(ParseColor("#000000")));
            res["PanelTitleBrush"] = Frozen(new SolidColorBrush(ParseColor("#FFFFFF")));
        }
        else
        {
            // Standard keys were already restored by Apply(theme); put the
            // ornate panel chrome back. The focus ring stays WHITE - see Apply().
            SetBrush(res, "FocusBrush", "#FFFFFF");

            var filigree = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
            };
            filigree.GradientStops.Add(new GradientStop(ParseColor("#FF6C7AE5"), 0.0));
            filigree.GradientStops.Add(new GradientStop(ParseColor("#FF1B275F"), 0.35));
            filigree.GradientStops.Add(new GradientStop(ParseColor("#FFE472DC"), 0.65));
            filigree.GradientStops.Add(new GradientStop(ParseColor("#FF122555"), 1.0));
            res["PanelFiligreeBrush"] = Frozen(filigree);

            var card = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
            };
            card.GradientStops.Add(new GradientStop(ParseColor("#FF02030C"), 0.0));
            card.GradientStops.Add(new GradientStop(ParseColor("#FF010105"), 1.0));
            res["PanelCardBrush"] = Frozen(card);

            var title = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0),
            };
            title.GradientStops.Add(new GradientStop(ParseColor("#FF47C9FF"), 0.0));
            title.GradientStops.Add(new GradientStop(ParseColor("#FFFF94FF"), 1.0));
            res["PanelTitleBrush"] = Frozen(title);
        }
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// Apply the given theme across the whole running app. Safe to call
    /// repeatedly and safe to call before the UI has built any panels.
    /// </summary>
    public static void Apply(Theme theme)
    {
        _currentTheme = theme;
        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;

        // Update every color / brush key that the theme dictionary owns.
        // DynamicResource bindings in the XAML pick these up automatically.
        SetColor(res, "PrimaryColor",        theme.Primary);
        SetColor(res, "PrimaryColorGlow",    WithAlpha(theme.Primary, 0x66));
        SetColor(res, "SecondaryColor",      theme.Secondary);
        SetColor(res, "BackgroundColor",     theme.Background);
        SetColor(res, "SurfaceColor",        theme.Surface);
        SetColor(res, "SurfaceBorderColor",  theme.SurfaceBorder);
        SetColor(res, "ForegroundColor",     theme.Foreground);
        SetColor(res, "MutedColor",          theme.Muted);
        SetColor(res, "SuccessColor",        theme.Success);
        SetColor(res, "WarningColor",        theme.Warning);
        SetColor(res, "ErrorColor",          theme.Error);

        // Text/icon colour to sit ON the accent. Themes range from deep blue to
        // bright gold, so a fixed white foreground was unreadable on the light
        // accents (Solar Gold, Frost Bloom, Verdant). Pick black or white by the
        // accent's luminance so filled buttons stay legible in every theme.
        var onPrimary = BestLabelOn(theme.Primary);
        SetColor(res, "OnPrimaryColor", onPrimary);
        SetBrush(res, "OnPrimaryBrush", onPrimary);

        // The SECONDARY button was a fixed near-black in every theme - "#E61A1A24" -
        // so choosing Ember Forge or Verdant recoloured the panel around it and left a
        // row of dead grey slabs in the middle. It is the theme's own second colour now,
        // with its label picked black or white by luminance so a bright secondary
        // (Neon Void's cyan) stays as readable as a deep one (Ember Forge's red).
        var onSecondary = BestLabelOn(theme.Secondary);
        SetColor(res, "OnSecondaryColor", onSecondary);
        SetBrush(res, "OnSecondaryBrush", onSecondary);
        SetBrush(res, "SecondaryHoverBrush", Lighten(theme.Secondary, 0.22));
        SetBrush(res, "SecondaryPressBrush", Lighten(theme.Secondary, 0.38));
        SetBrush(res, "PrimaryHoverBrush",   Lighten(theme.Primary, 0.18));

        SetBrush(res, "PrimaryBrush",        theme.Primary);
        SetBrush(res, "SecondaryBrush",      theme.Secondary);
        SetBrush(res, "BackgroundBrush",     theme.Background);
        SetBrush(res, "SurfaceBrush",        theme.Surface);
        SetBrush(res, "SurfaceBorderBrush",  theme.SurfaceBorder, opacity: 0.6);
        SetBrush(res, "ForegroundBrush",     theme.Foreground);
        SetBrush(res, "MutedBrush",          theme.Muted);
        SetBrush(res, "SuccessBrush",        theme.Success);
        SetBrush(res, "WarningBrush",        theme.Warning);
        SetBrush(res, "ErrorBrush",          theme.Error);

        // ── Panel chrome, themed ────────────────────────────────────────────
        // The pop-up frame (ornate border, card fill, glowing title) used fixed
        // blue/pink gradients in every theme, so "everything changes with the
        // theme" wasn't true. Rebuild all three from the theme's own colours so
        // the whole panel — not just its contents — recolours on theme change.
        // High Contrast overrides these afterwards when it's on.
        if (!s_highContrastOn)
        {
            var filigree = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 1),
            };
            filigree.GradientStops.Add(new GradientStop(ParseColor(theme.Primary), 0.0));
            filigree.GradientStops.Add(new GradientStop(ParseColor(Darken(theme.Secondary, 0.35)), 0.35));
            filigree.GradientStops.Add(new GradientStop(ParseColor(theme.Secondary), 0.65));
            filigree.GradientStops.Add(new GradientStop(ParseColor(Darken(theme.Primary, 0.45)), 1.0));
            res["PanelFiligreeBrush"] = Frozen(filigree);

            var card = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
            };
            // A near-black card, but tinted toward the theme background so gold,
            // green and blue themes each read differently instead of all-black.
            card.GradientStops.Add(new GradientStop(ParseColor(Darken(theme.Background, 0.10)), 0.0));
            card.GradientStops.Add(new GradientStop(ParseColor(Darken(theme.Background, 0.55)), 1.0));
            res["PanelCardBrush"] = Frozen(card);

            var title = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(1, 0),
            };
            title.GradientStops.Add(new GradientStop(ParseColor(theme.Primary), 0.0));
            title.GradientStops.Add(new GradientStop(ParseColor(theme.Secondary), 1.0));
            res["PanelTitleBrush"] = Frozen(title);

            // NOT theme.Primary. The focus ring used to be the theme accent, which
            // meant that on an accent-filled button - every primary button in the
            // app - the ring and the button were the same colour: 1:1 contrast,
            // invisible, in every theme. It is white inside a black halo now, a
            // brightness edge with no hue at all, so keyboard focus is equally
            // visible in Solar Gold, Abyss Blue and to any kind of colour vision.
            SetBrush(res, "FocusBrush", "#FFFFFF");
        }

        ThemeChanged?.Invoke(null, theme);
    }

    /// <summary>Darken a hex colour toward black by <paramref name="amount"/> (0..1).
    /// Preserves the alpha channel. Used to derive panel-chrome gradients from the
    /// theme's own accent/background colours.</summary>
    private static string Darken(string hex, double amount)
    {
        try
        {
            var c = ParseColor(hex);
            double k = Math.Clamp(1.0 - amount, 0, 1);
            byte r = (byte)(c.R * k), g = (byte)(c.G * k), b = (byte)(c.B * k);
            return $"#{c.A:X2}{r:X2}{g:X2}{b:X2}";
        }
        catch { return hex; }
    }

    /// <summary>
    /// True if a colour is bright enough that black text reads better on it than
    /// white. Uses the standard sRGB luminance weights (green dominates human
    /// brightness perception), so gold and pale cyan count as "light" while a
    /// saturated blue does not.
    /// </summary>
    /// <summary>
    /// Black or white on this colour - whichever actually reads better.
    ///
    /// This used to be a luminance threshold: lighter than half, use black. That looks
    /// reasonable and quietly fails, because luminance near the boundary does not tell
    /// you which way the contrast falls. Ember Forge's orange sits just under the line,
    /// so it took WHITE text and came out at 2.87 to 1 - below even the large-text
    /// minimum, on a button in an app for people who cannot see well.
    ///
    /// Computing both and keeping the better one raises the worst button in any theme
    /// from 2.87 to 4.94, so every one now clears AA.
    /// </summary>
    private static string BestLabelOn(string background)
    {
        const string Black = "#0A0A0F", White = "#FFFFFF";
        return Contrast(Black, background) >= Contrast(White, background) ? Black : White;
    }

    /// <summary>WCAG contrast ratio between two colours, 1:1 to 21:1.</summary>
    private static double Contrast(string a, string b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            static double Ch(byte v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        catch { return 0.5; }
    }

    /// <summary>Mix a colour toward white. Used for hover and press states so they lift
    /// off the base colour instead of jumping to a fixed grey that belongs to no theme.</summary>
    private static string Lighten(string hex, double amount)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            byte Mix(byte v) => (byte)Math.Clamp(v + (255 - v) * amount, 0, 255);
            return $"#{Mix(c.R):X2}{Mix(c.G):X2}{Mix(c.B):X2}";
        }
        catch { return hex; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static void SetColor(ResourceDictionary res, string key, string hex)
    {
        res[key] = ParseColor(hex);
    }

    private static void SetBrush(ResourceDictionary res, string key, string hex, double opacity = 1.0)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        if (opacity < 1.0) brush.Opacity = opacity;
        brush.Freeze();
        res[key] = brush;
    }

    /// <summary>Parse either #RRGGBB or #AARRGGBB hex. Invalid input falls back to white.</summary>
    private static Color ParseColor(string hex)
    {
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            return obj is Color c ? c : Colors.White;
        }
        catch { return Colors.White; }
    }

    /// <summary>Return the color with a new alpha channel. Input is #RRGGBB or #AARRGGBB.</summary>
    private static string WithAlpha(string hex, byte alpha)
    {
        var c = ParseColor(hex);
        return $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
