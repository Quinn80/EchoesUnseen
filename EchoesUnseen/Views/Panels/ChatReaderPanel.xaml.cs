using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Chat Reader — continuously OCRs the GW2 chat region and speaks new lines.
///
/// WORKFLOW:
///   1. User clicks "Set Chat Region" to drag-select the chat area
///   2. User clicks "Start Reading" to begin polling
///   3. Every N seconds (default 3.5), we capture the region, OCR it, and
///      compare line-by-line against a seen-set
///   4. The FIRST scan seeds the seen-set silently (so we don't scream out
///      the entire chat history the instant they enable it)
///   5. Subsequent scans speak the newest M lines (default 3) that weren't
///      in the seen-set
///
/// SEEN-SET MANAGEMENT:
///   HashSet&lt;string&gt; of raw OCR'd lines. Cap at 500 entries; trim to
///   the most recent 200 when that's exceeded. This prevents unbounded growth
///   during long play sessions while keeping enough history to avoid double-
///   reading when the chat scrolls a line back into view.
///
/// ARCHITECTURAL NOTES:
///   Uses the same SelectionOverlayWindow pattern as Screen Reader — no
///   z-index trap possible. Uses Windows.Media.Ocr directly, no PSM bug.
/// </summary>
public partial class ChatReaderPanel : UserControl, IPanel, IBackgroundPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private Rect? _region;
    private DispatcherTimer? _timer;
    private bool _enabled;
    private bool _firstScan = true;
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _recent = new(); // last 10 read, newest at the end

    public ChatReaderPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntervalSlider.Value = App.Settings.Current.ChatReaderInterval / 1000.0;
            CountSlider.Value = App.Settings.Current.ChatReaderMessageCount;
        };
        // NOTE: deliberately does NOT stop on Unloaded. Closing the window should
        // not stop reading chat — that's the whole point of the feature. It runs
        // until the user presses Stop Reading, or the app exits.
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    /// <summary>Stop scanning. Called on app exit (see IBackgroundPanel).</summary>
    public void StopBackgroundWork() { _enabled = false; StopTimer(); }

    // ── Region selection ─────────────────────────────────────────────────────
    private async void SetRegion_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this);
        var sel = new SelectionOverlayWindow();
        Rect? chosen = null;
        sel.RegionSelected += (_, r) => chosen = r;

        if (mainWindow != null) mainWindow.Visibility = Visibility.Hidden;

        var tcs = new TaskCompletionSource();
        sel.Closed += (_, _) => tcs.TrySetResult();
        sel.Show();
        sel.Activate();
        await tcs.Task;

        if (mainWindow != null) mainWindow.Visibility = Visibility.Visible;

        if (chosen is { } r)
        {
            _region = r;
            _firstScan = true;
            _seen.Clear();
            StatusText.Text = $"Region set: {(int)r.Width}×{(int)r.Height} at ({(int)r.X}, {(int)r.Y}). Click Start Reading.";
        }
        else
        {
            StatusText.Text = "Selection cancelled.";
        }
    }

    // ── Enable toggle ────────────────────────────────────────────────────────
    private void EnableToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return; // construction-time safety (v21.4)
        if (_region == null)
        {
            StatusText.Text = "⚠ Set a chat region first.";
            EnableToggle.IsChecked = false;
            return;
        }
        _enabled = true;
        EnableToggle.Content = "⏹ Stop Reading";
        StartTimer();
        StatusText.Text = "Reading chat. First scan silently seeds known messages.";
    }

    private void EnableToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return; // construction-time safety (v21.4)
        _enabled = false;
        EnableToggle.Content = "🔴 Start Reading";
        StopTimer();
        StatusText.Text = "Paused.";
    }

    private void StartTimer()
    {
        StopTimer();
        var interval = TimeSpan.FromMilliseconds(App.Settings.Current.ChatReaderInterval);
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += async (_, _) => await ScanAsync();
        _timer.Start();
        // Fire an immediate scan so user doesn't wait the first interval
        _ = Dispatcher.InvokeAsync(async () => await ScanAsync());
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    // ── The scan cycle ───────────────────────────────────────────────────────
    private async Task ScanAsync()
    {
        if (!_enabled || _region is null) return;

        try
        {
            var png = ScreenCaptureService.CapturePng(_region.Value);
            if (png == null) return;

            // Use the engine's OWN line segmentation rather than splitting a
            // flattened string — it keeps one chat message per entry instead of
            // merging two messages or splitting one, which is what made the same
            // text get re-read.
            var lines = (await OcrService.ReadLinesAsync(png))
                            .Where(IsLikelyChatLine)
                            .ToList();
            if (lines.Count == 0) return;

            // Dedup on a NORMALISED key, not the raw string. OCR jitters slightly
            // between scans — one pass reads "[10:24 AM]" and the next "1024 AM" —
            // so exact matching treated the same message as new over and over,
            // which is why the same lines kept being re-read and piling up.
            var newLines = lines.Where(l => _seen.Add(DedupKey(l))).ToList();

            // Trim seen set if it grew too large
            if (_seen.Count > 500)
            {
                var keep = _seen.TakeLast(200).ToList();
                _seen.Clear();
                foreach (var l in keep) _seen.Add(l);
            }

            if (_firstScan)
            {
                _firstScan = false;
                StatusText.Text = $"Seeded {newLines.Count} existing lines. Now watching for new messages.";
                return;
            }

            if (newLines.Count == 0) return;

            // Take the last N (newest) new lines
            int count = (int)CountSlider.Value;
            var toRead = newLines.TakeLast(count).ToList();

            var startEpoch = _tts?.StopEpoch ?? 0;
            foreach (var line in toRead)
            {
                AddRecent(line);
                if (_tts != null)
                {
                    await _tts.SpeakAsync(line);
                    // A Stop press (or panel close) aborts the rest of the batch,
                    // not just the current line.
                    if (_tts.StopEpoch != startEpoch) break;
                }
                if (!_enabled) break; // user may have disabled mid-read
            }
            StatusText.Text = $"Read {toRead.Count} new line(s). Total seen: {_seen.Count}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan error: {ex.Message}";
            CrashLogger.Log("ChatReaderPanel.ScanAsync", ex);
        }
    }

    /// <summary>
    /// Filter out OCR debris. Real chat has several letters and usually a
    /// speaker; a line that's mostly punctuation or digits is a misread icon or
    /// a chopped timestamp, and reading it aloud is worse than skipping it.
    /// </summary>
    private static bool IsLikelyChatLine(string line)
    {
        if (line.Length < 6) return false;
        int letters = line.Count(char.IsLetter);
        if (letters < 4) return false;
        return letters >= line.Length * 0.4;   // at least 40% actual letters
    }

    /// <summary>
    /// Key used for "have I already read this?". Lower-cases, drops everything
    /// that isn't a letter or digit, and collapses whitespace — so small OCR
    /// wobbles in punctuation and timestamps don't make an old line look new.
    /// </summary>
    private static string DedupKey(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    private void AddRecent(string line)
    {
        _recent.Enqueue(line);
        while (_recent.Count > 10) _recent.Dequeue();

        RecentList.Items.Clear();
        foreach (var l in _recent.Reverse()) // newest on top
        {
            var tb = new TextBlock
            {
                Text = $"• {l}",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 13,
            };
            RecentList.Items.Add(tb);
        }
    }

    // ── Slider change handlers ───────────────────────────────────────────────
    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists (v21.4).
        // Also prevents the XAML default from stomping the saved setting.
        if (IntervalLabel == null) return;
        var ms = (int)(IntervalSlider.Value * 1000);
        App.Settings.Current.ChatReaderInterval = ms;
        App.Settings.NotifyChanged();
        IntervalLabel.Text = $"{IntervalSlider.Value:0.#}s";
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(ms);
    }

    private void CountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during construction before the label exists (v21.4).
        if (CountLabel == null) return;
        App.Settings.Current.ChatReaderMessageCount = (int)CountSlider.Value;
        App.Settings.NotifyChanged();
        CountLabel.Text = $"{(int)CountSlider.Value} message{((int)CountSlider.Value == 1 ? "" : "s")}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _seen.Clear();
        _recent.Clear();
        _firstScan = true;
        RecentList.Items.Clear();
        StatusText.Text = "History cleared. Next scan will re-seed.";
    }
}
