using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EchoesUnseen.Models;
using EchoesUnseen.Services;
using EchoesUnseen.Services.Tts;
using NAudio.Wave;

namespace EchoesUnseen.Views;

/// <summary>
/// Guitar-Hero style playing guide, anchored above the GW2 skill bar. Notes fall
/// down 8 lanes (keys 1–8) to a hit-line; you press the key when a note lands.
///
///   TIMED mode — notes fall on their own at the song's tempo; a soft tone sounds
///                as each reaches the line (follow by ear + sight).
///   STEP  mode — the next note waits at the line; it advances only when you press
///                the correct key (fed in from the KeyWatcher), with a soft "nope"
///                on a wrong key. Truly at your pace.
/// </summary>
public partial class GuideOverlay : UserControl
{
    private TtsService? _tts;

    // Note-key → pitch (C major, C4..C5) for the guide tones.
    private static readonly double[] KeyPitch =
        { 261.63, 293.66, 329.63, 349.23, 392.00, 440.00, 493.88, 523.25 };

    private const int Lanes = 8;
    private double _laneW, _canvasW, _canvasH, _hitY, _noteH;
    private Color _color = Colors.Magenta;

    // Static lane guides + hit-line + labels (kept between renders).
    private readonly List<UIElement> _chrome = new();

    // TIMED
    private DispatcherTimer? _timer;
    private DateTime _startUtc;
    private double _tempo = 1.0;
    private int _leadMs = 2000;
    private List<(int lane, double hitMs, int octave)> _timeline = new();
    private readonly Dictionary<int, Rectangle> _noteRects = new();  // index → rect
    private int _nextSpawn;   // next timeline index not yet given a rect
    private int _nextHit;     // next timeline index not yet "hit" (for tone/flash)
    private double _endMs;

    // STEP
    private bool _stepMode;
    private List<int> _stepLanes = new();    // sequence of lane indices
    private List<int> _stepOctaves = new();  // octave register per step (from 9/0 keys)
    private int _stepCursor;
    private readonly List<Rectangle> _stepRects = new();

    public bool StepActive => _stepMode && Root.Visibility == Visibility.Visible;

    private KeyWatcher? _keys;

    public GuideOverlay()
    {
        InitializeComponent();
    }

    public void AttachServices(TtsService? tts, KeyWatcher? keys)
    {
        _tts = tts;
        _keys = keys;
        if (_keys != null)
            _keys.NumberKeyDown += n => Dispatcher.Invoke(() => OnKeyDown(n));
    }

    // ── Public control ────────────────────────────────────────────────────────
    public void StartTimed(List<ParsedNote> notes, int bpm, double tempo)
    {
        Stop();
        ApplyAppearance();
        _tempo = Math.Clamp(tempo, 0.25, 3.0);
        _leadMs = Math.Clamp(App.Settings.Current.GuideLeadMs, 700, 4000);
        BuildTimeline(notes);
        if (_timeline.Count == 0) return;

        _stepMode = false;
        _nextSpawn = 0; _nextHit = 0;
        _noteRects.Clear();
        TitleText.Text = "Guide — follow the falling notes";
        Root.Visibility = Visibility.Visible;

        _startUtc = DateTime.UtcNow.AddMilliseconds(_leadMs);  // first note leads in
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += TimedTick;
        _timer.Start();
    }

    public void StartStep(List<ParsedNote> notes)
    {
        Stop();
        ApplyAppearance();
        // Walk the notes, applying octave keys (9/0) so each note carries its
        // register. Octave commands are auto-applied in step mode — you press the
        // eight note keys and the guide tracks the register for you.
        _stepLanes = new List<int>();
        _stepOctaves = new List<int>();
        int oct = 0;
        foreach (var n in notes)
        {
            if (!n.IsRest && n.Key == '9') { oct = Math.Max(-1, oct - 1); continue; }
            if (!n.IsRest && n.Key == '0') { oct = Math.Min(1, oct + 1); continue; }
            if (!n.IsRest && n.Key >= '1' && n.Key <= '8') { _stepLanes.Add(n.Key - '1'); _stepOctaves.Add(oct); }
        }
        if (_stepLanes.Count == 0) return;

        _stepMode = true;
        _stepCursor = 0;
        TitleText.Text = "Guide — press the lit key to advance";
        Root.Visibility = Visibility.Visible;
        _keys?.Start();
        RenderStep();
    }

    public void Stop()
    {
        _timer?.Stop(); _timer = null;
        _keys?.Stop();
        _stepMode = false;
        NoteCanvas.Children.Clear();
        _chrome.Clear();
        _noteRects.Clear();
        _stepRects.Clear();
        Root.Visibility = Visibility.Collapsed;
    }

