using System.Globalization;
using System.Speech.Recognition;

namespace EchoesUnseen.Services.Speech;

/// <summary>
/// Local speech-to-text via Windows' built-in Speech Recognition engine
/// (System.Speech.Recognition) — a classical Hidden Markov Model that has
/// shipped with Windows since Windows 7. No neural networks, no cloud, no AI,
/// no downloads. This is the same engine that powers the OS "Windows Speech
/// Recognition" accessibility feature, so screen-reader users may already have
/// trained a profile for it.
///
/// It is accurate on short, constrained utterances but weak on open-vocabulary
/// dictation. Users who need better dictation accuracy can switch to
/// <see cref="WhisperSpeechEngine"/> in Settings.
///
/// NOTE ON MIC SELECTION: System.Speech binds to the system DEFAULT recording
/// device and offers no clean per-device selection, so the user's chosen
/// microphone is honoured only by the Whisper engine. If a specific mic is
/// required here, set it as the Windows default input device.
///
/// This is the original SpeechRecognitionService implementation, relocated
/// behind <see cref="ISpeechEngine"/> without behavioural change.
/// </summary>
internal sealed class WindowsSpeechEngine : ISpeechEngine
{
    private SpeechRecognitionEngine? _engine;
    private readonly object _lock = new();

    public event EventHandler<string>? TextRecognized;
    public event EventHandler<string>? PartialResult;
    public event EventHandler<string>? RecognitionError;

    public bool IsListening { get; private set; }

    public bool Start()
    {
        lock (_lock)
        {
            if (IsListening) return true;
            try
            {
                var culture = new CultureInfo("en-US");
                _engine = TryCreateEngine(culture);
                if (_engine == null)
                {
                    RecognitionError?.Invoke(this,
                        "No Windows Speech Recognition engine is installed for English. " +
                        "Install one via Settings → Time & Language → Speech → Add a voice.");
                    return false;
                }

                _engine.LoadGrammar(new DictationGrammar());
                _engine.SpeechRecognized += OnSpeechRecognized;
                _engine.SpeechHypothesized += OnSpeechHypothesized;
                _engine.SpeechRecognitionRejected += OnRecognitionRejected;
                _engine.RecognizeCompleted += OnRecognizeCompleted;
                _engine.SetInputToDefaultAudioDevice();
                _engine.RecognizeAsync(RecognizeMode.Multiple);
                IsListening = true;
                return true;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("WindowsSpeechEngine.Start", ex);
                RecognitionError?.Invoke(this, $"Could not start speech recognition: {ex.Message}");
                Cleanup();
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsListening) return;
            try { _engine?.RecognizeAsyncCancel(); }
            catch (Exception ex) { CrashLogger.Log("WindowsSpeechEngine.Stop", ex); }
            IsListening = false;
            Cleanup();
        }
    }

    private static SpeechRecognitionEngine? TryCreateEngine(CultureInfo culture)
    {
        try
        {
            return new SpeechRecognitionEngine(culture);
        }
        catch
        {
            try
            {
                var installed = SpeechRecognitionEngine.InstalledRecognizers();
                if (installed.Count == 0) return null;
                return new SpeechRecognitionEngine(installed[0].Culture);
            }
            catch { return null; }
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result?.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text)) TextRecognized?.Invoke(this, text);
    }

    private void OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        var text = e.Result?.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text)) PartialResult?.Invoke(this, text);
    }

    private void OnRecognitionRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        // Heard something but couldn't transcribe it confidently. Not an error.
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null)
        {
            CrashLogger.Log("WindowsSpeechEngine.OnRecognizeCompleted", e.Error);
            RecognitionError?.Invoke(this, e.Error.Message);
        }
    }

    private void Cleanup()
    {
        try
        {
            if (_engine != null)
            {
                _engine.SpeechRecognized -= OnSpeechRecognized;
                _engine.SpeechHypothesized -= OnSpeechHypothesized;
                _engine.SpeechRecognitionRejected -= OnRecognitionRejected;
                _engine.RecognizeCompleted -= OnRecognizeCompleted;
                _engine.Dispose();
                _engine = null;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WindowsSpeechEngine.Cleanup", ex);
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
