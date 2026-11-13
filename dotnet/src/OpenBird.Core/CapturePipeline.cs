using System.Buffers.Binary;
using NAudio.Dsp;

namespace OpenBird.Core;

/// <summary>Format of raw audio as a capture device delivers it.</summary>
public sealed record CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>
/// Converts one device's raw audio into 16 kHz mono float samples: decode,
/// mix channels down, resample with a low-pass filter, then queue.
/// Runs without audio hardware, so it is covered by tests.
/// </summary>
public sealed class CaptureChannel
{
    private readonly int _targetRate;
    private readonly object _gate = new();
    private readonly Queue<float> _queue = new();
    private WdlResampler? _resampler;
    private CaptureFormat? _format;

    public CaptureChannel(int targetRate = 16000) => _targetRate = targetRate;

    public int Available { get { lock (_gate) return _queue.Count; } }

    public void Add(ReadOnlySpan<byte> raw, CaptureFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = raw.Length / (bytesPerSample * format.Channels);
        if (frames == 0) return;

        lock (_gate)
        {
            if (_format != format)       // first block, or the device changed
            {
                _format = format;
                _resampler = null;
                if (format.SampleRate != _targetRate)
                {
                    _resampler = new WdlResampler();
                    _resampler.SetMode(true, 2, false);
                    _resampler.SetFilterParms();
                    _resampler.SetFeedMode(true);
                    _resampler.SetRates(format.SampleRate, _targetRate);
                }
            }

            float[] mono;
            var offset = 0;
            if (_resampler is null) mono = new float[frames];
            else _resampler.ResamplePrepare(frames, 1, out mono, out offset);

            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < format.Channels; c++)
                {
                    var s = raw.Slice((f * format.Channels + c) * bytesPerSample, bytesPerSample);
                    sum += (format.IsFloat, format.BitsPerSample) switch
                    {
                        (true, 32) => BinaryPrimitives.ReadSingleLittleEndian(s),
                        (false, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                        (false, 24) => ((s[0] | (s[1] << 8) | ((sbyte)s[2] << 16))) / 8388608f,
                        (false, 32) => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                        _ => throw new NotSupportedException($"Unsupported capture format: {format}."),
                    };
                }
                mono[offset + f] = sum / format.Channels;
            }

            if (_resampler is null)
            {
                foreach (var s in mono) _queue.Enqueue(s);
            }
            else
            {
                var maxOut = (int)((long)frames * _targetRate / format.SampleRate) + 16;
                var output = new float[maxOut];
                var produced = _resampler.ResampleOut(output, 0, frames, maxOut, 1);
                for (var i = 0; i < produced; i++) _queue.Enqueue(output[i]);
            }
        }
    }

    /// <summary>Takes exactly one block if that much audio is waiting.</summary>
    public bool TryRead(float[] block)
    {
        lock (_gate)
        {
            if (_queue.Count < block.Length) return false;
            for (var i = 0; i < block.Length; i++) block[i] = _queue.Dequeue();
            return true;
        }
    }

    /// <summary>Drops the oldest audio beyond `maxSamples`, so a stalled reader cannot build an ever-growing delay.</summary>
    public int TrimTo(int maxSamples)
    {
        lock (_gate)
        {
            var dropped = 0;
            while (_queue.Count > maxSamples) { _queue.Dequeue(); dropped++; }
            return dropped;
        }
    }
}

/// <summary>
/// Pairs microphone and system-audio blocks and feeds them to the recorder.
/// The microphone sets the pace; when nothing is playing, Windows delivers no
/// system audio at all, so silence is filled in for that side.
/// </summary>
public sealed class CaptureMixer(MeetingRecorder recorder, int blockSamples = 1600, int sampleRate = 16000)
{
    public CaptureChannel Microphone { get; } = new(sampleRate);
    public CaptureChannel SystemAudio { get; } = new(sampleRate);

    /// <summary>Feeds every complete block that is waiting. Returns how many were fed.</summary>
    public int Pump(double now)
    {
        var fed = 0;
        while (true)
        {
            var mic = new float[blockSamples];
            if (!Microphone.TryRead(mic)) break;
            var loop = new float[blockSamples];
            SystemAudio.TryRead(loop);                       // stays silent when nothing is waiting
            recorder.Push(mic, loop, now);
            fed++;
        }
        SystemAudio.TrimTo(sampleRate * 2);                  // never let the two sides drift more than 2 s apart
        return fed;
    }

    /// <summary>No microphone at all: let system audio set the pace so calls are still recorded.</summary>
    public int PumpWithoutMicrophone(double now)
    {
        var fed = 0;
        var loop = new float[blockSamples];
        while (SystemAudio.TryRead(loop))
        {
            recorder.Push(new float[blockSamples], loop, now);
            loop = new float[blockSamples];
            fed++;
        }
        return fed;
    }
}
