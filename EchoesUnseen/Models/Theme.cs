namespace EchoesUnseen.Models;

/// <summary>
/// A complete UI color theme.
///
/// All ten themes are defined in <see cref="EchoesUnseen.Services.ThemeService"/>
/// and can be switched at runtime via Settings > HUD > Theme.
///
/// Colors are stored as hex strings in #AARRGGBB or #RRGGBB format so they
/// serialize cleanly to JSON and are easy for humans to tweak.
///
/// WHICH KEYS EXIST:
///   Every XAML file references these color keys by name (e.g. PrimaryColor,
///   PrimaryBrush). Changing the theme updates all of them at once and every
///   panel — plus the radial HUD — reflects the change immediately.
/// </summary>
public class Theme
{
    /// <summary>Identifier, used in settings (e.g. "hot-pink", "cyberpunk").</summary>
    public string Id { get; set; } = "hot-pink";

    /// <summary>Human-readable name shown in the Settings dropdown.</summary>
    public string Name { get; set; } = "Hot Pink (Default)";

    /// <summary>One-line description shown as a hint under the theme picker.</summary>
    public string Description { get; set; } = "";

    // ── Color palette ────────────────────────────────────────────────────────
    /// <summary>Primary accent. Buttons, ring, highlights.</summary>
    public string Primary { get; set; } = "#FF1A8A";

    /// <summary>Secondary accent. Darker shade of primary for subtle elements.</summary>
    public string Secondary { get; set; } = "#A41435";

    /// <summary>Main app/window background. Should be near-black for dark mode.</summary>
    public string Background { get; set; } = "#0A0A0F";

    /// <summary>Card/panel surface. Semi-transparent for the HUD backdrop.</summary>
    public string Surface { get; set; } = "#CC14141C";

    /// <summary>Border around panels. Usually matches primary.</summary>
    public string SurfaceBorder { get; set; } = "#FF1A8A";

    /// <summary>Foreground text on dark backgrounds.</summary>
    public string Foreground { get; set; } = "#FFFFFF";

    /// <summary>Muted text (hints, secondary info).</summary>
    public string Muted { get; set; } = "#B8B8C8";

    /// <summary>Semantic colors — usually consistent across themes for accessibility.</summary>
    public string Success { get; set; } = "#4ADE80";
    public string Warning { get; set; } = "#FACC15";
    public string Error { get; set; } = "#EF4444";

    /// <summary>Per-theme earcon sound palette (panel open/close, startup, hover).</summary>
    public ThemeSound Sound { get; set; } = new();
}

/// <summary>
/// Each theme carries its own set of soft sine-tone earcons, so switching theme
/// changes how the app SOUNDS as well as looks. Frequencies are in Hz; the
/// EarconService renders them into gentle fading chords. Kept as plain numbers
/// so themes stay pure data (no audio files, all generated locally).
/// </summary>
public class ThemeSound
{
    /// <summary>Notes of the panel-open swell (fades in).</summary>
    public double[] Open { get; set; } = { 220.00, 329.63 };
    /// <summary>Notes of the panel-close cue (fades out).</summary>
    public double[] Close { get; set; } = { 329.63, 220.00 };
    /// <summary>Rising arpeggio played once on launch.</summary>
    public double[] Startup { get; set; } = { 146.83, 293.66, 440.00, 739.99 };
    /// <summary>Single soft tone when the pointer enters the app surface.</summary>
    public double HoverIn { get; set; } = 523.25;
    /// <summary>Single soft tone when the pointer leaves the app surface.</summary>
    public double HoverOut { get; set; } = 392.00;
}
