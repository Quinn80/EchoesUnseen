using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using Whisper.net;

namespace EchoesUnseen.Services.Speech;

/// <summary>
/// Local, high-accuracy speech-to-text using OpenAI's Whisper model running
/// entirely on this machine via Whisper.net (inference only — nothing is
/// trained, and no audio or text ever leaves the device).
///
/// LOW-LATENCY DESIGN
/// ------------------
/// Whisper transcribes a chunk of audio; it is not a streaming recogniser. If we
/// waited until the user stopped dictating and then transcribed everything at
/// once, the delay would scale with how long they spoke. Instead we segment on
/// natural pauses:
///
///   1. NAudio captures the mic at 16 kHz mono (Whisper's native rate).
///   2. A simple energy VAD watches each ~100 ms frame. Speech is accumulated;
///      once ~700 ms of trailing silence follows real speech, that phrase is
///      finalized.
///   3. The finalized phrase — usually only a few seconds long — is handed to a
///      single background worker that runs Whisper and raises TextRecognized.
///
/// Because each segment is short, transcription returns in a fraction of a
/// second on a modern CPU, so results feel near-immediate rather than arriving
/// in one lump at the end.
///
/// A small pre-roll (the audio just before speech onset) is prepended to each
/// segment so the first word is never clipped.
/// </summary>
internal sealed class WhisperSpeechEngine : ISpeechEngine
{
    // ── Audio format (Whisper wants 16 kHz mono) ─────────────────────────────
    private const int SampleRate = 16000;
    private const int Bits = 16;
    private const int Channels = 1;
    private const int FrameMs = 100;                         // NAudio buffer size
    private const int BytesPerSecond = SampleRate * (Bits / 8) * Channels;

    // ── VAD tuning ───────────────────────────────────────────────────────────
    // RMS on the 16-bit scale (0..32767). Quiet rooms sit well under 300; normal
    // speech is well over 1000. 500 is a deliberately forgiving threshold.
    private const double SpeechRmsThreshold = 500;
    private const int SilenceHangoverMs = 700;               // pause that ends a phrase
    private const int MinSpeechMs = 250;                     // ignore blips shorter than this
    private const int MaxSegmentMs = 12000;                  // force-flush very long runs
    private const int PreRollFrames = 2;                     // ~200 ms kept before onset

    private readonly string _appDataDir;
    private readonly string _modelKey;

    public event EventHandler<string>? TextRecognized;
    public event EventHandler<string>? PartialResult;
    public event EventHandler<string>? RecognitionError;

    public bool IsListening { get; private set; }

    private readonly object _lock = new();
    private WaveInEvent? _waveIn;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _segments;
    private Task? _worker;

    // VAD state (touched only on NAudio's capture thread)
    private readonly MemoryStream _segment = new();
    private readonly Queue<byte[]> _preRoll = new();
    private bool _inSpeech;
    private int _silenceMs;
    private int _speechMs;

