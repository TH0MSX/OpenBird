using System.Buffers.Binary;
using System.Globalization;

namespace OpenBird.Core;

public static class AudioMath
{
    public static float Rms(ReadOnlySpan<float> block)
    {
        if (block.Length == 0) return 0;
        double sum = 0;
        foreach (var s in block) sum += (double)s * s;
        return (float)Math.Sqrt(sum / block.Length);
    }

    /// <summary>
    /// True when a microphone block sounds like speech. Rejects quiet blocks,
    /// isolated clicks (very high peak for the loudness) and steady hiss (high
    /// zero-crossing rate). Port of the Python `is_speech_block`.
    /// </summary>
    public static bool IsSpeech(ReadOnlySpan<float> block, float rms, float threshold)
    {
        if (rms < threshold) return false;
        float peak = 0;
        foreach (var s in block) peak = Math.Max(peak, Math.Abs(s));
        if (peak / (rms + 1e-6f) > 6.8f && rms < threshold * 2.2f) return false;

        if (block.Length > 1)
        {
            double changes = 0;
            for (var i = 1; i < block.Length; i++) changes += Math.Abs(Math.Sign(block[i]) - Math.Sign(block[i - 1]));
            if (changes / (block.Length - 1) / 2.0 > 0.42) return false;
        }
        return true;
    }
}

/// <summary>Streams a two-channel 16-bit WAV to disk (channel 0 = microphone, channel 1 = system audio).</summary>
public sealed class StereoWavWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly int _sampleRate;
    private long _frames;

    public StereoWavWriter(string path, int sampleRate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _sampleRate = sampleRate;
        _file.Write(new byte[44]);   // header is written on close, when the length is known
    }

    public long Frames => _frames;

    public void Write(ReadOnlySpan<float> mic, ReadOnlySpan<float> loop)
    {
        var n = Math.Max(mic.Length, loop.Length);
        var buffer = new byte[n * 4];
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 4), ToPcm(i < mic.Length ? mic[i] : 0));
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 4 + 2), ToPcm(i < loop.Length ? loop[i] : 0));
        }
        _file.Write(buffer);
        _frames += n;
    }

    private static short ToPcm(float sample) => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);

    public void Dispose()
    {
        var dataBytes = (uint)(_frames * 4);
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(h[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);                       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 2);                       // channels
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)_sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)_sampleRate * 4);   // bytes per second
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], 4);                       // block align
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);                      // bits per sample
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataBytes);
        _file.Position = 0;
        _file.Write(h);
        _file.Dispose();
    }
}

public sealed record WavAudio(int SampleRate, float[][] Channels)
{
    public int Frames => Channels.Length == 0 ? 0 : Channels[0].Length;
}

public static class WavReader
{
    /// <summary>Reads PCM 16-bit or IEEE float 32-bit WAV files (what OpenBird and the Python app write).</summary>
    public static WavAudio Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"{path} is not a WAV file.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4)), (uint)(bytes.Length - pos - 8));
            var body = bytes.AsSpan(pos + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);   // WAVE_FORMAT_EXTENSIBLE
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0) throw new InvalidDataException($"{path}: data chunk before format chunk.");
                var bytesPerSample = bits / 8;
                var frames = size / (bytesPerSample * channels);
                var result = new float[channels][];
                for (var c = 0; c < channels; c++) result[c] = new float[frames];
                for (var f = 0; f < frames; f++)
                    for (var c = 0; c < channels; c++)
                    {
                        var sample = body.Slice((f * channels + c) * bytesPerSample, bytesPerSample);
                        result[c][f] = (format, bits) switch
                        {
                            (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                            (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(sample),
                            _ => throw new NotSupportedException($"{path}: WAV format {format} with {bits} bits is not supported."),
                        };
                    }
                return new WavAudio(sampleRate, result);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"{path} has no audio data.");
    }
}

public sealed class RecorderOptions
{
    public int SampleRate { get; init; } = 16000;
    public float SpeechThreshold { get; init; } = 0.015f;
    public double MinSpeechSeconds { get; init; } = 2.0;
    public double SilenceTimeoutSeconds { get; init; } = 25.0;
    public double PreRollSeconds { get; init; } = 3.0;
    public bool AutoStart { get; set; } = true;
    /// <summary>When false, only speech on the microphone starts a meeting; a video or music playing does not.</summary>
    public bool StartOnSystemAudio { get; set; } = true;
    public required string OutputDir { get; init; }

    public static RecorderOptions From(AppSettings s) => new()
    {
        SampleRate = s.SampleRate,
        SpeechThreshold = (float)s.AutoTranscribeThreshold,
        MinSpeechSeconds = s.MinSpeechSeconds,
        SilenceTimeoutSeconds = s.SilenceTimeoutSeconds,
        AutoStart = s.AutoTranscribe,
        StartOnSystemAudio = s.AutoStartOnSystemAudio,
        OutputDir = s.RecordingsDir,
    };
}

