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
    public string QuietMode       { get; set; } = "Ctrl+Shift+Z";
    /// <summary>A bare F-key for the same reason as RecordBug: Ctrl+Shift+O was already
    /// claimed by other software on the test machine and never registered, so reading the
    /// objective silently did nothing for weeks.</summary>
    public string ReadObjective   { get; set; } = "F8";
    public string GuideDirection  { get; set; } = "Ctrl+Shift+G";
    public string ToggleVoiceGuide{ get; set; } = "Ctrl+Shift+V";
    public string CopyWaypoint    { get; set; } = "Ctrl+Shift+W";
    public string NextEvents      { get; set; } = "Ctrl+Shift+E";
    public string ToggleTwistCue  { get; set; } = "Ctrl+Shift+P";
    public string ReadBags        { get; set; } = "Ctrl+Shift+B";
    public string ReadWallet      { get; set; } = "Ctrl+Shift+M";
    public string GuideSelfCheck  { get; set; } = "Ctrl+Shift+D";
    /// <summary>A bare F-key, because the combinations kept losing.
    ///
    /// Ctrl+Shift+X and Ctrl+Alt+B were both already claimed by other software on the
    /// test machine and silently never registered. F9 is claimed by almost nothing, is
    /// unbound in Guild Wars 2 by default, and needs one finger - which matters when the
    /// thing you are trying to record is happening while you are moving.</summary>
    public string RecordBug       { get; set; } = "F9";
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
        new Entry("Quiet mode (hush while playing)",
            "Silences the chat reader and awareness announcements while you play, without turning anything off. Press again to resume. Manual reads still work.",
            () => QuietMode, v => QuietMode = v),
        new Entry("Read my current objective",
            "Reads the story / event objective tracker in the top-right of the screen, so you know your current goal and next step.",
            () => ReadObjective, v => ReadObjective = v),
        new Entry("Which way to my objective (clock check)",
            "Says the direction and distance to your guided target, like 'objective at 2 o'clock, 40 metres', using the way your camera is facing as 12 o'clock.",
            () => GuideDirection, v => GuideDirection = v),
        new Entry("Turn-by-turn voice guide on/off",
            "Turns spoken turn-by-turn steering on or off — 'turn left, straight ahead, go forward' as you move toward your target.",
            () => ToggleVoiceGuide, v => ToggleVoiceGuide = v),
        new Entry("Copy nearest waypoint code",
            "Copies the nearest waypoint's chat link to the clipboard — paste it into GW2 chat and click to travel, no map-clicking needed.",
            () => CopyWaypoint, v => CopyWaypoint = v),
        new Entry("What events are coming up",
            "Speaks the next few world bosses / meta events and how long until each starts.",
            () => NextEvents, v => NextEvents = v),
        new Entry("Forward-sense hum on/off",
            "Turns the camera-vs-body friction hum on or off — a soft sound that grows when your view twists away from where you're running, silent when aligned.",
            () => ToggleTwistCue, v => ToggleTwistCue = v),
        new Entry("Check the guide is pointing right",
            "Says where you are, which way you're facing, where the target is and the direction to it — and writes the same to the log, so a wrong direction can be diagnosed instead of guessed at.",
            () => GuideSelfCheck, v => GuideSelfCheck = v),
        new Entry("Record a bug report",
            "Starts a short recording of the whole screen INCLUDING the overlay, and says so out loud. Press again to stop, or it stops itself after two minutes. Saves one zip file to your Downloads with the pictures and the app's own log inside, ready to send.",
            () => RecordBug, v => RecordBug = v),
        new Entry("Read my money",
            "Speaks exactly how much gold, silver and copper you have, straight from your account rather than by reading the screen - so it is right to the copper and needs no pointing at anything. Needs an API key.",
            () => ReadWallet, v => ReadWallet = v),
        new Entry("Read my bag space",
            "Speaks how full your current character's bags are and what's in them by rarity — free slots, exotics, rares and so on. Needs an API key.",
            () => ReadBags, v => ReadBags = v),
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
