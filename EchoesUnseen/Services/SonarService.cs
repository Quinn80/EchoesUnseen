using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoesUnseen.Services;

/// <summary>
/// Proximity sonar — plays a repeating ping tone whose pitch rises and
/// interval shortens as the player approaches a target.
///
/// ACCESSIBILITY RATIONALE:
///   For visually impaired players, the sonar provides continuous audio
///   feedback about direction and distance without requiring them to check
///   the minimap. The frequency/interval mapping is tuned to feel intuitive:
///   a fast high beep means "almost there", a slow low beep means "far off".
///
/// MAPPING (distance → audio):
///   2000+ game units: 440 Hz @ 2000 ms interval (slow, low — "far")
///   1000 units:      ~600 Hz @ ~1150 ms interval (medium)
///   500 units:       ~770 Hz @ ~775 ms interval (getting close)
///   0 units:         880 Hz @ 300 ms interval (fast, high — "here")
///
/// THREADING:
///   A single <see cref="Timer"/> drives the pings on the thread pool.
///   <see cref="WaveOutEvent"/> instances are created per-ping because
///   reusing a single one would require fighting NAudio's playback-state
///   machine. Creating a new 80ms tone each ping is cheap and robust.
/// </summary>
public class SonarService : IDisposable
{
    private System.Threading.Timer? _timer;
    private readonly object _lock = new();
    private bool _running;
    private int _frequency = 440;
    private int _intervalMs = 2000;
    private float _volume = 0.5f;

    /// <summary>Start the sonar (idempotent).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            ScheduleNextPing();
        }
    }

    /// <summary>Stop the sonar and dispose the current timer.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// Update the target's distance and volume.
    /// Call whenever the player's position updates (every ~2 seconds from
    /// the Trail Navigator's MumbleLink poll).
    /// </summary>
    public void UpdateDistance(float distanceGameUnits, float volume)
    {
        _volume = Math.Clamp(volume, 0f, 1f);
        var t = Math.Clamp(1f - (distanceGameUnits / 2000f), 0f, 1f);
        _frequency  = (int)(440 + (440 * t));        // 440 → 880 Hz
        _intervalMs = (int)(2000 - (1700 * t));      // 2000 → 300 ms
    }

    private void ScheduleNextPing()
    {
        if (!_running) return;
        _timer?.Dispose();
        _timer = new System.Threading.Timer(_ =>
        {
            PlayPing();
            // Reschedule based on CURRENT interval (distance may have changed between pings)
            ScheduleNextPing();
        }, null, _intervalMs, Timeout.Infinite);
    }

    private void PlayPing()
    {
        if (!_running) return;
        try
        {
            var tone = new SignalGenerator(44100, 1)
            {
                Gain = _volume * 0.3f,       // Cap because sine at full gain is piercing
                Frequency = _frequency,
                Type = SignalGeneratorType.Sin,
            }.Take(TimeSpan.FromMilliseconds(80));

            var output = new WaveOutEvent();
            output.Init(tone);

            // Auto-dispose after playback
            output.PlaybackStopped += (_, _) =>
            {
                try { output.Dispose(); } catch { }
            };
            output.Play();
        }
        catch (Exception ex)
        {
            CrashLogger.Log("SonarService.PlayPing", ex);
        }
    }

    public void Dispose() => Stop();
}
