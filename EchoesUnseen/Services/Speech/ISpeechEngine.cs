namespace EchoesUnseen.Services.Speech;

/// <summary>
/// A pluggable speech-to-text backend. Two implementations exist:
///
///   • <see cref="WindowsSpeechEngine"/> — System.Speech.Recognition, the
///     classical Hidden Markov Model that ships with Windows. No AI, no
///     downloads, always available. The safe default and fallback.
///
///   • <see cref="WhisperSpeechEngine"/> — OpenAI's Whisper running fully
///     locally (inference only) via Whisper.net. Far higher accuracy on
///     open-vocabulary dictation, at the cost of a one-time model download.
///
/// <see cref="SpeechRecognitionService"/> is a thin facade that picks one of
/// these at start time based on the user's setting, so the two panels that use
/// speech never need to know which engine is running.
///
/// Threading contract: all three events may be raised on a background thread.
/// Consumers marshal to the UI thread themselves (both panels use
/// Dispatcher.Invoke), matching the long-standing System.Speech behaviour.
/// </summary>
internal interface ISpeechEngine : IDisposable
{
    /// <summary>A finalized phrase was transcribed.</summary>
    event EventHandler<string>? TextRecognized;

    /// <summary>Interim text while the user is still speaking (may be empty).</summary>
    event EventHandler<string>? PartialResult;

    /// <summary>A non-fatal problem the UI should surface to the user.</summary>
    event EventHandler<string>? RecognitionError;

    /// <summary>True while the microphone is open and recognition is active.</summary>
    bool IsListening { get; }

    /// <summary>Begin listening. Returns false if the engine could not start.</summary>
    bool Start();

    /// <summary>Stop listening. Idempotent.</summary>
    void Stop();
}
