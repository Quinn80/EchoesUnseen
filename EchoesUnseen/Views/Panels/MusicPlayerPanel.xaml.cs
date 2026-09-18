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
    private readonly KeyPressService _keyPress = new();   // fallback sender
    private readonly AhkPlayer _ahk = new();              // primary: real AutoHotkey engine
    private readonly SongCatalogService _catalog = new(); // online song search

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
            InitGuideControls();
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
        foreach (var s in filtered.OrderBy(s => s.Name).Take(1000))
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
            int newId = lib.SaveExternalSong(name, instrument, bpm, abc);

            if (newId < 0)
            {
                SetImportStatus($"\"{name}\" is already in your library — nothing added.");
                _tts?.SpeakAsync($"{name} is already in your library.");
                return;
            }

            // Reload the library so the new song shows up everywhere immediately.
            _allSongs = lib.LoadAll();
            RefreshSongCombo("");
            SongCount.Text = $"{_allSongs.Count} songs";

            // Select the song we just added so the user can play it right away.
            var justAdded = _allSongs.FirstOrDefault(s => s.Name == name);
            if (justAdded != null) SongCombo.SelectedItem = justAdded;

            var skipNote = skipped > 0
                ? $" {skipped} unsupported symbol(s) were skipped (GW2 instruments only have eight notes)."
                : "";
            var okMsg = $"Added \"{name}\" with {noteCount} notes. You now have {_allSongs.Count} songs.{skipNote}";
            SetImportStatus(okMsg);
            _tts?.SpeakAsync(okMsg);

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

    /// <summary>Batch import: pick many .ahk/.mid/.txt/.abc files and add them all
    /// to the library at once — so pulling a folder of songs isn't one-at-a-time.</summary>
    private void ImportMany_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose song files to import (Ctrl+A selects all in a folder)",
                Filter = "Song files (*.ahk;*.mid;*.midi;*.txt;*.abc)|*.ahk;*.mid;*.midi;*.txt;*.abc|All files (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var lib = new SongLibraryService(App.Settings);
            int added = 0, already = 0, failed = 0;
            foreach (var path in dlg.FileNames)
            {
                try
                {
                    var abc = FileToAbc(path, out int bpm, out int count);
                    if (count == 0) { failed++; continue; }
                    // Normalise so ABC/number/letters all end up consistent.
                    var norm = SongLibraryService.NormalizeUserNotation(abc, out int n, out _);
                    if (n == 0) { failed++; continue; }
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (lib.SaveExternalSong(name, "Lute", bpm, norm) < 0) already++;   // duplicate
                    else added++;
                }
                catch (Exception ex) { CrashLogger.Log("ImportMany one", ex); failed++; }
            }

            _allSongs = lib.LoadAll();
            RefreshSongCombo("");
            SongCount.Text = $"{_allSongs.Count} songs";

            // Clear, spoken confirmation of exactly what happened.
            var parts = new List<string>();
            parts.Add(added == 0 ? "No new songs added" : $"Added {added} new song{(added == 1 ? "" : "s")}");
            if (already > 0) parts.Add($"{already} were already in your library");
            if (failed > 0) parts.Add($"{failed} could not be read");
            var msg = string.Join(". ", parts) + $". You now have {_allSongs.Count} songs.";
            SetImportStatus(msg);
            _tts?.SpeakAsync(msg);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ImportMany_Click", ex);
            SetImportStatus($"Batch import failed: {ex.Message}");
        }
    }

    /// <summary>Convert one song file (by extension) to ABC notation.</summary>
    private static string FileToAbc(string path, out int bpm, out int count)
    {
        bpm = 100; count = 0;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".ahk")
            return SongImportService.FromAhk(System.IO.File.ReadAllText(path), out bpm, out count);
        if (ext is ".mid" or ".midi")
            return SongImportService.FromMidi(path, out bpm, out count);
        // .txt / .abc / anything else: treat the text as notation.
        var text = System.IO.File.ReadAllText(path);
        var abc = SongLibraryService.NormalizeUserNotation(text, out count, out _);
        return abc;
    }

    // ── Find Songs (online search) ────────────────────────────────────────────
    private async void FindSearch_Click(object sender, RoutedEventArgs e) => await DoFind();
    private async void FindBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) await DoFind(); }

    private async System.Threading.Tasks.Task DoFind()
    {
        FindStatus.Text = "Searching…";
        _tts?.SpeakAsync("Searching.");
        try
        {
            var all = await _catalog.GetAsync();
            var q = FindBox.Text?.Trim() ?? "";
            var hits = (string.IsNullOrWhiteSpace(q)
                    ? all
                    : all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.Name).Take(500).ToList();

            FindResults.Items.Clear();
            foreach (var s in hits) FindResults.Items.Add(s);
            if (hits.Count > 0) FindResults.SelectedIndex = 0;

            if (all.Count == 0)
            {
                FindStatus.Text = "Couldn't reach the song library — check your internet connection.";
                _tts?.SpeakAsync("Could not reach the song library.");
            }
            else
            {
                FindStatus.Text = $"Found {hits.Count} song{(hits.Count == 1 ? "" : "s")}. Choose one and press Add.";
                _tts?.SpeakAsync($"Found {hits.Count} songs.");
            }
        }
        catch (Exception ex) { FindStatus.Text = $"Search failed: {ex.Message}"; CrashLogger.Log("FindSearch", ex); }
    }

    private void FindAdd_Click(object sender, RoutedEventArgs e)
    {
        if (FindResults.SelectedItem is not SongCatalogService.CatalogSong cs)
        {
            FindStatus.Text = "Pick a song from the list first.";
            _tts?.SpeakAsync("Pick a song from the list first.");
            return;
        }
        try
        {
            var abc = SongLibraryService.NormalizeUserNotation(cs.Notation, out int nc, out _);
            if (nc == 0) { FindStatus.Text = "That song had no playable notes."; return; }

            var lib = new SongLibraryService(App.Settings);
            int id = lib.SaveExternalSong(cs.Name, "Harp", 100, abc);
            _allSongs = lib.LoadAll();
            RefreshSongCombo("");
            SongCount.Text = $"{_allSongs.Count} songs";

            if (id < 0)
            {
                FindStatus.Text = $"\"{cs.Name}\" is already in your library.";
                _tts?.SpeakAsync($"{cs.Name} is already in your library.");
                return;
            }
            var added = _allSongs.FirstOrDefault(s => s.Name == cs.Name);
            if (added != null) SongCombo.SelectedItem = added;
            FindStatus.Text = $"Added \"{cs.Name}\". You now have {_allSongs.Count} songs.";
            _tts?.SpeakAsync($"Added {cs.Name}. It's in your library.");
        }
        catch (Exception ex) { FindStatus.Text = $"Add failed: {ex.Message}"; CrashLogger.Log("FindAdd", ex); }
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
                Filter = "All song files (*.txt;*.abc;*.ahk;*.mid;*.midi)|*.txt;*.abc;*.ahk;*.mid;*.midi" +
                         "|AutoHotkey scripts (*.ahk)|*.ahk|MIDI files (*.mid;*.midi)|*.mid;*.midi" +
                         "|Text / ABC (*.txt;*.abc)|*.txt;*.abc|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            int count;
            if (ext == ".ahk")
            {
                var abc = SongImportService.FromAhk(System.IO.File.ReadAllText(dlg.FileName), out int bpm, out count);
                if (count == 0) { SetImportStatus("No note keys (1-8) were found in that AutoHotkey script."); return; }
                ImportNotes.Text = abc;
                ImportBpm.Text = bpm.ToString();
                SetImportStatus($"Read {count} notes from the AutoHotkey script (speed set to {bpm}). Check the name, then Convert and Save.");
            }
            else if (ext is ".mid" or ".midi")
            {
                var abc = SongImportService.FromMidi(dlg.FileName, out int bpm, out count);
                if (count == 0) { SetImportStatus("Couldn't find a melody in that MIDI file."); return; }
                ImportNotes.Text = abc;
                ImportBpm.Text = bpm.ToString();
                SetImportStatus($"Read {count} melody notes from the MIDI (speed set to {bpm}). MIDI is folded into the game's 8 notes, so tweak if needed, then Convert and Save.");
            }
            else
            {
                ImportNotes.Text = System.IO.File.ReadAllText(dlg.FileName);
                SetImportStatus($"Loaded {System.IO.Path.GetFileName(dlg.FileName)}. Check the name and speed, then Convert and Save.");
            }

            // Pre-fill the name from the file name if the user hasn't typed one.
            if (string.IsNullOrWhiteSpace(ImportName.Text))
                ImportName.Text = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
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
        SelectedSongMeta.Text = $"{s.Instrument} · {s.Bpm} BPM";
        _parsedNotes = SongLibraryService.ParseAbc(s.Abc, s.Bpm);
        SheetText.Text = FormatSheet(s, _parsedNotes);
        RenderAutoNoteDisplay();
        RenderGuideCanvas(0);

        // Sync the per-song instrument picker without re-triggering a save.
        _syncingInstrument = true;
        foreach (ComboBoxItem it in SongInstrumentCombo.Items)
            if ((string)it.Content == s.Instrument) { SongInstrumentCombo.SelectedItem = it; break; }
        if (SongInstrumentCombo.SelectedIndex < 0) SongInstrumentCombo.SelectedIndex = 0;
        _syncingInstrument = false;
    }

    private bool _syncingInstrument;

    /// <summary>
    /// Change the instrument for the selected song. In Guild Wars 2 every
    /// instrument uses the same eight keys, so this doesn't change what's played
    /// — it labels the song for you and (for your own saved songs) is remembered.
    /// </summary>
    private void SongInstrument_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInstrument || _selectedSong == null) return;
        if (SongInstrumentCombo.SelectedItem is not ComboBoxItem it) return;

        var instrument = (string)it.Content;
        _selectedSong.Instrument = instrument;
        SelectedSongMeta.Text = $"{instrument} · {_selectedSong.Bpm} BPM";

        try
        {
            // Persist for user songs; bundled songs update for the session only.
            new SongLibraryService(App.Settings).UpdateInstrument(_selectedSong.Name, instrument);
        }
        catch (Exception ex) { CrashLogger.Log("SongInstrument_Changed", ex); }

        _tts?.SpeakAsync($"Instrument set to {instrument}.");
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
    private Views.GuideOverlay? MainGuide =>
        (System.Windows.Application.Current?.MainWindow as EchoesUnseen.MainWindow)?.Guide;

    private void GuideStart_Click(object sender, RoutedEventArgs e)
    {
        if (_parsedNotes.Count == 0) { _tts?.SpeakAsync("Pick a song first."); return; }
        var guide = MainGuide;
        if (guide == null) return;

        GuideStartBtn.IsEnabled = false;
        GuideStopBtn.IsEnabled = true;

        if (App.Settings.Current.GuideMode == "step")
        {
            guide.StartStep(_parsedNotes);
            _tts?.SpeakAsync("Step guide started. Watch above your skill bar and press the lit key.");
        }
        else
        {
            guide.StartTimed(_parsedNotes, _selectedSong?.Bpm ?? 100, (float)TempoSlider.Value);
            _tts?.SpeakAsync("Guide started. Follow the falling notes above your skill bar.");
        }
    }

    // ── Guide appearance controls ─────────────────────────────────────────────
    private bool _syncingGuide;
    private static readonly (string name, string hex)[] GuideColors =
    {
        ("Theme accent", ""), ("Vivid Red", "#FFFF1744"), ("Neon Orange", "#FFFF6D00"),
        ("Bright Gold", "#FFFFC400"), ("Electric Lime", "#FF76FF03"), ("Neon Green", "#FF00E676"),
        ("Aqua Cyan", "#FF00E5FF"), ("Electric Blue", "#FF2979FF"), ("Vivid Violet", "#FFD500F9"),
        ("Hot Pink", "#FFFF1B8E"), ("White", "#FFFFFFFF"),
    };

    private void InitGuideControls()
    {
        _syncingGuide = true;
        var s = App.Settings.Current;
        foreach (ComboBoxItem it in GuideModeCombo.Items)
            if ((string)it.Tag == s.GuideMode) { GuideModeCombo.SelectedItem = it; break; }
        if (GuideModeCombo.SelectedIndex < 0) GuideModeCombo.SelectedIndex = 0;

        foreach (var (name, hex) in GuideColors)
            GuideColorCombo.Items.Add(new ComboBoxItem { Content = name, Tag = hex });
        foreach (ComboBoxItem it in GuideColorCombo.Items)
            if ((string)it.Tag == s.GuideColor) { GuideColorCombo.SelectedItem = it; break; }
        if (GuideColorCombo.SelectedIndex < 0) GuideColorCombo.SelectedIndex = 0;

        GuideSpeedSlider.Value = s.GuideLeadMs;
        GuideSpeedLabel.Text = $"{s.GuideLeadMs / 1000.0:0.0}s";
        GuideSizeSlider.Value = s.GuideSize;
        GuideSizeLabel.Text = $"{(int)(s.GuideSize * 100)}%";
        GuideTones.IsChecked = s.GuideTones;
        _syncingGuide = false;
    }

    private void GuideMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGuide || GuideModeCombo.SelectedItem is not ComboBoxItem it) return;
        App.Settings.Current.GuideMode = (string)it.Tag;
        App.Settings.NotifyChanged();
    }
    private void GuideColor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGuide || GuideColorCombo.SelectedItem is not ComboBoxItem it) return;
        App.Settings.Current.GuideColor = (string)it.Tag;
        App.Settings.NotifyChanged();
    }
    private void GuideSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingGuide || GuideSpeedLabel == null) return;
        App.Settings.Current.GuideLeadMs = (int)GuideSpeedSlider.Value;
        GuideSpeedLabel.Text = $"{GuideSpeedSlider.Value / 1000.0:0.0}s";
        App.Settings.NotifyChanged();
    }
    private void GuideSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingGuide || GuideSizeLabel == null) return;
        App.Settings.Current.GuideSize = GuideSizeSlider.Value;
        GuideSizeLabel.Text = $"{(int)(GuideSizeSlider.Value * 100)}%";
        App.Settings.NotifyChanged();
    }
    private void GuideTones_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingGuide || GuideTones == null) return;
        App.Settings.Current.GuideTones = GuideTones.IsChecked == true;
        App.Settings.NotifyChanged();
    }

    private int _guideNoteCursor;

    // Key '1'..'8' → a C-major scale pitch, so guide mode plays the melody by ear.
    // A blind player can follow the rhythm and relative pitch and press along —
    // which a purely visual scrolling canvas never allowed.
    private static readonly double[] KeyPitch =
        { 261.63, 293.66, 329.63, 349.23, 392.00, 440.00, 493.88, 523.25 };

    private void PlayGuideTone(char key)
    {
        if (key < '1' || key > '8') return;
        double hz = KeyPitch[key - '1'];
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                int sr = 44100, len = sr * 160 / 1000;
                var buf = new float[len];
                for (int n = 0; n < len; n++)
                {
                    double env = n < len * 0.15 ? n / (len * 0.15)
                               : n > len * 0.6 ? (len - n) / (len * 0.4) : 1.0;
                    buf[n] = (float)(Math.Sin(2 * Math.PI * hz * n / sr) * env * 0.22);
                }
                var prov = new NAudio.Wave.BufferedWaveProvider(
                    NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sr, 1)) { BufferLength = len * 4 + 64 };
                var bytes = new byte[len * 4];
                Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
                prov.AddSamples(bytes, 0, bytes.Length);
                var outp = new NAudio.Wave.WaveOutEvent();
                outp.Init(prov);
                outp.PlaybackStopped += (_, _) => { try { outp.Dispose(); } catch { } };
                outp.Play();
            }
            catch (Exception ex) { CrashLogger.Log("PlayGuideTone", ex); }
        });
    }

    private void GuideStop_Click(object sender, RoutedEventArgs e)
    {
        GuideStartBtn.IsEnabled = true;
        GuideStopBtn.IsEnabled = false;
        MainGuide?.Stop();
    }

    private void GuideTick(object? sender, EventArgs e)
    {
        _guideStartTimeMs += 40;
        var tempo = (float)TempoSlider.Value;
        var elapsed = _guideStartTimeMs * tempo;
        RenderGuideCanvas(elapsed);

        // Sound each note as its moment arrives, so the guide works by ear.
        double cum = 0;
        for (int i = 0; i < _guideNoteCursor && i < _parsedNotes.Count; i++) cum += _parsedNotes[i].BeatMs;
        while (_guideNoteCursor < _parsedNotes.Count && cum <= elapsed)
        {
            var note = _parsedNotes[_guideNoteCursor];
            if (!note.IsRest) PlayGuideTone(note.Key);
            cum += note.BeatMs;
            _guideNoteCursor++;
        }

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
        KeyPressService.ResetSendLog();   // so the log records if input is blocked this run
        _playbackCts = new CancellationTokenSource();

        // Auto-play sends key presses to whatever window has FOCUS. The moment
        // this button is clicked, THIS overlay has focus — so without a pause the
        // notes went to the overlay, not the game, and nothing happened. Count
        // down first (spoken) so the user can click Guild Wars 2 and give it focus.
        try
        {
            for (int c = 3; c >= 1; c--)
            {
                if (_playbackCts.IsCancellationRequested) { AutoStatus.Text = "Cancelled."; return; }
                AutoStatus.Text = $"Click the Guild Wars 2 window now. Playing in {c}…";
                _tts?.SpeakAsync(c == 3 ? $"Click Guild Wars 2. Playing in {c}." : c.ToString());
                await Task.Delay(1000, _playbackCts.Token);
            }
            // Play via the REAL AutoHotkey engine (bundled) — the reliable path
            // that works exactly like the scripts you know. Fall back to our own
            // sender only if AutoHotkey couldn't be extracted for some reason.
            if (_ahk.IsAvailable && _ahk.Play(_parsedNotes, (float)TempoSlider.Value, leadMs: 600))
            {
                AutoStatus.Text = "Playing…";
                while (_ahk.IsPlaying && !_playbackCts.IsCancellationRequested)
                    await Task.Delay(150, _playbackCts.Token);
                AutoStatus.Text = _playbackCts.IsCancellationRequested ? "Stopped." : "Done.";
            }
            else
            {
                AutoStatus.Text = "Playing…";
                KeyPressService.ResetSendLog();
                await _keyPress.PlayAsync(
                    _parsedNotes,
                    (float)TempoSlider.Value,
                    onNoteChange: i => HighlightNote(i),
                    _playbackCts.Token);
                AutoStatus.Text = "Done.";
            }
        }
        catch (OperationCanceledException) { AutoStatus.Text = "Stopped."; }
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
        _ahk.Stop();
        _playbackCts?.Cancel();
        AutoStatus.Text = "Stopped.";
    }

    /// <summary>Relaunch the app elevated so Auto-Play keypresses can reach a GW2
    /// window that is itself running as administrator (Windows blocks input from a
    /// lower-privilege app to a higher one).</summary>
    private void RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath
                      ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) { AutoStatus.Text = "Couldn't find the app path."; return; }

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",   // triggers the UAC elevation prompt
            };
            System.Diagnostics.Process.Start(psi);
            // The new elevated instance will kill this one on startup (single-instance).
            _tts?.SpeakAsync("Restarting as administrator.");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            // User cancelled the UAC prompt, or it failed.
            CrashLogger.Log("RunAsAdmin", ex);
            AutoStatus.Text = "Restart as administrator was cancelled or failed.";
        }
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
        _ahk.Stop();
        _playbackCts?.Cancel();
        _guideTimer?.Stop();
    }
}
