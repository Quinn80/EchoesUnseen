using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Voice to Chat — click the mic, speak your message, and have it transcribed
/// to the clipboard for pasting into GW2's chat input.
///
/// HOW IT WORKS:
///   * Uses Windows' built-in Speech Recognition (System.Speech.Recognition),
///     the same engine that powers the OS-level "Windows Speech Recognition"
///     accessibility feature in Settings.
///   * 100% local — no audio data ever leaves the machine.
///   * NOT AI/ML. The engine is a 1990s-era classical Hidden Markov Model;
///     it ships with Windows by default and meets Guild Wars 2's "no AI in
///     third-party tools" terms of service requirement.
///
/// STATE MACHINE:
///   Idle ──(click)──▶ Listening ──(click)──▶ Idle
///   While Listening: SpeechHypothesized events update partial text live;
///                    SpeechRecognized events finalize a phrase.
///
/// ACCESSIBILITY:
///   * All controls expose AutomationProperties.Name for NVDA/JAWS/Narrator.
///   * The mic button announces state ("listening" / "idle") via the
///     LiveSetting and dynamic Name updates.
///   * The transcript text-box is fully editable so users can correct
///     recognition mistakes before copying.
/// </summary>
public partial class VoiceToChatPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private readonly SpeechRecognitionService _recognizer = new();
    private DispatcherTimer? _timer;
    private DateTime _listenStartedAt;
    private string _pendingText = string.Empty;

    public string PanelId => "voice-chat";
    public string PanelTitle => "Voice to Chat";
    // (An unused "CloseRequested" event was removed in v21 — panel closing is
    // handled by the PanelHost frame, which has its own working event.)

    public VoiceToChatPanel()
    {
        InitializeComponent();

        // Wire recognition events. These are raised on a worker thread, so
        // every UI update needs Dispatcher.Invoke.
        _recognizer.TextRecognized   += OnTextRecognized;
        _recognizer.PartialResult    += OnPartialResult;
        _recognizer.RecognitionError += OnRecognitionError;

        Unloaded += (_, _) => StopListening();

        UpdateEngineBanner();
        UpdateStatus("Click the microphone to start dictating.");
    }

    /// <summary>
    /// Make the privacy banner state the engine that is ACTUALLY selected, so it
    /// can never claim "no AI" while Whisper is running (or vice versa). Read on
    /// every open, since the engine can be changed in Settings between opens.
    /// </summary>
    private void UpdateEngineBanner()
    {
        var whisper = string.Equals(App.Settings.Current.SttEngine, "whisper",
            StringComparison.OrdinalIgnoreCase);
        var text = whisper
            ? "100% local. Uses Whisper, a local AI speech model that runs entirely on this " +
              "PC — inference only. Your voice never leaves your computer and no game data is used."
            : "100% local. Uses Windows' built-in Speech Recognition (an OS accessibility feature, " +
              "not AI). Your voice never leaves your computer.";
        EngineBanner.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(EngineBanner, "Privacy notice: " + text);
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts,
        GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    // ── Mic button ───────────────────────────────────────────────────────────
    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizer.IsListening) StopListening();
        else                          StartListening();
    }

    private void StartListening()
    {
        _pendingText = string.Empty;
        if (!_recognizer.StartDictation())
        {
            // Error already surfaced via the RecognitionError event.
            return;
        }

        MicButton.Content = "⏹";
        MicButton.Tag = "active"; // fire the Jarvis sonar animation + cyan core
        AutomationProperties.SetName(MicButton, "Listening. Click to stop.");
        _listenStartedAt = DateTime.UtcNow;
        StartTimer();
        UpdateStatus("Listening… speak your message clearly.");
        // Live announcement for screen readers.
        AutomationProperties.SetLiveSetting(StatusText, AutomationLiveSetting.Polite);
    }

    private void StopListening()
    {
        _recognizer.StopDictation();
        StopTimer();

        MicButton.Content = "🎙";
        MicButton.Tag = "idle"; // stop the animation, return to pink
        AutomationProperties.SetName(MicButton, "Microphone. Click to start dictation.");

        TimerText.Text = "Click to record";

        // Finalize anything we heard.
        if (!string.IsNullOrWhiteSpace(_pendingText))
        {
            UpdateStatus("Dictation stopped. Edit, copy, or play back below.");
        }
        else if (TranscriptBox.Text.Length == 0)
        {
            UpdateStatus("Nothing was recognized. Try again, speaking clearly.");
        }
        else
        {
            UpdateStatus("Dictation stopped.");
        }
    }

    // ── Recognition events (raised on worker thread) ─────────────────────────
    private void OnTextRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            // Append finalized phrases with a single space between them.
            var existing = TranscriptBox.Text;
            var separator = string.IsNullOrEmpty(existing) || existing.EndsWith(" ") ? string.Empty : " ";
            TranscriptBox.Text = existing + separator + text;
            TranscriptBox.CaretIndex = TranscriptBox.Text.Length;
            _pendingText = string.Empty;

            // Save a history entry for each finalized phrase.
            AddToHistory(text);
        });
    }

    private void OnPartialResult(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            // Show what's being heard right now without committing it.
            _pendingText = text;
            UpdateStatus($"Listening… \"{text}\"");
        });
    }

    private void OnRecognitionError(object? sender, string message)
    {
        Dispatcher.Invoke(() =>
        {
            StopTimer();
            MicButton.Content = "🎙";
            AutomationProperties.SetName(MicButton, "Microphone. Click to start dictation.");
            UpdateStatus($"Speech recognition error: {message}");
        });
    }

    // ── Timer ────────────────────────────────────────────────────────────────
    private void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _listenStartedAt;
        TimerText.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    // ── Action buttons ───────────────────────────────────────────────────────
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = TranscriptBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            UpdateStatus("Nothing to copy. Dictate or type a message first.");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            UpdateStatus("Copied to clipboard. Paste into Guild Wars 2 chat with Ctrl+V.");
            // Brief audible confirmation for screen-reader users.
            _tts?.SpeakAsync("Copied.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("VoiceToChatPanel.Copy_Click", ex);
            UpdateStatus("Could not access the clipboard. Try again.");
        }
    }

    private void Playback_Click(object sender, RoutedEventArgs e)
    {
        var text = TranscriptBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            UpdateStatus("Nothing to play back.");
            return;
        }
        _tts?.SpeakAsync(text);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        TranscriptBox.Text = string.Empty;
        UpdateStatus("Transcript cleared.");
    }

    // ── History ──────────────────────────────────────────────────────────────
    private void AddToHistory(string text)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        var item = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (System.Windows.Media.Brush)FindResource("SurfaceBorderBrush"),
            Padding = new Thickness(0, 6, 0, 6),
            Child = new TextBlock
            {
                Text = $"[{ts}] {text}",
                Foreground = System.Windows.Media.Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        // Insert newest at the top for quick scanning.
        HistoryList.Children.Insert(0, item);
        // Cap history to avoid unbounded growth.
        while (HistoryList.Children.Count > 25)
            HistoryList.Children.RemoveAt(HistoryList.Children.Count - 1);
    }

    // ── Utilities ────────────────────────────────────────────────────────────
    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
        // Setting Name on the live-region helps NVDA/JAWS announce the change.
        AutomationProperties.SetName(StatusText, message);
    }
}
