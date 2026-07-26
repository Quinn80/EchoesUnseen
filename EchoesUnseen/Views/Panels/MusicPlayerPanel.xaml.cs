using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using EchoesUnseen.Models;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;

namespace EchoesUnseen.Views.Panels;

/// <summary>
/// Music Player — three modes for playing GW2 instruments.
///
/// TAB 1 — SHEET MUSIC:
///   Shows the ABC notation + key mapping so a visually able player or a
///   player with a printed/read-aloud score can perform the song manually.
///   "Read Notation Aloud" dictates the sequence in 1-2-3-4-5 key-number form
///   which is how most GW2 instrument tutorials teach songs.
///
/// TAB 2 — GUIDE MODE:
///   Notes scroll across the canvas from right to left. When a note's left
///   edge crosses the vertical "press" line, the user should press that key
///   on their own keyboard. We do NOT send keys in this mode — the user
///   plays the instrument themselves and uses this as a practice aid.
///
/// TAB 3 — AUTO-PLAY:
///   We send the key presses directly to whatever window has focus. The user
///   is instructed to focus the GW2 window before starting. ToS-compliant
///   because we only ever send single digits 0-9 with clamped durations.
///
/// NOTE PARSING is shared: <see cref="SongLibraryService.ParseAbc"/> turns
/// the ABC string into a list of <see cref="ParsedNote"/>s that all three
/// modes consume.
/// </summary>
public partial class MusicPlayerPanel : UserControl, IPanel, IBackgroundPanel
{
    private MumbleLinkReader? _mumble;
    private TtsService? _tts;
    private GlobalHotkeyService? _hotkeys;
    private Gw2ApiService? _gw2Api;

    private List<Song> _allSongs = new();
    private Song? _selectedSong;
    private List<ParsedNote> _parsedNotes = new();

    // Auto-play / guide state
    private CancellationTokenSource? _playbackCts;
    private readonly KeyPressService _keyPress = new();

    // Guide canvas rendering
    private DispatcherTimer? _guideTimer;
    private double _guideStartTimeMs;
    private const double GuidePixelsPerSecond = 180;
    private const double GuidePressLineX = 100; // where notes should be when pressed

