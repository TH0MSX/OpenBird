using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenBird.Core;

namespace OpenBird.Windows;

public sealed record AudioDevices(string? Microphone, string? Speakers);

/// <summary>
/// Captures the default microphone and the system audio (loopback) through
/// WASAPI and feeds them to the recorder. Replaces the Python `soundcard`
/// library, whose memory bug crashed the app. If a stream stops (device
/// unplugged, sleep, default device changed) it is reopened on the current
/// default device.
/// </summary>
public sealed class WasapiCaptureService : IDisposable
{
    private readonly CaptureMixer _mixer;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _pump;
    private readonly System.Threading.Timer _watchdog;
    private WasapiCapture? _mic;
    private WasapiLoopbackCapture? _loop;
    private bool _running;
    private long _lastMicData;

    public WasapiCaptureService(CaptureMixer mixer, Action<string>? log = null)
    {
        _mixer = mixer;
        _log = log ?? (_ => { });
        _pump = new System.Threading.Timer(_ => Pump(), null, Timeout.Infinite, Timeout.Infinite);
        _watchdog = new System.Threading.Timer(_ => EnsureStreams(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static AudioDevices DescribeDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? Name(DataFlow flow, Role role)
        {
            if (!enumerator.HasDefaultAudioEndpoint(flow, role)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            return device.FriendlyName;
        }
        return new AudioDevices(Name(DataFlow.Capture, Role.Console), Name(DataFlow.Render, Role.Console));
    }

    public bool MicrophoneActive { get { lock (_gate) return _mic is not null; } }
    public bool SystemAudioActive { get { lock (_gate) return _loop is not null; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }
        EnsureStreams();
        _pump.Change(50, 50);
        _watchdog.Change(3000, 3000);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }
        _pump.Change(Timeout.Infinite, Timeout.Infinite);
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        CloseMic();
        CloseLoop();
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private void Pump()
    {
        try
        {
            // With a working microphone it sets the pace; without one, system audio does.
            var micAlive = MicrophoneActive && Environment.TickCount64 - Interlocked.Read(ref _lastMicData) < 2000;
            if (micAlive) _mixer.Pump(Now());
            else _mixer.PumpWithoutMicrophone(Now());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _log($"Audio pump error: {e.Message}");
        }
    }

    /// <summary>Opens whichever stream is missing. Called at start and every few seconds.</summary>
    private void EnsureStreams()
    {
        lock (_gate)
        {
            if (!_running) return;
            if (_mic is null) _mic = Open(() => new WasapiCapture(DefaultDevice(DataFlow.Capture)), _mixer.Microphone, "Microphone", isMic: true);
            if (_loop is null) _loop = (WasapiLoopbackCapture?)Open(() => new WasapiLoopbackCapture(), _mixer.SystemAudio, "System audio", isMic: false);
        }
    }

    private static MMDevice DefaultDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
    }

    private WasapiCapture? Open(Func<WasapiCapture> create, CaptureChannel channel, string name, bool isMic)
    {
        try
        {
            var capture = create();
            var wf = capture.WaveFormat;
            var standard = wf is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : wf;
            var format = new CaptureFormat(wf.SampleRate, wf.Channels, wf.BitsPerSample, standard.Encoding == WaveFormatEncoding.IeeeFloat);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                channel.Add(e.Buffer.AsSpan(0, e.BytesRecorded), format);
                if (isMic) Interlocked.Exchange(ref _lastMicData, Environment.TickCount64);
            };
            capture.RecordingStopped += (_, e) =>
            {
                _log($"{name} stream stopped{(e.Exception is null ? "" : ": " + e.Exception.Message)}; it will be reopened.");
                if (isMic) CloseMic(); else CloseLoop();
            };
            capture.StartRecording();
            if (isMic) Interlocked.Exchange(ref _lastMicData, Environment.TickCount64);
            _log($"{name} stream open: {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample}-bit {(format.IsFloat ? "float" : "PCM")}.");
            return capture;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _log($"{name} could not be opened ({e.Message}); retrying.");
            return null;
        }
    }

    private void CloseMic()
    {
        WasapiCapture? c;
        lock (_gate) { c = _mic; _mic = null; }
        DisposeCapture(c);
    }

    private void CloseLoop()
    {
        WasapiCapture? c;
        lock (_gate) { c = _loop; _loop = null; }
        DisposeCapture(c);
    }

    private static void DisposeCapture(WasapiCapture? capture)
    {
        if (capture is null) return;
        // Dispose off the callback thread: disposing from inside RecordingStopped can deadlock.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { capture.StopRecording(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            capture.Dispose();
        });
    }

    public void Dispose()
    {
        Stop();
        _pump.Dispose();
        _watchdog.Dispose();
    }
}