/// <summary>
/// Decides when a meeting starts and ends, and writes its audio. It only sees
/// blocks of samples, so it runs and is tested without any audio hardware.
///
/// A meeting starts after sustained speech (or on request) and includes the
/// last few seconds before it; it ends after a long silence (or on request).
/// Audio goes straight to disk, so a long meeting does not grow in memory.
/// Time is measured in audio written, not wall clock, so the result does not
/// depend on how fast blocks arrive.
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    private readonly RecorderOptions _o;
    private readonly object _gate = new();
    private readonly Queue<(float[] Mic, float[] Loop)> _preRoll = new();
    private int _preRollSamples;
    private StereoWavWriter? _writer;
    private string? _wavPath;
    // Counted in samples, not seconds, so thresholds are exact.
    private long _speechSamples;
    private long _silenceSamples;
    private double _startTimestamp;
    private int _blocks;
    private double _micRmsSum, _loopRmsSum;
    private bool _muteChecked;

    public MeetingRecorder(RecorderOptions options) => _o = options;

    public bool IsRecording { get; private set; }
    public string? CurrentMeetingId { get; private set; }
    public string? CurrentTitle { get; private set; }
    public float MicRms { get; private set; }
    public float LoopRms { get; private set; }
    public bool AutoStart { get => _o.AutoStart; set => _o.AutoStart = value; }
    public bool StartOnSystemAudio { get => _o.StartOnSystemAudio; set => _o.StartOnSystemAudio = value; }
    public double ElapsedSeconds { get { lock (_gate) return _writer is null ? 0 : _writer.Frames / (double)_o.SampleRate; } }

    public event Action<string, string>? MeetingStarted;           // id, title
    public event Action<FinishedRecording>? MeetingFinished;
    /// <summary>Raised once per meeting when the microphone is silent while system audio is playing.</summary>
    public event Action? MicrophoneSilent;

    /// <summary>Feeds one block from each source. `now` is Unix time in seconds.</summary>
    public void Push(float[] mic, float[] loop, double now)
    {
        Action? notify = null;
        lock (_gate)
        {
            var samples = Math.Max(mic.Length, loop.Length);
            MicRms = AudioMath.Rms(mic);
            LoopRms = AudioMath.Rms(loop);
            var micSpeech = AudioMath.IsSpeech(mic, MicRms, _o.SpeechThreshold);
            var speech = micSpeech || LoopRms >= _o.SpeechThreshold;          // keeps a running meeting alive
            var startSpeech = micSpeech || (_o.StartOnSystemAudio && speech);  // may begin a new one

            if (!IsRecording)
            {
                _preRoll.Enqueue((mic, loop));
                _preRollSamples += Math.Max(mic.Length, loop.Length);
                while (_preRoll.Count > 1 && _preRollSamples - Math.Max(_preRoll.Peek().Mic.Length, _preRoll.Peek().Loop.Length) >= _o.PreRollSeconds * _o.SampleRate)
                {
                    var old = _preRoll.Dequeue();
                    _preRollSamples -= Math.Max(old.Mic.Length, old.Loop.Length);
                }

                if (!_o.AutoStart) return;
                _speechSamples = startSpeech ? _speechSamples + samples : Math.Max(0, _speechSamples - samples);
                if (_speechSamples >= (long)Math.Round(_o.MinSpeechSeconds * _o.SampleRate)) notify = StartLocked("Spoken Conversation", now);
            }
            else
            {
                _writer!.Write(mic, loop);
                _blocks++;
                _micRmsSum += MicRms;
                _loopRmsSum += LoopRms;
                if (!_muteChecked && _writer.Frames >= 3 * _o.SampleRate)
                {
                    _muteChecked = true;
                    if (_micRmsSum / _blocks < 0.0001 && _loopRmsSum / _blocks > 0.003) notify = MicrophoneSilent;
                }

                _silenceSamples = speech ? 0 : _silenceSamples + samples;
                if (_silenceSamples >= (long)Math.Round(_o.SilenceTimeoutSeconds * _o.SampleRate)) notify = StopLocked();
            }
        }
        notify?.Invoke();
    }

    public string Start(string title, double now)
    {
        Action? notify;
        string id;
        lock (_gate)
        {
            if (IsRecording) return CurrentMeetingId!;
            notify = StartLocked(title, now);
            id = CurrentMeetingId!;
        }
        notify?.Invoke();
        return id;
    }

    /// <summary>Ends the current meeting. Returns false when nothing was being recorded.</summary>
    public bool Stop()
    {
        Action? notify;
        lock (_gate)
        {
            if (!IsRecording) return false;
            notify = StopLocked();
        }
        notify?.Invoke();
        return true;
    }

    private Action? StartLocked(string title, double now)
    {
        var preRollSeconds = _preRollSamples / (double)_o.SampleRate;
        _startTimestamp = now - preRollSeconds;
        var started = DateTimeOffset.FromUnixTimeMilliseconds((long)(now * 1000)).LocalDateTime;
        var id = "mtg_" + started.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        _wavPath = Path.Combine(_o.OutputDir, id + ".wav");
        for (var n = 2; File.Exists(_wavPath); n++)     // two meetings in the same second
        {
            id = "mtg_" + started.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + n;
            _wavPath = Path.Combine(_o.OutputDir, id + ".wav");
        }

        _writer = new StereoWavWriter(_wavPath, _o.SampleRate);
        foreach (var (mic, loop) in _preRoll) _writer.Write(mic, loop);
        _preRoll.Clear();
        _preRollSamples = 0;

        CurrentMeetingId = id;
        CurrentTitle = title;
        IsRecording = true;
        _speechSamples = _silenceSamples = 0;
        _blocks = 0;
        _micRmsSum = _loopRmsSum = 0;
        _muteChecked = false;
        var started1 = MeetingStarted;
        return started1 is null ? null : () => started1(id, title);
    }

    private Action? StopLocked()
    {
        var frames = _writer!.Frames;
        _writer.Dispose();
        _writer = null;
        IsRecording = false;
        var finished = new FinishedRecording(CurrentMeetingId!, CurrentTitle ?? "Meeting", "", _startTimestamp, frames / (double)_o.SampleRate, _wavPath);
        CurrentMeetingId = null;
        CurrentTitle = null;
        _speechSamples = _silenceSamples = 0;
        var handler = MeetingFinished;
        return handler is null ? null : () => handler(finished);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            IsRecording = false;
        }
    }
}