    public MusicPlayerPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Deliberately no Unloaded teardown: music should keep playing with the
        // window closed. Stops on the panel's own Stop button, or app exit.
    }

    public void AttachServices(MumbleLinkReader? mumble, TtsService? tts, GlobalHotkeyService? hotkeys, Gw2ApiService? gw2Api)
    {
        _mumble = mumble;
        _tts = tts;
        _hotkeys = hotkeys;
        _gw2Api = gw2Api;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var lib = new SongLibraryService(App.Settings);
            _allSongs = lib.LoadAll();
            RefreshSongCombo("");
            SongCount.Text = $"{_allSongs.Count} songs";
            if (_allSongs.Count > 0)
            {
                SongCombo.SelectedItem = _allSongs[0];
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("MusicPlayerPanel OnLoaded", ex);
            SongCount.Text = "Error loading songs";
        }
    }

    /// <summary>Stop playback and the guide timer. Called on app exit.</summary>
    public void StopBackgroundWork() { try { StopAll(); } catch { } }

    // ── Song selection / filtering ───────────────────────────────────────────
    private void RefreshSongCombo(string filter)
    {
        SongCombo.Items.Clear();
        IEnumerable<Song> filtered = _allSongs;
        if (!string.IsNullOrWhiteSpace(filter))
            filtered = _allSongs.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var s in filtered.Take(50))
            SongCombo.Items.Add(s);
    }

    // ── Import tab: paste number notation → personal library ─────────────────
    private void ImportSave_Click(object sender, RoutedEventArgs e)
    {
        var name = ImportName.Text?.Trim() ?? "";
        var notes = ImportNotes.Text ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            SetImportStatus("Please enter a song name first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(notes))
        {
            SetImportStatus("Please paste the song's number notation first.");
            return;
        }
        if (!int.TryParse(ImportBpm.Text?.Trim(), out int bpm) || bpm < 20 || bpm > 400)
        {
            SetImportStatus("Speed must be a number between 20 and 400. Try 100.");
            return;
        }

        var instrument = (ImportInstrument.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Lute";

        // Accept EITHER number notation (1-8) or ABC letters (A-G) — auto-detected.
        var abc = SongLibraryService.NormalizeUserNotation(notes, out int noteCount, out int skipped);
        if (noteCount == 0)
        {
            SetImportStatus("No playable notes were found. Use numbers 1 to 8 (like 1 2 3 4 5) or letters A to G (like C D E F G).");
            return;
        }

        try
        {
            var lib = new SongLibraryService(App.Settings);
            lib.SaveExternalSong(name, instrument, bpm, abc);

            // Reload the library so the new song shows up everywhere immediately.
            _allSongs = lib.LoadAll();
            RefreshSongCombo("");
            SongCount.Text = $"{_allSongs.Count} songs";

            // Select the song we just added so the user can play it right away.
            var added = _allSongs.FirstOrDefault(s => s.Name == name);
            if (added != null) SongCombo.SelectedItem = added;

            var skipNote = skipped > 0
                ? $" {skipped} unsupported symbol(s) were skipped (GW2 instruments only have eight notes)."
                : "";
            SetImportStatus($"Saved \"{name}\" with {noteCount} notes for {instrument}.{skipNote} It's now in your library and ready to play in all three modes.");

            // Clear the entry fields for the next import (keep instrument + bpm).
            ImportName.Text = "";
            ImportNotes.Text = "";
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ImportSave_Click", ex);
            SetImportStatus($"Could not save the song: {ex.Message}");
        }
    }

    private void ImportClear_Click(object sender, RoutedEventArgs e)
    {
        ImportName.Text = "";
        ImportNotes.Text = "";
        SetImportStatus("Form cleared.");
    }

    /// <summary>Load notation text from a file into the notes box.</summary>
    private void ImportFromFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose a song file",
                Filter = "Song files (*.txt;*.abc)|*.txt;*.abc|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var text = System.IO.File.ReadAllText(dlg.FileName);
            ImportNotes.Text = text;

            // Pre-fill the name from the file name if the user hasn't typed one.
            if (string.IsNullOrWhiteSpace(ImportName.Text))
                ImportName.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            SetImportStatus($"Loaded {System.IO.Path.GetFileName(dlg.FileName)}. Check the name and speed, then Convert and Save.");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ImportFromFile_Click", ex);
            SetImportStatus($"Could not read that file: {ex.Message}");
        }
    }

    private void SetImportStatus(string message)
    {
        ImportStatus.Text = message;
        System.Windows.Automation.AutomationProperties.SetName(ImportStatus, message);
        _tts?.SpeakAsync(message);
    }

    private void SongCombo_KeyUp(object sender, KeyEventArgs e)
    {
        // Filter as the user types. Only when the combo box is in editable text mode.
        if (SongCombo.IsEditable && SongCombo.Text != null)
        {
            var query = SongCombo.Text;
            // Avoid re-filtering when the user is just selecting from a dropdown
            if (e.Key != Key.Enter && e.Key != Key.Tab)
                RefreshSongCombo(query);
        }
    }

    private void SongCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SongCombo.SelectedItem is not Song s) return;
        _selectedSong = s;
        SelectedSongName.Text = s.Name;
        SelectedSongMeta.Text = $"{s.Instrument} · {s.Bpm} BPM · by {s.Uploader}";
        _parsedNotes = SongLibraryService.ParseAbc(s.Abc, s.Bpm);
        SheetText.Text = FormatSheet(s, _parsedNotes);
        RenderAutoNoteDisplay();
        RenderGuideCanvas(0);
    }

    private string FormatSheet(Song s, List<ParsedNote> notes)
    {
        var lines = new List<string>();
        lines.Add($"{s.Name} — {s.Instrument} — {s.Bpm} BPM");
        lines.Add(new string('─', 40));
        lines.Add("");
        lines.Add("Keys to press (in order):");
        var keyLine = string.Join(" ", notes.Select(n => n.IsRest ? "—" : n.Key.ToString()));
        lines.Add(keyLine);
        lines.Add("");
        lines.Add("Raw ABC notation:");
        lines.Add(s.Abc);
        return string.Join("\n", lines);
    }

    // ── Tempo slider ─────────────────────────────────────────────────────────
    private void TempoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: WPF fires ValueChanged while the panel is still being BUILT
        // (the slider's Value attribute is set before TempoLabel exists),
        // which threw a NullReferenceException during construction (v21.3 fix).
        if (TempoLabel == null) return;
        TempoLabel.Text = $"{TempoSlider.Value:0.0}x";
    }

    // ── Sheet tab ────────────────────────────────────────────────────────────
    private async void ReadNotation_Click(object sender, RoutedEventArgs e)
    {
        if (_tts == null || _selectedSong == null) return;
        var keys = string.Join(", ", _parsedNotes
            .Take(20) // don't dictate 500 keys in one go
            .Select(n => n.IsRest ? "rest" : n.Key.ToString()));
        var speech = $"{_selectedSong.Name}. First notes: {keys}.";
        await _tts.SpeakAsync(speech);
    }

    // ── Guide tab ────────────────────────────────────────────────────────────
    private void GuideStart_Click(object sender, RoutedEventArgs e)
    {
        if (_parsedNotes.Count == 0) return;
        GuideStartBtn.IsEnabled = false;
        GuideStopBtn.IsEnabled = true;
        _guideStartTimeMs = 0;
        _guideTimer?.Stop();
        _guideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _guideTimer.Tick += GuideTick;
        _guideTimer.Start();
    }

    private void GuideStop_Click(object sender, RoutedEventArgs e)
    {
        GuideStartBtn.IsEnabled = true;
        GuideStopBtn.IsEnabled = false;
        _guideTimer?.Stop();
        _guideTimer = null;
        RenderGuideCanvas(0);
    }

    private void GuideTick(object? sender, EventArgs e)
    {
        _guideStartTimeMs += 40;
        var tempo = (float)TempoSlider.Value;
        RenderGuideCanvas(_guideStartTimeMs * tempo);
        // Stop when the last note has passed the press line
        var totalMs = _parsedNotes.Sum(n => n.BeatMs);
        if (_guideStartTimeMs * tempo > totalMs + 2000) // 2s grace after end
        {
            GuideStop_Click(this, new RoutedEventArgs());
        }
    }

    private void RenderGuideCanvas(double currentMs)
    {
        GuideCanvas.Children.Clear();
        if (_parsedNotes.Count == 0) return;

        double width = GuideCanvas.ActualWidth > 0 ? GuideCanvas.ActualWidth : 500;
        double height = GuideCanvas.ActualHeight > 0 ? GuideCanvas.ActualHeight : 200;

        // Draw the "press" vertical line
        var line = new System.Windows.Shapes.Line
        {
            X1 = GuidePressLineX,
            Y1 = 0,
            X2 = GuidePressLineX,
            Y2 = height,
            Stroke = (Brush)FindResource("PrimaryBrush"),
            StrokeThickness = 2,
        };
        GuideCanvas.Children.Add(line);

        // Draw notes scrolling right-to-left based on when they should be pressed
        double pixelsPerMs = GuidePixelsPerSecond / 1000.0;
        double cumMs = 0;
        for (int i = 0; i < _parsedNotes.Count; i++)
        {
            var n = _parsedNotes[i];
            double noteCenterX = GuidePressLineX + (cumMs - currentMs) * pixelsPerMs;
            cumMs += n.BeatMs;
            if (noteCenterX < -40 || noteCenterX > width + 40) continue; // off-canvas

            if (!n.IsRest)
            {
                var keyNum = int.Parse(n.Key.ToString());
                double y = ((double)(keyNum - 1) / 7) * (height - 30) + 5;
                var circle = new Ellipse
                {
                    Width = 30,
                    Height = 30,
                    Fill = noteCenterX < GuidePressLineX
                        ? new SolidColorBrush(Color.FromArgb(0x88, 0xB8, 0xB8, 0xC8))
                        : (Brush)FindResource("PrimaryBrush"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(circle, noteCenterX - 15);
                Canvas.SetTop(circle, y);
                GuideCanvas.Children.Add(circle);

                var label = new TextBlock
                {
                    Text = n.Key.ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                };
                Canvas.SetLeft(label, noteCenterX - 5);
                Canvas.SetTop(label, y + 4);
                GuideCanvas.Children.Add(label);
            }
        }
    }

    // ── Auto-Play tab ────────────────────────────────────────────────────────
    private void RenderAutoNoteDisplay()
    {
        AutoNoteDisplay.Children.Clear();
        for (int i = 0; i < _parsedNotes.Count; i++)
        {
            var n = _parsedNotes[i];
            var chip = new Border
            {
                Background = n.IsRest
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0xB8, 0xB8, 0xC8))
                    : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x1A, 0x8A)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 4),
                Tag = i,
            };
            chip.Child = new TextBlock
            {
                Text = n.IsRest ? "—" : n.Key.ToString(),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
            };
            AutoNoteDisplay.Children.Add(chip);
        }
    }

    private async void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_parsedNotes.Count == 0) return;
        AutoStartBtn.IsEnabled = false;
        AutoStopBtn.IsEnabled = true;
        _playbackCts = new CancellationTokenSource();
        AutoStatus.Text = "Playing... click GW2 window now if you haven't yet.";

        try
        {
            await _keyPress.PlayAsync(
                _parsedNotes,
                (float)TempoSlider.Value,
                onNoteChange: i => HighlightNote(i),
                _playbackCts.Token);
            AutoStatus.Text = "Done.";
        }
        catch (Exception ex)
        {
            AutoStatus.Text = $"Error: {ex.Message}";
            CrashLogger.Log("MusicPlayerPanel AutoStart", ex);
        }
        finally
        {
            AutoStartBtn.IsEnabled = true;
            AutoStopBtn.IsEnabled = false;
            UnhighlightAllNotes();
        }
    }

    private void AutoStop_Click(object sender, RoutedEventArgs e)
    {
        _playbackCts?.Cancel();
        AutoStatus.Text = "Stopped.";
    }

    private void HighlightNote(int index)
    {
        Dispatcher.Invoke(() =>
        {
            UnhighlightAllNotes();
            if (index >= 0 && index < AutoNoteDisplay.Children.Count &&
                AutoNoteDisplay.Children[index] is Border b)
            {
                b.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x1A, 0x8A));
            }
        });
    }

    private void UnhighlightAllNotes()
    {
        for (int i = 0; i < AutoNoteDisplay.Children.Count; i++)
        {
            if (AutoNoteDisplay.Children[i] is Border b && b.Tag is int idx && idx < _parsedNotes.Count)
            {
                var n = _parsedNotes[idx];
                b.Background = n.IsRest
                    ? new SolidColorBrush(Color.FromArgb(0x22, 0xB8, 0xB8, 0xC8))
                    : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0x1A, 0x8A));
            }
        }
    }

    private void StopAll()
    {
        _playbackCts?.Cancel();
        _guideTimer?.Stop();
    }
}
