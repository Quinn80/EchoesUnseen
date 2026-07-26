using NAudio.Wave;

namespace EchoesUnseen.Services;

/// <summary>
/// Short non-speech audio cues ("earcons") that confirm UI actions without
/// words — the audio equivalent of a visual highlight.
///
/// WHY: For blind and low-vision users, a silent UI is an ambiguous UI. A
/// click that produces no sound leaves the user unsure whether it registered.
/// Earcons give instant, language-free confirmation that's faster than TTS.
///
/// DESIGN (v21):
///   * Panel open  → a soft ambient SWELL: two low warm notes (A3 + E4) that
///     FADE IN from silence, overlapping like a breath drawn in (~0.5 s).
///   * Panel close → the mirror: the notes FADE OUT into silence, dissolving
///     away like a breath released (~0.55 s).
///   * Both notes are rendered into one buffer (a gentle chord, not two
///     beeps) with raised-cosine envelopes, so there are no clicks or pops.
///
/// All tones are generated locally with NAudio — no audio files, no network,
/// no AI. Volume follows the user's SonarVolume setting so one slider
/// governs all non-speech audio. The PanelEarcons setting disables them.
/// </summary>
public class EarconService
{
    private readonly SettingsService _settings;

    public EarconService(SettingsService settings) => _settings = settings;

    /// <summary>Soft ambient swell that fades IN: a panel just opened.</summary>
    public void PanelOpened() => PlayAmbient(open: true);

    /// <summary>Soft ambient dissolve that fades OUT: a panel just closed.</summary>
    public void PanelClosed() => PlayAmbient(open: false);

    /// <summary>
    /// A soft startup flourish — a gentle rising arpeggio (D major: D4, A4, F#5)
    /// with a low D3 pad under it, all long fade-ins so it swells in rather than
    /// stabbing. Meant to evoke a warm high-fantasy "the world opens" chord.
    /// Always plays on launch (independent of the PanelEarcons toggle, which is
    /// specifically about panel open/close cues).
    /// </summary>
    public void StartupChime()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var volume = Math.Clamp(_settings.Current.SonarVolume, 0.15f, 1f) * 0.4f;
                var notes = ThemeService.Current.Sound.Startup;
                if (notes.Length == 0) return;

                // Rising arpeggio: a low pad note held under progressively
                // higher, staggered voices that swell in. Uses the active
                // theme's Startup notes so each theme has its own launch flourish.
                var voices = new (float, int, int, int, int)[notes.Length];
                voices[0] = ((float)notes[0], 0, 1500, 400, 700); // pad underneath
                for (int i = 1; i < notes.Length; i++)
                    voices[i] = ((float)notes[i], 120 + (i - 1) * 240, 880, 250, 460);

