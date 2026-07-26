using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Voice Assistant — search the Guild Wars 2 Wiki by TYPING or by SPEAKING,
/// with TTS readback of the results.
///
/// Voice input uses Windows' built-in System.Speech recognition (a local,
/// classical HMM engine — not AI, no cloud, audio never leaves the machine),
/// via SpeechRecognitionService. A recognized phrase becomes the wiki search
/// query exactly as if the user had typed it. Typed search remains fully
/// supported for users who prefer it.
///
/// PICK-BEST-RESULT BUG FIX:
///   The previous build used <c>results[0]</c> and frequently returned the
///   wrong article. We now route through <see cref="WikiService.PickBestResult"/>
///   which scores by title match, so "Charr" returns the Charr race page,
///   not some random article that mentions charr.
///
/// LONG-ARTICLE PAGINATION:
///   Wiki extracts can be thousands of words. We chunk at ~600 characters
///   (preferring sentence boundaries) and show a "Continue reading?" button
///   between chunks so the user isn't trapped listening to a 10-minute read.
/// </summary>
public partial class VoiceAssistantPanel : UserControl, IPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;
    private readonly WikiService _wiki = new();
    private readonly SpeechRecognitionService _recognizer = new();

    // Pagination state for the current article
    private List<string> _pendingChunks = new();
    private int _nextChunkIndex = 0;

    private static readonly string[] Suggestions =
    {
        "How do I get a mount?",
        "What is WvW?",
        "Where is Divinity's Reach?",
        "How does crafting work?",
        "What is a raid?",
        "How do I get a legendary?",
    };

    public VoiceAssistantPanel()
    {
        InitializeComponent();
        BuildSuggestionChips();
        AddSystemMessage("Ask me anything about Guild Wars 2. Type or click the microphone to speak. Answers are read aloud.");

        // Speech-recognition wiring. The events fire on a worker thread,
        // so handlers marshal back to the UI thread via Dispatcher.Invoke.
        _recognizer.TextRecognized += OnVoiceQueryRecognized;
        _recognizer.RecognitionError += (_, msg) => Dispatcher.Invoke(() =>
        {
            ResetMicButton();
            AddSystemMessage($"Voice input error: {msg}");
        });

        Unloaded += (_, _) => _recognizer.Dispose();
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    // ── UI construction ──────────────────────────────────────────────────────
    private void BuildSuggestionChips()
    {
        foreach (var s in Suggestions)
        {
            var chip = new Button
            {
                Content = s,
                Style = (Style)FindResource("SecondaryButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 12,
            };
            chip.Click += (_, _) => { QueryBox.Text = s; Submit(s); };
            SuggestionsPanel.Children.Add(chip);
        }
    }

    private void AddUserMessage(string text) => AddMessage(text, isUser: true);
    private void AddAssistantMessage(string text) => AddMessage(text, isUser: false);
    private void AddSystemMessage(string text) => AddMessage(text, isUser: false, isSystem: true);

    private void AddMessage(string text, bool isUser, bool isSystem = false)
    {
        var bubble = new Border
        {
            Background = isUser
                ? new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x1A, 0x8A))
                : isSystem
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                    : new SolidColorBrush(Color.FromArgb(0x22, 0xA4, 0x14, 0x35)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(isUser ? 40 : 0, 0, isUser ? 0 : 40, 8),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };
        bubble.Child = tb;
        HistoryPanel.Children.Add(bubble);
        HistoryScroll.ScrollToEnd();
    }

    // ── Input handling ───────────────────────────────────────────────────────
    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Search_Click(sender, e); e.Handled = true; }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var q = QueryBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;
        QueryBox.Clear();
        Submit(q);
    }

    private async void Submit(string query)
    {
        AddUserMessage(query);
        AddSystemMessage("Searching the GW2 wiki...");

        WikiArticle? article;
        try
        {
            article = await _wiki.SearchAndFetchAsync(query);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Search failed: {ex.Message}");
            CrashLogger.Log("VoiceAssistantPanel.Submit", ex);
            return;
        }

        if (article == null)
        {
            AddSystemMessage($"No wiki article found for \"{query}\". Try rephrasing.");
            return;
        }

        AddAssistantMessage($"📖 {article.Title}");

        // Chunk the extract — the chunks are individually short so TTS doesn't
        // queue up multi-minute audio buffers, but we read ALL of them in
        // sequence so the user hears the full article. (Previous build stopped
        // after the first chunk and required a "Continue reading?" click each
        // time — bad for screen-reader users who want hands-free operation.)
        _pendingChunks = ChunkText(article.Extract, maxChars: 600);
        _nextChunkIndex = 0;
        await ReadAllChunksAsync();
    }

    /// <summary>
    /// Read every remaining chunk of the current article aloud, in sequence.
    /// Yields to the UI thread between chunks so the panel stays responsive
    /// and screen readers can announce each one separately.
    /// </summary>
    private async Task ReadAllChunksAsync()
    {
        // Capture the stop epoch up front. If the user presses Stop (or closes
        // the panel) at ANY point during the read — mid-sentence or in the gap
        // between paragraphs — the epoch changes and we abort the whole read,
        // instead of ploughing on to the next chunk.
        var startEpoch = _tts?.StopEpoch ?? 0;
        var stopped = false;

        while (_nextChunkIndex < _pendingChunks.Count)
        {
            if (_tts != null && _tts.StopEpoch != startEpoch) { stopped = true; break; }

            var chunk = _pendingChunks[_nextChunkIndex++];
            AddAssistantMessage(chunk);

            if (_tts != null)
            {
                try
                {
                    await _tts.SpeakAsync(chunk);
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("VoiceAssistantPanel ReadAllChunks", ex);
                    break; // don't keep trying if TTS is broken
                }
                if (_tts.StopEpoch != startEpoch) { stopped = true; break; }
            }

            // Tiny breath between paragraphs so the cadence sounds natural and
            // the screen reader has a moment to announce the new text bubble.
            await Task.Delay(150);
        }

        if (!stopped) AddSystemMessage("(End of article.)");
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _tts?.StopSpeaking();

    // ── Voice input via Windows Speech Recognition ───────────────────────────
    /// <summary>
    /// Toggle dictation. When listening starts, the next phrase the user speaks
    /// becomes the wiki search query (the same as if they'd typed it).
    /// </summary>
    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recognizer.IsListening)
        {
            _recognizer.StopDictation();
            ResetMicButton();
            return;
        }

        if (_recognizer.StartDictation())
        {
            MicButton.Content = "⏹";
            MicButton.Tag = "active"; // Jarvis sonar animation on
            System.Windows.Automation.AutomationProperties.SetName(MicButton, "Listening. Click to cancel.");
            AddSystemMessage("Listening… speak your search query.");
        }
    }

    private void OnVoiceQueryRecognized(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            // First confident phrase wins — stop listening and submit.
            _recognizer.StopDictation();
            ResetMicButton();

            if (string.IsNullOrWhiteSpace(text)) return;
            QueryBox.Text = text;
            Submit(text);
        });
    }

    private void ResetMicButton()
    {
        MicButton.Content = "🎙";
        MicButton.Tag = "idle"; // stop the Jarvis animation
        System.Windows.Automation.AutomationProperties.SetName(MicButton, "Dictate search query");
    }

    // ── Text chunking ────────────────────────────────────────────────────────
    /// <summary>
    /// Split <paramref name="text"/> into chunks no longer than <paramref name="maxChars"/>,
    /// preferring sentence boundaries (. ! ?) near the target length.
    /// Never cuts a word mid-character.
    /// </summary>
    private static List<string> ChunkText(string text, int maxChars)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        text = text.Trim();
        int pos = 0;
        while (pos < text.Length)
        {
            int remaining = text.Length - pos;
            if (remaining <= maxChars)
            {
                chunks.Add(text[pos..].Trim());
                break;
            }
            // Look for a sentence boundary in the last ~25% of the window
            int windowEnd = pos + maxChars;
            int searchStart = pos + (maxChars * 3 / 4);
            int cut = -1;
            for (int i = windowEnd; i >= searchStart && i < text.Length; i--)
            {
                if (text[i] == '.' || text[i] == '!' || text[i] == '?')
                {
                    cut = i + 1;
                    break;
                }
            }
            // Fallback: break on whitespace near the boundary
            if (cut < 0)
            {
                for (int i = windowEnd; i > pos && i < text.Length; i--)
                {
                    if (char.IsWhiteSpace(text[i])) { cut = i; break; }
                }
            }
            // Last resort: hard cut
            if (cut < 0) cut = windowEnd;

            chunks.Add(text[pos..cut].Trim());
            pos = cut;
        }
        return chunks;
    }
}
