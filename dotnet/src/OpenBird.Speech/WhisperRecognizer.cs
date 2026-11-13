using OpenBird.Core;
using SherpaOnnx;

namespace OpenBird.Speech;

/// <summary>Offline Whisper (base.en, int8) through sherpa-onnx. Runs on x64 and ARM64 with no network.</summary>
public sealed class WhisperRecognizer : ISpeechRecognizer, IDisposable
{
    private readonly OfflineRecognizer _recognizer;
    private readonly object _gate = new();

    public WhisperRecognizer(string modelDir, int threads = 4)
    {
        var missing = WhisperModel.MissingFiles(modelDir);
        if (missing.Count > 0)
            throw new FileNotFoundException($"Speech model files missing in {modelDir}: {string.Join(", ", missing)}");

        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Whisper.Encoder = Path.Combine(modelDir, WhisperModel.Encoder);
        config.ModelConfig.Whisper.Decoder = Path.Combine(modelDir, WhisperModel.Decoder);
        config.ModelConfig.Whisper.Language = "en";
        config.ModelConfig.Whisper.Task = "transcribe";
        config.ModelConfig.Tokens = Path.Combine(modelDir, WhisperModel.Tokens);
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    public string Recognize(float[] samples, int sampleRate)
    {
        lock (_gate)    // one decode at a time; the engine uses its own threads internally
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text?.Trim() ?? "";
        }
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>
/// Loads the Whisper model on first use, so the app starts instantly and uses no
/// model memory until a meeting needs transcribing. Throws FileNotFoundException
/// while the model is not installed, which the meeting pipeline reports as a warning.
/// </summary>
public sealed class LazyWhisperTranscriber(Func<string> modelDir, Func<int> threads) : ITranscriber, IDisposable
{
    private readonly object _gate = new();
    private WhisperRecognizer? _recognizer;
    private string? _loadedFrom;

    public IReadOnlyList<DialogueTurn> Transcribe(string wavPath, string hostSpeaker, string remoteSpeaker)
    {
        WhisperRecognizer recognizer;
        lock (_gate)
        {
            var dir = modelDir();
            if (_recognizer is null || _loadedFrom != dir)
            {
                _recognizer?.Dispose();
                _recognizer = new WhisperRecognizer(dir, threads());
                _loadedFrom = dir;
            }
            recognizer = _recognizer;
        }
        return new DualChannelTranscriber(recognizer).Transcribe(wavPath, hostSpeaker, remoteSpeaker);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
