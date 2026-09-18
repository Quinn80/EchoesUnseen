using System.IO;
using NAudio.Wave;

namespace EchoesUnseen.Services;

/// <summary>
/// Proximity sonar — a repeating ping whose pitch rises and interval shortens as
/// the player nears a target. For low-vision players this gives continuous
/// direction/distance feedback by ear alone.
///
/// SOFTENED (B1.2): every ping is now rendered into a buffer with a raised-cosine
/// attack/release envelope instead of a hard-edged 80 ms sine, which removes the
/// click that made it feel aggressive. Ten selectable timbres (see
/// <see cref="Profiles"/>) let the user pick something easy on the ear.
///
/// MAPPING (distance → audio):
///   far (2000+ units): low pitch, ~1.8 s between pings
///   here (0 units):    high pitch, ~0.32 s between pings
/// </summary>
public class SonarService : IDisposable
{
    private const int SampleRate = 44100;

    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private bool _running;
    private double _frequency = 440;
    private int _intervalMs = 1800;
    private float _volume = 0.5f;
    private bool _heartbeat;
    private SonarProfile _profile = Profiles[0].Profile;

    /// <summary>Toggle the gentle heartbeat mode (steady slow pulse).</summary>
    public void SetHeartbeat(bool on) => _heartbeat = on;

    // ONE persistent output for the whole ping loop. The old code opened a new
    // WaveOutEvent per ping and relied on PlaybackStopped to dispose it — but a
    // BufferedWaveProvider with ReadFully=true plays silence forever, so that
    // event never fired and every ping leaked an audio handle. Windows caps
    // waveOut handles, so after a while ALL audio (sonar AND TTS) went silent and
    // never came back. A single persistent output that we feed samples fixes it.
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _provider;
    private static readonly WaveFormat Fmt = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

