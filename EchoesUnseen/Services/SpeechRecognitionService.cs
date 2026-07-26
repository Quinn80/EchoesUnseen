using EchoesUnseen.Services.Speech;

namespace EchoesUnseen.Services;

/// <summary>
/// Local speech-to-text. This is a thin facade over one of two interchangeable
/// backends, chosen at start time by the user's <c>SttEngine</c> setting:
///
///   • "windows" (default) — <see cref="WindowsSpeechEngine"/>. The classical
///     Windows Speech Recognition HMM: no AI, no downloads, always available.
///
///   • "whisper" — <see cref="WhisperSpeechEngine"/>. OpenAI Whisper running
///     100% locally (inference only) for much higher dictation accuracy, at the
///     cost of a one-time model download. See that class and the project docs
///     for the reasoning and the local-only privacy guarantee.
///
/// The public surface (events, IsListening, StartDictation/StopDictation,
/// Dispose) is unchanged from the original single-engine implementation, so the
/// panels that consume speech need no changes and can freely switch engines.
///
/// Engine choice is read each time StartDictation() is called, so toggling the
/// setting takes effect on the next dictation without an app restart.
/// </summary>
public class SpeechRecognitionService : IDisposable
{
    private ISpeechEngine? _engine;
    private readonly object _lock = new();

    public event EventHandler<string>? TextRecognized;
    public event EventHandler<string>? PartialResult;
    public event EventHandler<string>? RecognitionError;

    public bool IsListening
    {
        get { lock (_lock) return _engine?.IsListening ?? false; }
    }

    public bool StartDictation()
    {
        lock (_lock)
        {
            if (_engine is { IsListening: true }) return true;

            // Tear down any previous (stopped) engine before switching. Its event
            // handlers are closures bound to that instance, so disposing it stops
            // them firing; the new engine below gets its own fresh handlers.
            if (_engine != null) { _engine.Dispose(); _engine = null; }

            _engine = CreateEngine();
            // Reference the facade's event fields at invocation time (not capture
            // their current value), so a handler added after this still fires.
            _engine.TextRecognized   += (_, t) => TextRecognized?.Invoke(this, t);
            _engine.PartialResult    += (_, t) => PartialResult?.Invoke(this, t);
            _engine.RecognitionError += (_, t) => RecognitionError?.Invoke(this, t);

            return _engine.Start();
        }
    }

    public void StopDictation()
    {
        lock (_lock) { _engine?.Stop(); }
    }

    private ISpeechEngine CreateEngine()
    {
        var choice = App.Settings.Current.SttEngine?.Trim().ToLowerInvariant();
        if (choice == "whisper")
        {
            return new WhisperSpeechEngine(
                App.Settings.AppDataDirectory,
                App.Settings.Current.WhisperModel);
        }
        return new WindowsSpeechEngine();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _engine?.Dispose();
            _engine = null;
        }
        GC.SuppressFinalize(this);
    }
}