    // ── Appearance / chrome ───────────────────────────────────────────────────
    private void ApplyAppearance()
    {
        var s = App.Settings.Current;
        try
        {
            _color = string.IsNullOrWhiteSpace(s.GuideColor)
                ? (Color)ColorConverter.ConvertFromString(ThemeService.Current.Primary)!
                : (Color)ColorConverter.ConvertFromString(s.GuideColor)!;
        }
        catch { _color = Colors.Magenta; }

        double scale = Math.Clamp(s.GuideSize, 0.6, 1.8);
        _laneW = 54 * scale;
        _canvasW = _laneW * Lanes;
        _canvasH = 280 * scale;
        _hitY = _canvasH - 42 * scale;
        _noteH = 24 * scale;
        NoteCanvas.Width = _canvasW;
        NoteCanvas.Height = _canvasH;

        BuildChrome(scale);
    }

    /// <summary>Draw the lane dividers, hit-line and the 1–8 key labels.</summary>
    private void BuildChrome(double scale)
    {
        NoteCanvas.Children.Clear();
        _chrome.Clear();
        var laneBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        for (int i = 0; i <= Lanes; i++)
        {
            var line = new Line { X1 = i * _laneW, Y1 = 0, X2 = i * _laneW, Y2 = _canvasH,
                Stroke = laneBrush, StrokeThickness = 1 };
            NoteCanvas.Children.Add(line);
        }
        // Hit-line
        var hit = new Line { X1 = 0, Y1 = _hitY, X2 = _canvasW, Y2 = _hitY,
            Stroke = new SolidColorBrush(_color), StrokeThickness = 3, Opacity = 0.9 };
        NoteCanvas.Children.Add(hit);
        // Key labels under each lane
        for (int i = 0; i < Lanes; i++)
        {
            var t = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.White,
                FontWeight = FontWeights.Bold, FontSize = 14 * scale, Width = _laneW, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(t, i * _laneW);
            Canvas.SetTop(t, _hitY + 6);
            NoteCanvas.Children.Add(t);
        }
    }

    private void BuildTimeline(List<ParsedNote> notes)
    {
        _timeline = new List<(int, double, int)>();
        double t = 0;
        int oct = 0;   // octave register: -1 low, 0 mid, +1 high (set by keys 9/0)
        foreach (var n in notes)
        {
            if (!n.IsRest && n.Key == '9') { oct = Math.Max(-1, oct - 1); t += n.BeatMs; continue; }
            if (!n.IsRest && n.Key == '0') { oct = Math.Min(1, oct + 1); t += n.BeatMs; continue; }
            if (!n.IsRest && n.Key >= '1' && n.Key <= '8')
                _timeline.Add((n.Key - '1', t, oct));
            t += Math.Max(1, n.BeatMs);
        }
        _endMs = t;
    }

