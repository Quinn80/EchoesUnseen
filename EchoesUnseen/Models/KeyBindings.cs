namespace EchoesUnseen.Models;

/// <summary>
/// The app's rebindable global hotkeys. Registered with the Win32 RegisterHotKey
/// API at startup, so they work even while Guild Wars 2 has input focus.
///
/// Wheel NAVIGATION (Alt + arrow keys, to move around the ring) is intentionally
/// fixed and not listed here — it's the core scheme and rebinding four
/// directional keys individually adds confusion for little gain. Everything the
/// user might reasonably want to change lives below and is editable from
/// Settings → Keybinds.
/// </summary>
public class KeyBindings
{
    public string OpenSelected    { get; set; } = "Alt+Enter";
    public string ReadUnderCursor { get; set; } = "Ctrl+Shift+Space";
    public string StopSpeaking    { get; set; } = "Ctrl+Shift+S";
    public string ToggleHoverRead { get; set; } = "Ctrl+Shift+R";
    public string RecenterHud     { get; set; } = "Ctrl+Shift+H";
    public string Quit            { get; set; } = "Ctrl+Shift+Q";

    /// <summary>One editable hotkey: its label, help text, and get/set access.</summary>
    public sealed class Entry
    {
        public string Label { get; }
        public string Description { get; }
        public Func<string> Get { get; }
        public Action<string> Set { get; }
        public Entry(string label, string description, Func<string> get, Action<string> set)
        {
            Label = label; Description = description; Get = get; Set = set;
        }
    }

    /// <summary>The editable hotkeys, for the Settings Keybinds list.</summary>
    public IReadOnlyList<Entry> Editable() => new[]
    {
        new Entry("Open selected tool",
            "Opens the wheel tool you've moved to with Alt and the arrow keys.",
            () => OpenSelected, v => OpenSelected = v),
        new Entry("Read what's under the pointer",
            "Reads the screen around the mouse — item tooltips, menu buttons, list rows.",
            () => ReadUnderCursor, v => ReadUnderCursor = v),
        new Entry("Stop speech",
            "Silences the voice immediately.",
            () => StopSpeaking, v => StopSpeaking = v),
        new Entry("Toggle hover to read",
            "Turns speaking whatever the mouse rests on on or off.",
            () => ToggleHoverRead, v => ToggleHoverRead = v),
        new Entry("Recenter wheel",
            "Snaps the wheel back to the middle of the screen if it gets lost.",
            () => RecenterHud, v => RecenterHud = v),
        new Entry("Quit Echoes Unseen",
            "Closes the app (the overlay can't be closed with Alt F4).",
            () => Quit, v => Quit = v),
    };

    /// <summary>Fixed, non-editable shortcuts, shown for reference only.</summary>
    public static IReadOnlyList<(string Keys, string Action)> FixedInfo { get; } = new[]
    {
        ("Alt + Arrow keys", "Move around the wheel (the voice names each tool)"),
        ("Ctrl + Shift + Arrow keys", "Move the wheel itself around the screen"),
        ("Shift + drag  /  Middle-drag", "Move the wheel with the mouse"),
        ("Escape", "Close the open panel"),
    };
}
