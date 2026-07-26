using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Services;

/// <summary>
/// Speaks whatever the mouse pointer is resting on — a hover-to-hear layer for
/// low-vision users, so exploring the interface with the mouse reads aloud the
/// same way keyboard focus already does.
///
/// HOW IT WORKS
///   Attaches a PreviewMouseMove handler to the main window. When the pointer
///   settles on a NEW element for a short dwell (so quick passes don't chatter),
///   the element's accessible text is spoken via the shared TtsService.
///
///   Because the overlay is click-through whenever no panel is open, mouse
///   events only reach us while a panel IS open — which is exactly when there's
///   readable content under the cursor. The HUD ring itself is a WebView-free
///   native visual now, so its buttons read too when hovered during interaction.
///
/// TEXT SELECTION (in priority order)
///   1. An explicit AutomationProperties.Name (what screen readers would say).
///   2. A TextBlock's text, a ContentControl's string content, a TextBox's text.
///   3. Otherwise walk up the visual tree until something readable is found.
///
/// Respects the HoverToRead setting and can be toggled live (Ctrl+Shift+R).
/// </summary>
public sealed class HoverReader
{
    private readonly Window _window;
    private readonly TtsService _tts;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _dwell;

    private DependencyObject? _pending;
    private string? _lastSpoken;

    public HoverReader(Window window, TtsService tts, SettingsService settings)
    {
        _window = window;
        _tts = tts;
        _settings = settings;

        _dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _dwell.Tick += OnDwellElapsed;

        _window.PreviewMouseMove += OnMouseMove;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_settings.Current.HoverToRead) return;

        var hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;

        // Only restart the dwell when the pointer moves to a DIFFERENT element,
        // so resting still fires exactly once.
        if (ReferenceEquals(hit, _pending)) return;
        _pending = hit;
        _dwell.Stop();
        _dwell.Start();
    }

    private void OnDwellElapsed(object? sender, EventArgs e)
    {
        _dwell.Stop();
        if (!_settings.Current.HoverToRead || _pending == null) return;

        // Don't talk over a deliberate read (e.g. the Oracle reading an article,
        // or the startup greeting). Hover cues are for quiet exploration, so if
        // speech is already in progress we simply stay out of the way.
        if (_tts.IsSpeaking) return;

        var text = FindReadableText(_pending);
        if (string.IsNullOrWhiteSpace(text)) return;

        // Don't repeat the same phrase if the pointer wanders within one control.
        if (string.Equals(text, _lastSpoken, StringComparison.Ordinal)) return;
        _lastSpoken = text;

        try { _ = _tts.SpeakAsync(text); }
        catch (Exception ex) { CrashLogger.Log("HoverReader.Speak", ex); }
    }

    // Container-level names we never speak on hover: the overlay window and the
    // wheel itself. Hovering the ring background used to repeat "Radial HUD with
    // 12 panel buttons" endlessly; now the wheel gives a soft audio fade-in/out
    // cue instead (see RadialHud.OnCursorEntered/LeftHud), and hovering an actual
    // gem still reads that gem's panel name.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Radial HUD with 12 panel buttons",
        "Echoes Unseen accessibility overlay for Guild Wars 2",
        "Open panel",
    };

    /// <summary>Walk up from the hovered node until something has readable text.</summary>
    private static string? FindReadableText(DependencyObject? node)
    {
        for (int depth = 0; node != null && depth < 12; depth++)
        {
            if (node is UIElement ue)
            {
                var name = AutomationProperties.GetName(ue);
                if (!string.IsNullOrWhiteSpace(name))
                    return SkipNames.Contains(name.Trim()) ? null : Trim(name);
            }

            switch (node)
            {
                case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                    return Trim(tb.Text);
                case System.Windows.Controls.TextBox box when !string.IsNullOrWhiteSpace(box.Text):
                    return Trim(box.Text);
                case ContentControl cc when cc.Content is string s && !string.IsNullOrWhiteSpace(s):
                    return Trim(s);
            }

            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // Keep spoken hover cues short so they don't monopolise the voice; long
    // article text is read deliberately by the Oracle, not by a hover.
    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    public void Detach() => _window.PreviewMouseMove -= OnMouseMove;
}