    // ── TIMED tick ────────────────────────────────────────────────────────────
    private void TimedTick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _startUtc).TotalMilliseconds * _tempo;

        // Spawn rects entering the lead window.
        while (_nextSpawn < _timeline.Count && _timeline[_nextSpawn].hitMs <= elapsed + _leadMs)
        {
            int idx = _nextSpawn++;
            var (lane, _, octave) = _timeline[idx];
            // Tint the note by its octave: dimmer/smaller for the low register, a
            // white outline for the high register — so a 3-octave song reads clearly.
            var rect = new Rectangle
            {
                Width = _laneW - 8, Height = _noteH, RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(OctaveColor(octave)),
                Stroke = octave > 0 ? new SolidColorBrush(Colors.White) : null,
                StrokeThickness = octave > 0 ? 2 : 0,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = _color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.8 },
            };
            Canvas.SetLeft(rect, lane * _laneW + 4);
            NoteCanvas.Children.Add(rect);
            _noteRects[idx] = rect;
        }

        // Position visible rects; note reaches the hit-line exactly at its hitMs.
        var done = new List<int>();
        foreach (var kv in _noteRects)
        {
            var (lane, hitMs, _) = _timeline[kv.Key];
            double y = _hitY - (hitMs - elapsed) / _leadMs * (_hitY - 6) - _noteH / 2;
            Canvas.SetTop(kv.Value, y);
            if (hitMs < elapsed - 160) done.Add(kv.Key);
        }
        foreach (var i in done) { NoteCanvas.Children.Remove(_noteRects[i]); _noteRects.Remove(i); }

        // Hit cues (tone + flash) as notes cross the line.
        while (_nextHit < _timeline.Count && _timeline[_nextHit].hitMs <= elapsed)
        {
            FlashLane(_timeline[_nextHit].lane);
            if (App.Settings.Current.GuideTones) PlayTone(_timeline[_nextHit].lane, _timeline[_nextHit].octave);
            _nextHit++;
        }

        if (elapsed > _endMs + 600)
        {
            Stop();
            _ = _tts?.SpeakAsync("Guide finished.");
        }
    }

    // ── STEP mode ─────────────────────────────────────────────────────────────
    /// <summary>Called (on the UI thread) by MainWindow when a 1–8 key is pressed.</summary>
    public void OnKeyDown(int number)
    {
        if (!_stepMode || _stepCursor >= _stepLanes.Count) return;
        int wanted = _stepLanes[_stepCursor];
        if (number - 1 == wanted)
        {
            FlashLane(wanted);
            if (App.Settings.Current.GuideTones)
                PlayTone(wanted, _stepCursor < _stepOctaves.Count ? _stepOctaves[_stepCursor] : 0);
            _stepCursor++;
            if (_stepCursor >= _stepLanes.Count)
            {
                _ = _tts?.SpeakAsync("Well played. Song complete.");
                Stop();
                return;
            }
            RenderStep();
        }
        else
        {
            PlayTone(-1);   // soft "nope"
        }
    }

    /// <summary>Draw the current note at the hit-line and the next few above it.</summary>
    private void RenderStep()
    {
        foreach (var r in _stepRects) NoteCanvas.Children.Remove(r);
        _stepRects.Clear();

        const int lookahead = 6;
        for (int k = 0; k < lookahead && _stepCursor + k < _stepLanes.Count; k++)
        {
            int lane = _stepLanes[_stepCursor + k];
            bool current = k == 0;
            var rect = new Rectangle
            {
                Width = _laneW - 8, Height = _noteH, RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(current ? _color : Color.FromArgb(0x88, _color.R, _color.G, _color.B)),
                Opacity = current ? 1.0 : 0.6,
            };
            if (current)
                rect.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = _color, BlurRadius = 16, ShadowDepth = 0, Opacity = 1 };
            Canvas.SetLeft(rect, lane * _laneW + 4);
            Canvas.SetTop(rect, _hitY - _noteH / 2 - k * (_noteH + 8));
            NoteCanvas.Children.Add(rect);
            _stepRects.Add(rect);
        }
    }

    // ── Flash + tone ──────────────────────────────────────────────────────────
    private void FlashLane(int lane)
    {
        var flash = new Rectangle { Width = _laneW, Height = 10, Fill = new SolidColorBrush(Colors.White) };
        Canvas.SetLeft(flash, lane * _laneW);
        Canvas.SetTop(flash, _hitY - 5);
        NoteCanvas.Children.Add(flash);
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
        anim.Completed += (_, _) => NoteCanvas.Children.Remove(flash);
        flash.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Note colour for an octave register: low = dimmed, mid = full, high
    /// = brightened, so the three octaves are visually distinct.</summary>
    private Color OctaveColor(int octave)
    {
        if (octave < 0) return Color.FromArgb(0xFF, (byte)(_color.R * 0.55), (byte)(_color.G * 0.55), (byte)(_color.B * 0.55));
        if (octave > 0) return Color.FromArgb(0xFF,
            (byte)(_color.R + (255 - _color.R) * 0.4), (byte)(_color.G + (255 - _color.G) * 0.4), (byte)(_color.B + (255 - _color.B) * 0.4));
        return _color;
    }

    private void PlayTone(int lane, int octave = 0)
    {
        try
        {
            double baseHz = lane >= 0 && lane < 8 ? KeyPitch[lane] : 180; // -1 = low "nope"
            double hz = baseHz * Math.Pow(2, Math.Clamp(octave, -1, 1));   // shift by register
            int sr = 44100, ms = lane < 0 ? 120 : 180;
            int len = sr * ms / 1000;
            var buf = new float[len];
            int edge = sr * 10 / 1000;
            double phase = 0;
            for (int n = 0; n < len; n++)
            {
                phase += 2 * Math.PI * hz / sr;
                double env = 1.0;
                if (n < edge) env = (double)n / edge;
                else if (len - n < edge) env = (double)(len - n) / edge;
                buf[n] = (float)(Math.Sin(phase) * 0.18 * env);
            }
            var bytes = new byte[len * 4];
            Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
            var ms2 = new System.IO.MemoryStream(bytes);
            var raw = new RawSourceWaveStream(ms2, WaveFormat.CreateIeeeFloatWaveFormat(sr, 1));
            var outp = new WaveOutEvent();
            outp.Init(raw);
            outp.PlaybackStopped += (_, _) => { try { outp.Dispose(); } catch { } try { raw.Dispose(); } catch { } try { ms2.Dispose(); } catch { } };
            outp.Play();
        }
        catch { }
    }
}