                PlayVoices(voices, volume);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("EarconService.StartupChime", ex);
            }
        });
    }

    /// <summary>Soft single tone as the pointer enters the app surface (fades in).</summary>
    public void HoverEnter() => PlayHoverTone(ThemeService.Current.Sound.HoverIn, fadeIn: true);

    /// <summary>Soft single tone as the pointer leaves the app surface (fades out).</summary>
    public void HoverLeave() => PlayHoverTone(ThemeService.Current.Sound.HoverOut, fadeIn: false);

    /// <summary>
    /// A single, deliberately gentle cue meaning "the wheel is placed" — played
    /// once after the user stops moving it, never per keypress. Pitched a fifth
    /// below the hover tone and quieter, so it reads as a settle rather than an
    /// alert.
    /// </summary>
    public void HudPlaced()
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var volume = Math.Clamp(_settings.Current.SonarVolume, 0f, 1f) * 0.13f;
                var hz = ThemeService.Current.Sound.HoverOut * 0.75; // softer, lower
                PlayVoices(new[] { ((float)hz, 0, 420, 160, 240) }, volume);
            }
            catch (Exception ex) { CrashLogger.Log("EarconService.HudPlaced", ex); }
        });
    }

    private void PlayHoverTone(double hz, bool fadeIn)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Softened: the old level was too assertive for a cue that fires
                // whenever the pointer crosses the wheel.
                var volume = Math.Clamp(_settings.Current.SonarVolume, 0f, 1f) * 0.13f;
                var voices = fadeIn
                    ? new[] { ((float)hz, 0, 300, 220, 70) }   // swell in
                    : new[] { ((float)hz, 0, 340, 30, 300) };  // melt out
                PlayVoices(voices, volume);
            }
            catch (Exception ex) { CrashLogger.Log("EarconService.PlayHoverTone", ex); }
        });
    }

    private void PlayAmbient(bool open)
    {
        if (!_settings.Current.PanelEarcons) return;

        // Fire-and-forget on the thread pool; each earcon owns its device
        // and disposes it when done, so overlapping earcons can't conflict.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var volume = Math.Clamp(_settings.Current.SonarVolume, 0f, 1f) * 0.35f;
                var s = ThemeService.Current.Sound;
                var notes = open ? s.Open : s.Close;
                if (notes.Length == 0) return;

                // OPEN: long attacks — emerges from silence.
                // CLOSE: quick attack, long release — present, then melting away.
                var voices = new (float, int, int, int, int)[notes.Length];
                for (int i = 0; i < notes.Length; i++)
                {
                    voices[i] = open
                        ? ((float)notes[i], i * 140, 420, 300, 120)
                        : ((float)notes[i], i * 80, 500 + i * 120, 30, 380 + i * 120);
                }

                // A decaying (close) sound reads quieter than a swelling one.
                if (!open) volume = Math.Min(volume * 1.35f, 0.5f);

                PlayVoices(voices, volume);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("EarconService.PlayAmbient", ex);
            }
        });
    }

    /// <summary>
    /// Render a small set of overlapping sine "voices" into ONE buffer and play
    /// it. Each voice has its own asymmetric raised-cosine envelope (separate
    /// fade-in and fade-out lengths) — that asymmetry is what makes the open
    /// cue feel like it fades in and the close cue feel like it fades out.
    /// </summary>
    private static void PlayVoices((float Hz, int StartMs, int LenMs, int FadeInMs, int FadeOutMs)[] voices, float volume)
    {
        const int sampleRate = 44100;
        int totalMs = 0;
        foreach (var v in voices)
            totalMs = Math.Max(totalMs, v.StartMs + v.LenMs);
        int totalSamples = sampleRate * totalMs / 1000;

        var buffer = new float[totalSamples];
        foreach (var v in voices)
        {
            int start = sampleRate * v.StartMs / 1000;
            int len = sampleRate * v.LenMs / 1000;
            int fadeIn = Math.Min(len, sampleRate * v.FadeInMs / 1000);
            int fadeOut = Math.Min(len - 1, sampleRate * v.FadeOutMs / 1000);

            for (int n = 0; n < len && start + n < totalSamples; n++)
            {
                double env = 1.0;
                if (n < fadeIn && fadeIn > 0)
                    env = 0.5 * (1 - Math.Cos(Math.PI * n / fadeIn));          // ease in
                else if (n > len - fadeOut && fadeOut > 0)
                    env = 0.5 * (1 - Math.Cos(Math.PI * (len - n) / fadeOut)); // ease out

                buffer[start + n] += (float)(Math.Sin(2 * Math.PI * v.Hz * n / sampleRate) * env);
            }
        }

        // Two overlapping voices can sum past 1.0 — scale down and clamp so
        // the mix never clips into harshness.
        for (int n = 0; n < totalSamples; n++)
            buffer[n] = Math.Clamp(buffer[n] * 0.62f * volume, -1f, 1f);

        var provider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1))
        {
            BufferLength = totalSamples * 4 + 64,
        };
        var bytes = new byte[totalSamples * 4];
        Buffer.BlockCopy(buffer, 0, bytes, 0, bytes.Length);
        provider.AddSamples(bytes, 0, bytes.Length);

        using var output = new WaveOutEvent();
        output.Init(provider);
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
            Thread.Sleep(10);
    }
}
