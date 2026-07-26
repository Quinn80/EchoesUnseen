using System.IO;
using EchoesUnseen.Models;
using NAudio.Wave;

namespace EchoesUnseen.Services.Tts;

/// <summary>
/// Unified TTS facade. Every panel calls this, not the individual engines.
///
/// AVAILABLE ENGINES:
///   * Piper (default) — local neural TTS that produces natural-sounding voices.
///     Runs entirely on the user's machine; no network calls. Voices are bundled
///     in Resources/Piper/voices/ (or installed via install-piper.ps1).
///   * ElevenLabs — cloud premium TTS. Requires an API key. Sends only text WE
///     generate (wiki readouts, transcripts) — never GW2 game data.
///   * Windows SAPI — classical Windows OS speech synthesis. Robotic but
///     always available, used as the terminal fallback.
///
/// COMPLIANCE NOTE:
///   Piper and ElevenLabs both use neural networks. Neither ever receives
///   Guild Wars 2 game data — they only synthesize text the app generated
///   (Wiki articles, user transcripts, panel readouts). The product website
///   discloses this trade-off so users know what they're opting into.
///
/// ROUTING LOGIC:
///   1. Engine from settings.VoiceEngine ("piper" / "elevenlabs" / "sapi")
///   2. If the preferred engine throws, fall back to the next tier.
///   3. SAPI is the terminal fallback — it's always available on Windows,
///      so we'll never run out of fallbacks.
///
/// PLAYBACK:
///   All audio is decoded via NAudio. WAV plays through WaveFileReader, MP3
///   through Mp3FileReader. One output device at a time: starting a new speech
///   call stops any in-progress speech immediately (the Escape hotkey also
///   calls StopSpeaking for the same behavior).
///
/// THREADING:
///   Synthesis happens on the thread pool. Playback starts on the thread pool,
///   but the _currentOutput field is guarded by a lock so the stop-speaking
///   hotkey from the UI thread can safely interrupt it.
/// </summary>
public class TtsService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PiperTtsEngine _piper;
    private readonly ElevenLabsTtsEngine _elevenLabs;
    private readonly SapiTtsEngine _sapi;

    private readonly object _playbackLock = new();
    private WaveOutEvent? _currentOutput;
    private Stream? _currentStream;
    private CancellationTokenSource? _currentCts;

    /// <summary>Fired when TTS starts/stops so UI can indicate speaking state.</summary>
    public event EventHandler<bool>? SpeakingStateChanged;

    /// <summary>True while an utterance is being synthesized or played.</summary>
    public bool IsSpeaking { get; private set; }

    public TtsService(SettingsService settings)
    {
        _settings = settings;
        _piper = new PiperTtsEngine(settings.AppDataDirectory);
        _elevenLabs = new ElevenLabsTtsEngine(() => _settings.Current.ElevenLabsApiKey);
        _sapi = new SapiTtsEngine();
    }

    /// <summary>
    /// Speak <paramref name="text"/> using the currently-selected engine.
    /// Any in-progress speech is stopped first (replace-mode).
    /// Returns when playback completes, is cancelled, or fails.
    /// </summary>
    public async Task SpeakAsync(string text, CancellationToken externalCt = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Interrupt any current playback first — but this is NOT a user stop,
        // so it must not bump the epoch (that would make a chunked reader think
        // the user cancelled and abort after the first sentence).
        CancelCurrentPlayback();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        lock (_playbackLock) { _currentCts = cts; }

        try
        {
            IsSpeaking = true;
            SpeakingStateChanged?.Invoke(this, true);
            var audio = await SynthesizeWithFallbackAsync(text, cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            await PlayAudioAsync(audio, _settings.Current.Volume, cts.Token);
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            CrashLogger.Log("TtsService.SpeakAsync", ex);
        }
        finally
        {
            IsSpeaking = false;
            SpeakingStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Monotonic counter, bumped every time speech is stopped. Multi-utterance
    /// readers (e.g. the Voice Assistant reading an article chunk by chunk)
    /// capture this before they start and bail out the moment it changes — so a
    /// single Stop press halts the WHOLE read, not just the current sentence.
    /// Without it, StopSpeaking only cancelled the sentence in flight and the
    /// reader's loop immediately spoke the next one, so speech never stopped.
    /// </summary>
    public int StopEpoch => System.Threading.Volatile.Read(ref _stopEpoch);
    private int _stopEpoch;

    /// <summary>
    /// User-initiated stop: bumps <see cref="StopEpoch"/> so multi-utterance
    /// readers abort the whole read, then cancels the sentence in flight.
    /// </summary>
    public void StopSpeaking()
    {
        System.Threading.Interlocked.Increment(ref _stopEpoch);
        CancelCurrentPlayback();
    }

    /// <summary>
    /// Cancel just the utterance currently playing, WITHOUT bumping the epoch.
    /// Used by SpeakAsync to interrupt the previous sentence before starting the
    /// next — which must not look like a user stop to a chunked reader.
    /// </summary>
    private void CancelCurrentPlayback()
    {
        lock (_playbackLock)
        {
            try { _currentCts?.Cancel(); } catch { }
            try { _currentOutput?.Stop(); } catch { }
            try { _currentOutput?.Dispose(); } catch { }
            try { _currentStream?.Dispose(); } catch { }
            _currentOutput = null;
            _currentStream = null;
            _currentCts = null;
        }
    }

    /// <summary>Expose engines for the Settings UI to enumerate voices per engine.</summary>
    /// <summary>True if the Piper engine binary is present in either location.</summary>
    public bool IsPiperInstalled => _piper.IsInstalled;

    /// <summary>The Piper engine, for the Settings voice manager (download / preview).</summary>
    public PiperTtsEngine Piper => _piper;

    /// <summary>
    /// Speak a short sample with a SPECIFIC engine + voice, ignoring the saved
    /// selection — used by the Settings "Preview" buttons so the user can hear a
    /// voice before committing to it.
    /// </summary>
    public async Task PreviewAsync(string engine, string voiceId)
    {
        CancelCurrentPlayback();
        var cts = new CancellationTokenSource();
        lock (_playbackLock) { _currentCts = cts; }
        try
        {
            IsSpeaking = true;
            SpeakingStateChanged?.Invoke(this, true);
            var eng = GetEngine(engine);
            var audio = await eng.SynthesizeAsync(
                "Hello, Commander. This is how I sound.", voiceId, _settings.Current.TtsSpeed, cts.Token);
            if (!cts.Token.IsCancellationRequested)
                await PlayAudioAsync(audio, _settings.Current.Volume, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CrashLogger.Log("TtsService.PreviewAsync", ex); }
        finally { IsSpeaking = false; SpeakingStateChanged?.Invoke(this, false); }
    }

    /// <summary>Re-resolve Piper's exe path after the auto-downloader runs.</summary>
    public void RefreshPiper() => _piper.RefreshExePath();

    public ITtsEngine GetEngine(string name) => name switch
    {
        "piper"      => _piper,
        "elevenlabs" => _elevenLabs,
        "sapi"       => _sapi,
        _            => _piper,
    };

    /// <summary>Enumerate every voice across every engine — used by the Settings
    /// UI to present a single combined dropdown labeled by engine.</summary>
    public async Task<List<VoiceInfo>> GetAllVoicesAsync()
    {
        var all = new List<VoiceInfo>();
        try { all.AddRange(await _piper.GetAvailableVoicesAsync()); }      catch (Exception ex) { CrashLogger.Log("Piper voices", ex); }
        try { all.AddRange(await _elevenLabs.GetAvailableVoicesAsync()); } catch (Exception ex) { CrashLogger.Log("ElevenLabs voices", ex); }
        try { all.AddRange(await _sapi.GetAvailableVoicesAsync()); }       catch (Exception ex) { CrashLogger.Log("SAPI voices", ex); }
        return all;
    }

    /// <summary>Get available voices for the currently-selected engine.</summary>
    public Task<List<VoiceInfo>> GetCurrentEngineVoicesAsync()
        => GetEngine(_settings.Current.VoiceEngine).GetAvailableVoicesAsync();

    // ─── Core: try preferred engine, fall back through the tiers on failure ───

    private async Task<TtsAudio> SynthesizeWithFallbackAsync(string text, CancellationToken ct)
    {
        var preferred = _settings.Current.VoiceEngine;
        var voiceId = _settings.Current.VoiceId;
        var speed = _settings.Current.TtsSpeed;

        // Fallback chain per preferred engine.
        // Piper → SAPI:           if Piper isn't installed yet (binaries missing)
        //                         fall through to robotic-but-always-available SAPI.
        // ElevenLabs → Piper → SAPI: if the cloud is unreachable, try local Piper;
        //                            if Piper isn't installed either, SAPI.
        // SAPI → (terminal):      SAPI ships with Windows and effectively always
        //                         works, so it's the safe last resort.
        var chain = preferred switch
        {
            "elevenlabs" => new[] {
                (engine: (ITtsEngine)_elevenLabs, id: voiceId),
                (engine: (ITtsEngine)_piper,      id: "en_US-lessac-high"),
                (engine: (ITtsEngine)_sapi,       id: ""),
            },
            "sapi" => new[] {
                (engine: (ITtsEngine)_sapi, id: voiceId),
            },
            _ /* piper or default */ => new[] {
                (engine: (ITtsEngine)_piper, id: voiceId),
                (engine: (ITtsEngine)_sapi,  id: ""),
            },
        };

        Exception? lastError = null;
        foreach (var (engine, id) in chain)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await engine.SynthesizeAsync(BuildUtterance(engine, preferred, text), id, speed, ct);
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CrashLogger.Log($"TtsService fallback — {engine.EngineName} failed", ex);
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidOperationException("All TTS engines failed.");
    }

    // One-time-per-session notice: when the user expects a natural voice
    // (Piper or ElevenLabs) but we've fallen back to robotic SAPI, explain
    // why ONCE, prepended to the first fallback utterance. Without this, a
    // blind user just hears a suddenly-robotic voice with no explanation.
    private bool _fallbackNoticeSpoken;
    private string BuildUtterance(ITtsEngine engine, string preferred, string text)
    {
        if (engine.EngineName == "sapi" && preferred != "sapi" && !_fallbackNoticeSpoken)
        {
            _fallbackNoticeSpoken = true;
            var reason = preferred == "piper"
                ? "Natural voice is not installed yet, so I'm using the basic Windows voice. Run the install piper script, or see Settings, to enable the natural voice."
                : "The cloud voice is unavailable, so I'm using the basic Windows voice.";
            return reason + " ... " + text;
        }
        return text;
    }

    // ─── Playback via NAudio ─────────────────────────────────────────────────

    private async Task PlayAudioAsync(TtsAudio audio, float volume, CancellationToken ct)
    {
        // Build the appropriate reader for the format.
        var ms = new MemoryStream(audio.Bytes);
        WaveStream reader = audio.Format == "mp3"
            ? new Mp3FileReader(ms)
            : new WaveFileReader(ms);

        var output = new WaveOutEvent();
        output.Init(reader);
        output.Volume = Math.Clamp(volume, 0f, 1f);

        lock (_playbackLock)
        {
            _currentOutput = output;
            _currentStream = reader;
        }

        var tcs = new TaskCompletionSource<bool>();
        output.PlaybackStopped += (_, _) => tcs.TrySetResult(true);
        output.Play();

        // Await either playback-complete or external cancellation.
        using (ct.Register(() => {
            try { output.Stop(); } catch { }
            tcs.TrySetResult(false);
        }))
        {
            await tcs.Task;
        }

        lock (_playbackLock)
        {
            if (ReferenceEquals(_currentOutput, output))
            {
                try { output.Dispose(); } catch { }
                try { reader.Dispose(); } catch { }
                _currentOutput = null;
                _currentStream = null;
            }
        }
    }

    public void Dispose() => StopSpeaking();
}