    public WhisperSpeechEngine(string appDataDir, string modelKey)
    {
        _appDataDir = appDataDir;
        _modelKey = string.IsNullOrWhiteSpace(modelKey) ? "base.en" : modelKey;
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (IsListening) return true;
            IsListening = true;
            _cts = new CancellationTokenSource();
            // Model load + mic open can block (first run downloads the model), so
            // do it off the UI thread. Report readiness/failure through events.
            _ = InitializeAsync(_cts.Token);
            return true;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var installer = new WhisperModelInstaller(_appDataDir);
            installer.Progress += (_, msg) => PartialResult?.Invoke(this, msg);

            if (!installer.IsInstalled(_modelKey))
                PartialResult?.Invoke(this, "Preparing speech recognition…");

            var modelPath = await installer.EnsureAsync(_modelKey, ct);
            if (modelPath == null)
            {
                Fail("The speech model isn't available. Switch to Windows speech in Settings.");
                return;
            }
            if (ct.IsCancellationRequested) return;

            // Load the model into memory once; reuse the processor for every
            // segment (guarded by the single-worker queue, so never concurrent).
            _factory = WhisperFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder().WithLanguage("en").Build();

            // Single-consumer queue: finalized phrases in, transcriptions out, in
            // order, with no overlapping Whisper calls.
            _segments = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            _worker = Task.Run(() => TranscribeLoopAsync(ct), ct);

            StartCapture();
            PartialResult?.Invoke(this, "Listening…");
        }
        catch (OperationCanceledException) { /* stopped during init */ }
        catch (Exception ex)
        {
            CrashLogger.Log("WhisperSpeechEngine.InitializeAsync", ex);
            Fail($"Could not start Whisper speech recognition: {ex.Message}");
        }
    }

    private void StartCapture()
    {
        var deviceNumber = ResolveMicDevice(App.Settings.Current.SelectedMicName);
        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(SampleRate, Bits, Channels),
            BufferMilliseconds = FrameMs,
            NumberOfBuffers = 3,
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                CrashLogger.Log("WhisperSpeechEngine.RecordingStopped", e.Exception);
                Fail($"Microphone error: {e.Exception.Message}");
            }
        };
        _waveIn.StartRecording();
    }

    /// <summary>
    /// Resolve the user's saved microphone NAME to an NAudio device index. Falls
    /// back to the system default (-1) if the saved device is missing or unset —
    /// unlike the Windows engine, the chosen mic is genuinely honoured here.
    /// </summary>
    private static int ResolveMicDevice(string micName)
    {
        if (string.IsNullOrWhiteSpace(micName)) return -1;
        try
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var caps = WaveInEvent.GetCapabilities(i);
                // NAudio truncates ProductName to 31 chars, so match on a prefix.
                if (micName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase) ||
                    caps.ProductName.StartsWith(micName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }
        catch (Exception ex) { CrashLogger.Log("WhisperSpeechEngine.ResolveMicDevice", ex); }
        return -1;
    }

    // ── Capture thread: energy VAD + segmentation ────────────────────────────
    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var rms = Rms(e.Buffer, e.BytesRecorded);
            var frame = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, frame, e.BytesRecorded);

            if (rms >= SpeechRmsThreshold)
            {
                if (!_inSpeech)
                {
                    // Speech onset: prepend the pre-roll so the first word survives.
                    _inSpeech = true;
                    while (_preRoll.Count > 0)
                    {
                        var pr = _preRoll.Dequeue();
                        _segment.Write(pr, 0, pr.Length);
                    }
                }
                _segment.Write(frame, 0, frame.Length);
                _speechMs += FrameMs;
                _silenceMs = 0;

                if (_speechMs >= MaxSegmentMs) FinalizeSegment();
            }
            else if (_inSpeech)
            {
                // Trailing silence: keep it (it helps Whisper) but count it down.
                _segment.Write(frame, 0, frame.Length);
                _silenceMs += FrameMs;
                if (_silenceMs >= SilenceHangoverMs) FinalizeSegment();
            }
            else
            {
                // Idle silence: maintain a short pre-roll ring.
                _preRoll.Enqueue(frame);
                while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log("WhisperSpeechEngine.OnData", ex);
        }
    }

    private void FinalizeSegment()
    {
        var hadSpeech = _speechMs >= MinSpeechMs;
        var bytes = hadSpeech ? _segment.ToArray() : null;

        _segment.SetLength(0);
        _inSpeech = false;
        _silenceMs = 0;
        _speechMs = 0;

        if (bytes != null) _segments?.Writer.TryWrite(bytes);
    }

    private static double Rms(byte[] buffer, int count)
    {
        if (count < 2) return 0;
        double sumSq = 0;
        int samples = count / 2;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSq += (double)s * s;
        }
        return Math.Sqrt(sumSq / samples);
    }

    // ── Worker thread: run Whisper on each finalized phrase ───────────────────
    private async Task TranscribeLoopAsync(CancellationToken ct)
    {
        var reader = _segments!.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var pcm))
                {
                    if (ct.IsCancellationRequested) return;
                    await TranscribeAsync(pcm, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* normal on Stop */ }
        catch (Exception ex)
        {
            CrashLogger.Log("WhisperSpeechEngine.TranscribeLoop", ex);
        }
    }

    private async Task TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        var processor = _processor;
        if (processor == null) return;
        try
        {
            using var wav = new MemoryStream();
            WriteWavHeader(wav, pcm.Length);
            wav.Write(pcm, 0, pcm.Length);
            wav.Position = 0;

            var sb = new System.Text.StringBuilder();
            await foreach (var seg in processor.ProcessAsync(wav, ct))
                sb.Append(seg.Text);

            var text = CleanUp(sb.ToString());
            if (!string.IsNullOrWhiteSpace(text))
                TextRecognized?.Invoke(this, text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLogger.Log("WhisperSpeechEngine.Transcribe", ex);
        }
    }

    /// <summary>
    /// Trim Whisper's leading space and drop the non-speech markers it emits for
    /// silence or noise (e.g. "[BLANK_AUDIO]", "(music)"), which would otherwise
    /// be typed into the user's chat.
    /// </summary>
    private static string CleanUp(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0) return t;
        if ((t.StartsWith('[') && t.EndsWith(']')) ||
            (t.StartsWith('(') && t.EndsWith(')')) ||
            (t.StartsWith('*') && t.EndsWith('*')))
            return string.Empty;
        return t;
    }

    private static void WriteWavHeader(Stream s, int dataLen)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        int byteRate = BytesPerSecond;
        short blockAlign = (short)(Channels * (Bits / 8));
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write((short)Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write((short)Bits);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);
    }

    private void Fail(string message)
    {
        RecognitionError?.Invoke(this, message);
        Stop();
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!IsListening) return;
            IsListening = false;

            try { _cts?.Cancel(); } catch { }
            try { if (_waveIn != null) { _waveIn.DataAvailable -= OnData; _waveIn.StopRecording(); _waveIn.Dispose(); } }
            catch (Exception ex) { CrashLogger.Log("WhisperSpeechEngine.Stop.waveIn", ex); }
            _waveIn = null;

            try { _segments?.Writer.TryComplete(); } catch { }

            try { _processor?.Dispose(); } catch (Exception ex) { CrashLogger.Log("WhisperSpeechEngine.Stop.processor", ex); }
            try { _factory?.Dispose(); }   catch (Exception ex) { CrashLogger.Log("WhisperSpeechEngine.Stop.factory", ex); }
            _processor = null;
            _factory = null;

            _segment.SetLength(0);
            _preRoll.Clear();
            _inSpeech = false;
            _silenceMs = 0;
            _speechMs = 0;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
