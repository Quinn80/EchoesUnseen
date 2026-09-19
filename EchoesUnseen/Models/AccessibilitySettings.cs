namespace EchoesUnseen.Models;

/// <summary>
/// Global visual-accessibility preferences — the Accessibility tab in Settings.
///
/// TWO RULES SHAPE THIS WHOLE CLASS.
///
/// 1. ORGANISED BY WHAT SOMEONE NEEDS, NOT BY WHAT THEY HAVE. There is no glaucoma
///    setting and no macular-degeneration setting. Two people with the same
///    diagnosis often need opposite things, and nobody should have to name a
///    condition to make text bigger. Everything here is phrased as a need:
///    larger, higher contrast, less motion, stronger focus, where on the screen.
///
/// 2. THESE ARE GLOBAL PREFERENCES, NOT FEATURE SETTINGS. Whether the Hover Reader
///    is on lives in Features; how wide a trail is drawn lives in Trail Navigator.
///    This class says things like "reduce glow" and "my usable area is the left
///    half", which every feature can then respect. Nothing here is duplicated from
///    another tab.
///
/// A PROFILE IS A STARTING POINT, NEVER A LOCK. Choosing one writes a set of
/// sensible values and nothing more; changing any single setting afterwards moves
/// <see cref="Profile"/> to "custom" and never overwrites the change.
///
/// DEFAULTS MATCH b1.5 EXACTLY, so an existing settings file that has never seen
/// this class comes back looking and behaving exactly as it did before.
/// </summary>
public class AccessibilitySettings
{
    // ── Quick setup ─────────────────────────────────────────────────────────
    /// <summary>
    /// "standard" | "low-vision" | "high-contrast" | "color-vision" | "eye-comfort" |
    /// "screen-reader" | "custom". A starting point; see <c>AccessibilityService.Profiles</c>.
    /// </summary>
    public string Profile { get; set; } = "standard";

    // ── Readability and size ────────────────────────────────────────────────
    /// <summary>"standard" | "large" | "extra-large" | "custom" — panels, buttons, spacing.</summary>
    public string InterfaceScale { get; set; } = "standard";

    /// <summary>Used when <see cref="InterfaceScale"/> is "custom". Clamped to 0.85–1.60 —
    /// past about 1.6 the panels stop fitting a 1080p screen, which helps nobody.</summary>
    public double CustomInterfaceScale { get; set; } = 1.0;

    /// <summary>
    /// "standard" | "large" | "extra-large" | "custom". "custom" uses
    /// <see cref="AppSettings.FontSize"/> — the same number the old Font Size slider
    /// wrote, so nothing is duplicated.
    ///
    /// The default is "standard", which IS 22 point, i.e. exactly what
    /// <see cref="AppSettings.FontSize"/> defaults to. Anyone who had moved that
    /// slider is migrated to "custom" so their own size survives untouched.
    /// </summary>
    public string TextSize { get; set; } = "standard";

    /// <summary>Heavier weight for body text. Headings are already bold and are left alone:
    /// making everything heavy costs more legibility than it buys.</summary>
    public bool BoldText { get; set; }

    /// <summary>Bigger buttons, toggles, tabs and icon buttons — easier to hit, easier to see.</summary>
    public bool LargerControls { get; set; }

    /// <summary>"standard" | "large" | "extra-large" — tooltip text size.</summary>
    public string TooltipSize { get; set; } = "standard";

    /// <summary>Thicker, brighter edges around panels, fields and buttons.</summary>
    public bool StrongerBorders { get; set; }

    // ── Colour and contrast ─────────────────────────────────────────────────
    /// <summary>
    /// "standard" | "protanopia" | "deuteranopia" | "tritanopia" | "high-contrast" | "custom".
    /// This picks an accessible accent pair INSIDE the app — it is not a screen filter, and
    /// it never touches Guild Wars 2's own colours.
    /// </summary>
    public string ColorVision { get; set; } = "standard";

    /// <summary>Make panels, tooltips and overlays opaque instead of translucent.</summary>
    public bool ReduceTransparency { get; set; }

    /// <summary>Draw a solid plate behind overlay text so it stays readable over snow,
    /// sky, spell effects and dark ground.</summary>
    public bool SolidTextBackgrounds { get; set; }

    /// <summary>"theme" (the theme's own accent) or one of the named accents in
    /// <c>AccessibilityService.Accents</c>, or "custom" with <see cref="CustomAccent"/>.</summary>
    public string AccentColor { get; set; } = "theme";

    /// <summary>#RRGGBB used when <see cref="AccentColor"/> is "custom".</summary>
    public string CustomAccent { get; set; } = "#FFC94A";

    // ── Motion and eye comfort ──────────────────────────────────────────────
    /// <summary>Drop sliding, rising and scaling transitions; things appear instead of moving.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Stop indicators that breathe or pulse on a loop.</summary>
    public bool DisablePulsing { get; set; }

    /// <summary>Cut the neon bloom around buttons, panels and the wheel, keeping the edge.</summary>
    public bool ReduceGlow { get; set; }

    /// <summary>Stop decorative movement that carries no information — drifting particles,
    /// scanlines, spinning frames. A theme keeps its colours and its shape; it stops moving.</summary>
    public bool ReduceDecorativeAnimation { get; set; }

    /// <summary>Suppress quick flashes and flickers.</summary>
    public bool ReduceFlashing { get; set; }

    /// <summary>Tone down the brightest highlights in Echoes Unseen's own interface.
    /// It never darkens Guild Wars 2 itself.</summary>
    public bool DimBrightEffects { get; set; }

    // ── Focus and visibility ────────────────────────────────────────────────
    /// <summary>"standard" | "strong" | "extra-strong" — thickness of the white-in-black
    /// keyboard focus ring. The ring is never a hue, in any setting.</summary>
    public string FocusStrength { get; set; } = "standard";

    /// <summary>Extra non-colour marks on whatever is selected. Selected states ALWAYS carry
    /// a second cue; this adds a stronger one still.</summary>
    public bool HighlightSelected { get; set; }

    /// <summary>Fewer decorative flourishes, plainer grouping. Never hides functionality.</summary>
    public bool SimplifiedInterface { get; set; }

    /// <summary>Turn off ornamental effects while keeping the theme's identity — Matrix stays
    /// black and green, the rain stops.</summary>
    public bool HideDecorativeEffects { get; set; }

    // ── Preferred viewing area ──────────────────────────────────────────────
    /// <summary>
    /// "auto" | "center" | "left" | "right" | "top" | "bottom" | "custom".
    ///
    /// Where accessibility messages are easiest for this person to see. It is NOT the same
    /// as placing a particular overlay: an overlay the user has positioned by hand stays
    /// exactly where they put it. This is the hint new and future surfaces start from.
    /// </summary>
    public string ViewingArea { get; set; } = "auto";

    /// <summary>Custom area as a fraction of the screen (0–1), so it survives a change of
    /// resolution or monitor. Only used when <see cref="ViewingArea"/> is "custom".</summary>
    public double CustomAreaX { get; set; } = 0.25;
    public double CustomAreaY { get; set; } = 0.25;
    public double CustomAreaWidth { get; set; } = 0.50;
    public double CustomAreaHeight { get; set; } = 0.50;

    /// <summary>Deep copy — used by "reset this section" and by the settings tests.</summary>
    public AccessibilitySettings Clone() => (AccessibilitySettings)MemberwiseClone();
}
