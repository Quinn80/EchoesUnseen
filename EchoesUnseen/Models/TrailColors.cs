namespace EchoesUnseen.Models;

/// <summary>
/// Color-coding for each type of map objective in the Trail Navigator panel.
/// Colors stored as hex strings (e.g. "#60A5FA") so they serialize cleanly to JSON.
///
/// Five presets are exposed in the Audio settings tab:
///   Default, High Contrast, Deuteranopia (green-blind), Protanopia (red-blind), Monochrome
/// </summary>
public class TrailColors
{
    public string Waypoints  { get; set; } = "#60A5FA"; // blue
    public string Vistas     { get; set; } = "#4ADE80"; // green
    public string POIs       { get; set; } = "#FACC15"; // yellow
    public string HeroPoints { get; set; } = "#C084FC"; // purple
    public string Hearts     { get; set; } = "#F87171"; // red-pink

    public static TrailColors Preset(string name) => name switch
    {
        "High Contrast"                 => new() { Waypoints="#00FFFF", Vistas="#00FF00", POIs="#FFFF00", HeroPoints="#FF00FF", Hearts="#FF0000" },
        "Deuteranopia (Green-Blind)"    => new() { Waypoints="#0077BB", Vistas="#EE7733", POIs="#FFDD44", HeroPoints="#CC3399", Hearts="#EE3377" },
        "Protanopia (Red-Blind)"        => new() { Waypoints="#4477AA", Vistas="#66CCEE", POIs="#EEDD88", HeroPoints="#AA3377", Hearts="#EE6677" },
        "Monochrome"                    => new() { Waypoints="#FFFFFF", Vistas="#CCCCCC", POIs="#999999", HeroPoints="#666666", Hearts="#333333" },
        _                               => new() // Default
    };
}