    /// <summary>Pick a timbre by id (falls back to the first profile).</summary>
    public void SetSound(string id)
    {
        foreach (var p in Profiles)
            if (p.Id == id) { _profile = p.Profile; return; }
        _profile = Profiles[0].Profile;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            EnsureOutput();
            ScheduleNextPing();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            _provider = null;
        }
    }

    // Open the single persistent output. Held for the whole sonar session and
    // fed ping samples; silence fills the gaps (ReadFully). Disposed in Stop().
    private void EnsureOutput()
    {
        if (_output != null) return;
        _provider = new BufferedWaveProvider(Fmt)
        {
            BufferDuration = TimeSpan.FromSeconds(4),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _output = new WaveOutEvent { DesiredLatency = 140 };
        _output.Init(_provider);
        _output.Play();
    }

    /// <summary>Play a single ping now — used by the Settings preview button.</summary>
    public void PreviewOnce(string id, float volume)
    {
        SetSound(id);
        _volume = Math.Clamp(volume, 0f, 1f);
        PlayOneShot(Render(_profile, 620, _volume));
    }

    public void UpdateDistance(float distanceGameUnits, float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        var t = Math.Clamp(1f - (distanceGameUnits / 2000f), 0f, 1f);
        if (_heartbeat)
        {
            // Steady, slow, gentle — the rate does NOT speed up. A low, calm tone
            // with only a subtle volume swell as you near, so it sits under the
            // game audio. Closeness comes from the compass and spoken cues.
            _frequency  = 300 + (90 * t);         // 300 → 390 Hz, low and soft
            _intervalMs = 1100;                   // constant ~1.1 s
            _volume    *= 0.85f + 0.15f * t;      // VERY subtle swell near the target
        }
        else
        {
            _frequency  = 392 + (392 * t);           // 392 → 784 Hz
            _intervalMs = (int)(1800 - (1480 * t));  // 1800 → 320 ms
        }
    }

    private void ScheduleNextPing()
    {
        if (!_running) return;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ =>
        {
            PlayPing();
            ScheduleNextPing();
        }, null, _intervalMs, Timeout.Infinite);
    }

    private void PlayPing()
    {
        try
        {
            BufferedWaveProvider? prov;
            lock (_lock) { prov = _provider; }
            if (prov == null) return;

            var buffer = Render(_profile, _frequency, _volume);
            var bytes = new byte[buffer.Length * 4];
            Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
            prov.AddSamples(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SonarService.PlayPing", ex);
        }
    }

    // ── One-shot playback (previews, arrival/heart confirmations) ─────────────
    /// <summary>Play a finite buffer once and dispose everything when it ends.
    /// Uses a finite RawSourceWaveStream so PlaybackStopped fires reliably —
    /// unlike a ReadFully BufferedWaveProvider, which never ends and leaks.</summary>
    private void PlayOneShot(float[] buffer)
    {
        try
        {
            var bytes = new byte[buffer.Length * 4];
            Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
            var ms = new MemoryStream(bytes);
            var raw = new RawSourceWaveStream(ms, Fmt);
            var output = new WaveOutEvent();
            output.Init(raw);
            output.PlaybackStopped += (_, _) =>
            {
                try { output.Dispose(); } catch { }
                try { raw.Dispose(); } catch { }
                try { ms.Dispose(); } catch { }
            };
            output.Play();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SonarService.PlayOneShot", ex);
        }
    }

    /// <summary>A distinct confirmation cue, clearly different from the pings:
    /// a bright rising arpeggio for reaching a target, and a fuller fanfare for
    /// completing a heart. Not a ping timbre, so it can't be mistaken for one.</summary>
    public void PlayConfirmation(bool heartComplete)
    {
        float vol = Math.Clamp(_volume <= 0 ? 0.5f : _volume, 0.2f, 1f);
        double[] notes = heartComplete
            ? new[] { 523.25, 659.25, 783.99, 1046.50 }   // C E G C — fanfare
            : new[] { 659.25, 987.77 };                    // E B — quick "got it"
        PlayOneShot(RenderMelody(notes, heartComplete ? 150 : 120, vol));
    }

    /// <summary>Render a short sequence of pure-ish tones into one buffer, each
    /// note with a soft raised-cosine envelope so it sounds pleasant.</summary>
    private static float[] RenderMelody(double[] freqs, int noteMs, float volume)
    {
        int noteLen = SampleRate * noteMs / 1000;
        var buf = new float[noteLen * freqs.Length];
        int edge = SampleRate * 12 / 1000;   // 12 ms fade in/out
        for (int i = 0; i < freqs.Length; i++)
        {
            double phase = 0;
            for (int n = 0; n < noteLen; n++)
            {
                phase += 2 * Math.PI * freqs[i] / SampleRate;
                double env = 1.0;
                if (n < edge) env = 0.5 * (1 - Math.Cos(Math.PI * n / edge));
                else if (noteLen - n < edge) env = 0.5 * (1 - Math.Cos(Math.PI * (noteLen - n) / edge));
                double s = Math.Sin(phase) + 0.25 * Math.Sin(2 * phase);
                buf[i * noteLen + n] = (float)Math.Clamp(s * env * 0.22 * volume, -1.0, 1.0);
            }
        }
        return buf;
    }

    /// <summary>
    /// Render one ping to a float buffer: base tone plus an optional harmonic
    /// partial, shaped by a raised-cosine attack and release, with optional decay
    /// (pluck/bell) and a downward pitch glide (bubble).
    /// </summary>
    private static float[] Render(SonarProfile p, double freq, float volume)
    {
        int len = SampleRate * p.LengthMs / 1000;
        int attack = SampleRate * p.AttackMs / 1000;
        int release = SampleRate * p.ReleaseMs / 1000;
        var buf = new float[len];

        double phase = 0, phase2 = 0;
        for (int n = 0; n < len; n++)
        {
            double glide = 1.0 - p.PitchDrop * (n / (double)len);   // 1.0 → (1-drop)
            double f = freq * glide;
            double f2 = f * p.Partial2Ratio;

            phase  += 2 * Math.PI * f  / SampleRate;
            phase2 += 2 * Math.PI * f2 / SampleRate;

            double s = Wave(p.Wave, phase) + p.Partial2Level * Wave(p.Wave, phase2);

            double env;
            if (n < attack && attack > 0)
                env = 0.5 * (1 - Math.Cos(Math.PI * n / attack));            // ease in
            else
            {
                int r = len - n;
                env = r < release && release > 0
                    ? 0.5 * (1 - Math.Cos(Math.PI * r / release))            // ease out
                    : 1.0;
                if (p.Decay > 0) env *= Math.Exp(-p.Decay * (n - attack) / (double)SampleRate);
            }

            buf[n] = (float)Math.Clamp(s * env * p.Gain * volume, -1.0, 1.0);
        }
        return buf;
    }

    private static double Wave(SonarWave w, double phase) => w switch
    {
        SonarWave.Sine     => Math.Sin(phase),
        SonarWave.Triangle => 2.0 / Math.PI * Math.Asin(Math.Sin(phase)),
        SonarWave.Square   => Math.Sin(phase) >= 0 ? 0.7 : -0.7,
        _ => Math.Sin(phase),
    };

    public void Dispose() => Stop();

    // ── Timbre profiles ──────────────────────────────────────────────────────
    public enum SonarWave { Sine, Triangle, Square }

    public readonly record struct SonarProfile(
        SonarWave Wave, int LengthMs, int AttackMs, int ReleaseMs,
        double Decay, double Partial2Ratio, double Partial2Level,
        double PitchDrop, double Gain);

    /// <summary>The ten selectable sonar sounds (id, friendly name, profile).</summary>
    public static readonly IReadOnlyList<(string Id, string Name, SonarProfile Profile)> Profiles = new[]
    {
        ("soft-sine", "Soft Sine (gentle, default)",
            new SonarProfile(SonarWave.Sine,     150, 18, 90,  0,  1,    0,    0,    0.16)),
        ("marimba",   "Marimba",
            new SonarProfile(SonarWave.Sine,     220, 4,  40,  9,  2.0,  0.30, 0,    0.20)),
        ("woodblock", "Woodblock",
            new SonarProfile(SonarWave.Triangle, 120, 2,  30, 16,  3.0,  0.15, 0,    0.22)),
        ("chime",     "Chime",
            new SonarProfile(SonarWave.Sine,     320, 6, 180,  4,  1.5,  0.35, 0,    0.15)),
        ("bell",      "Bell",
            new SonarProfile(SonarWave.Sine,     380, 4, 240,  3,  2.76, 0.28, 0,    0.14)),
        ("pluck",     "Pluck",
            new SonarProfile(SonarWave.Triangle, 200, 3,  60,  8,  1,    0,    0,    0.20)),
        ("submarine", "Submarine",
            new SonarProfile(SonarWave.Sine,     300, 40,160,  0,  1,    0,    0.03, 0.18)),
        ("beep",      "Digital Beep",
            new SonarProfile(SonarWave.Square,   110, 6,  40,  0,  1,    0,    0,    0.12)),
        ("bubble",    "Bubble",
            new SonarProfile(SonarWave.Sine,     160, 8,  70,  0,  1,    0,    0.18, 0.18)),
        ("ping",      "Sonar Ping",
            new SonarProfile(SonarWave.Sine,     260, 4, 150,  2,  1,    0,    0.06, 0.16)),
    };
}
