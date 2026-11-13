using System.Collections.Concurrent;
using OpenBird.Core;
using OpenBird.Speech;

namespace OpenBird.Windows;

public sealed record EngineStatus(
    bool IsRecording, string? MeetingTitle, double ElapsedSeconds, float MicLevel, float SystemLevel,
    bool AutoListen, bool ScreenPaused, int ScreenStored, int ScreenExcluded, string LastWindow,
    bool MicrophoneActive, bool SystemAudioActive, bool ModelInstalled, int Processing, string? HotkeyInUse);

/// <summary>
/// Everything that runs behind the window: audio capture, meeting detection,
/// transcription, screen memory and the hotkey. The window only calls methods
/// here and listens to events; events arrive on background threads.
/// </summary>
public sealed class OpenBirdEngine : IDisposable
{
    private readonly SettingsStore _store;
    private readonly ConcurrentDictionary<string, (string Title, string Tags)> _meetingInfo = new();
    private readonly LazyWhisperTranscriber _transcriber;
    // Five minutes: a model running on this PC's processor can take a while to answer.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly WindowsScreenSource _screenSource;
    private readonly CancellationTokenSource _stop = new();
    private MeetingRecorder _recorder = null!;
    private CaptureMixer _mixer = null!;
    private WasapiCaptureService? _capture;
    private MeetingPipeline _pipeline = null!;
    private HotkeyListener? _hotkey;
    private Task? _screenLoop;
    private int _processing;

    public AppSettings Settings { get; private set; }
    public Database Database { get; private set; } = null!;
    public ScreenWatcher Screen { get; private set; } = null!;
    public AskEngine Ask { get; private set; } = null!;
    public string LogPath { get; }
    public string? HotkeyProblem { get; private set; }

    /// <summary>A meeting was saved. May carry a warning (for example: no speech model).</summary>
    public event Action<ProcessedMeeting>? MeetingProcessed;
    public event Action<string, string>? MeetingStarted;
    /// <summary>Something the user should see (not an error dialog): hotkey taken, microphone silent, a failure.</summary>
    public event Action<string>? Notice;
    /// <summary>The scheduled daily digest was written.</summary>
    public event Action<DigestResult>? DigestReady;
    /// <summary>New suggestions were added after a meeting.</summary>
    public event Action? CardsChanged;

    public OpenBirdEngine(SettingsStore? store = null)
    {
        _store = store ?? new SettingsStore();
        Settings = _store.Load();
        Directory.CreateDirectory(_store.LogDir);
        LogPath = Path.Combine(_store.LogDir, "openbird-app.log");
        _screenSource = new WindowsScreenSource(Log);
        _transcriber = new LazyWhisperTranscriber(() => Settings.WhisperModelDir, () => Settings.WhisperThreads);
        Build();
    }

    public SettingsStore Store => _store;

    private void Build()
    {
        Database = new Database(Settings.DbPath, Settings.StorageDir);
        Ask = new AskEngine(Database, Settings.UserName);
        Screen = new ScreenWatcher(Database, _screenSource, Settings) { Paused = !Settings.ScreenMemoryEnabled };
        _pipeline = new MeetingPipeline(Database, Settings, _transcriber, new CloudSummaryProvider(AiModels.Create(Settings, _http)));

        _recorder = new MeetingRecorder(RecorderOptions.From(Settings));
        _recorder.MeetingStarted += OnMeetingStarted;
        _recorder.MeetingFinished += OnMeetingFinished;
        _recorder.MicrophoneSilent += () => Notice?.Invoke("The microphone is silent while system audio is playing. Check that it is not muted.");
        _mixer = new CaptureMixer(_recorder, sampleRate: Settings.SampleRate, blockSamples: Settings.SampleRate / 10);
    }

    public void Start()
    {
        _capture = new WasapiCaptureService(_mixer, Log);
        _capture.Start();
        _screenLoop = Task.Run(ScreenLoopAsync);
        _ = Task.Run(DigestLoopAsync);
        RegisterHotkey();
        Log($"Engine started. AI mode: {Settings.EffectiveAiMode}. Model installed: {WhisperModel.IsInstalled(Settings.WhisperModelDir)}.");
    }

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        HotkeyProblem = null;
        foreach (var spec in new[] { Settings.Hotkey, "ctrl+alt+r" }.Distinct())
        {
            HotkeyListener listener;
            try { listener = new HotkeyListener(spec); }
            catch (FormatException e) { HotkeyProblem = e.Message; continue; }

            if (listener.Registered)
            {
                listener.Pressed += () => ToggleRecording();
                _hotkey = listener;
                if (spec != Settings.Hotkey)
                    HotkeyProblem = $"The hotkey {Settings.Hotkey} is used by another program, so {spec} is active instead. Change it in Settings.";
                break;
            }
            HotkeyProblem = listener.FailureReason;
            listener.Dispose();
        }
        if (HotkeyProblem is not null) Log("Hotkey: " + HotkeyProblem);
    }

    public string? ActiveHotkey => _hotkey?.Spec;

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ------------------------------------------------------------ recording

    public bool ToggleRecording()
    {
        if (_recorder.IsRecording) { _recorder.Stop(); return false; }
        _recorder.Start("Manual Meeting", Now());
        return true;
    }

    public bool AutoListen
    {
        get => _recorder.AutoStart;
        set => _recorder.AutoStart = value;
    }

    private void OnMeetingStarted(string id, string title)
    {
        // Name the meeting after the window in front, unless that window is private.
        var window = _screenSource.GetForegroundWindow();
        if (window is not null && !Privacy.IsExcluded(window.Title, window.ProcessName, Settings.CaptureExclusions))
        {
            var clean = WindowTitles.Clean(window.Title);
            if (clean is not ("Desktop / Inactive" or "OpenBird")) title = $"Meeting ({(clean.Length > 40 ? clean[..40] : clean)})";
        }
        _meetingInfo[id] = (title, "");
        Log($"Meeting started: {id}");
        MeetingStarted?.Invoke(id, title);
    }

    private void OnMeetingFinished(FinishedRecording recording)
    {
        if (_meetingInfo.TryRemove(recording.MeetingId, out var info)) recording = recording with { Title = info.Title, Tags = info.Tags };
        Interlocked.Increment(ref _processing);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _pipeline.ProcessAsync(recording, ct: _stop.Token);
                if (result is null) return;
                Log($"Meeting saved: {result.MeetingId}, {result.Turns} turns{(result.Warning is null ? "" : ", warning: " + result.Warning)}");
                MeetingProcessed?.Invoke(result);

                // Suggestions are a bonus: if the model is slow or down, the meeting is already saved.
                var models = Models();
                if (models.Count > 0 && result.Turns > 0)
                {
                    var cards = await ActionCardGenerator.GenerateAsync(Database, models, result.MeetingId, Settings.UserName, _stop.Token);
                    if (cards.Count > 0)
                    {
                        Log($"{cards.Count} suggestion(s) for {result.MeetingId}");
                        CardsChanged?.Invoke();
                    }
                }
            }
            catch (Exception e)
            {
                Log($"Meeting {recording.MeetingId} could not be processed: {e}");
                Notice?.Invoke($"A meeting could not be saved: {e.Message}. The recording is kept at {recording.WavPath}.");
            }
            finally { Interlocked.Decrement(ref _processing); }
        });
    }

    // ------------------------------------------------------------ screen memory

    private async Task ScreenLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Screen.TickAsync(DateTime.Now, ct: _stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, Settings.ScreenOcrIntervalSeconds)), _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Log($"Screen capture cycle failed: {e}");
                try { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Turns the screen memory on or off and remembers the choice.</summary>
    public void SetScreenMemory(bool enabled)
    {
        Screen.Paused = !enabled;
        if (Settings.ScreenMemoryEnabled == enabled) return;
        Settings.ScreenMemoryEnabled = enabled;
        _store.Save(Settings);
    }

    /// <summary>Remembers whether recording starts by itself when a conversation begins.</summary>
    public void SetAutoListen(bool enabled)
    {
        _recorder.AutoStart = enabled;
        if (Settings.AutoTranscribe == enabled) return;
        Settings.AutoTranscribe = enabled;
        _store.Save(Settings);
    }

    private async Task DigestLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var path = Path.Combine(Database.NotesDir, "digests", $"{now:yyyy-MM-dd}_Daily_Digest.md");
                DateTime? written = File.Exists(path) ? File.GetLastWriteTime(path) : null;
                if (DigestSchedule.IsDue(now, Settings.DigestTime, written))
                {
                    var result = new DigestGenerator(Database, Settings.UserName).Generate(DateOnly.FromDateTime(now), now);
                    Log($"Scheduled digest written: {result.MarkdownPath}");
                    DigestReady?.Invoke(result);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Log($"Scheduled digest failed: {e}");
                try { await Task.Delay(TimeSpan.FromMinutes(10), _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public Task<ScreenTick> CaptureNowAsync() => Task.Run(() => Screen.TickAsync(DateTime.Now, force: true));

    // ------------------------------------------------------------ status, settings, model

    public EngineStatus Status() => new(
        _recorder.IsRecording, _meetingInfo.TryGetValue(_recorder.CurrentMeetingId ?? "", out var info) ? info.Title : _recorder.CurrentTitle,
        _recorder.ElapsedSeconds, _recorder.MicRms, _recorder.LoopRms, _recorder.AutoStart, Screen.Paused, Screen.StoredCount,
        Screen.ExcludedCount, Screen.LastWindow, _capture?.MicrophoneActive ?? false, _capture?.SystemAudioActive ?? false,
        WhisperModel.IsInstalled(Settings.WhisperModelDir), Volatile.Read(ref _processing), _hotkey?.Spec);

    /// <summary>The models allowed by the AI setting: none, one on this PC, or the cloud providers.</summary>
    public IReadOnlyList<IChatModel> Models() => AiModels.Create(Settings, _http);

    /// <summary>Lists the models of a local server, to check an address before saving it. Throws with a readable reason.</summary>
    public Task<IReadOnlyList<string>> ListLocalModelsAsync(string url, CancellationToken ct) =>
        new LocalChatModel(_http, url, "").ListModelsAsync(ct);

    /// <summary>Sends a real chat message to the local model so a wrong model name shows up before it breaks Ask.</summary>
    public Task<LocalTestResult> TestLocalModelAsync(string url, string model, CancellationToken ct) =>
        new LocalChatModel(_http, url, model).TestAsync(ct);

    public Task InstallModelAsync(IProgress<(long Done, long Total)> progress, CancellationToken ct) =>
        WhisperModel.InstallAsync(Settings.WhisperModelDir, _http, progress, ct: ct);

    /// <summary>Saves new settings and applies the ones that can change while running.</summary>
    public void ApplySettings(AppSettings updated)
    {
        var pathsChanged = updated.DbPath != Settings.DbPath || updated.StorageDir != Settings.StorageDir;
        _store.Save(updated);
        Settings = updated;
        if (pathsChanged) Database = new Database(Settings.DbPath, Settings.StorageDir);
        Ask = new AskEngine(Database, Settings.UserName);
        Screen = new ScreenWatcher(Database, _screenSource, Settings) { Paused = !Settings.ScreenMemoryEnabled };
        _pipeline = new MeetingPipeline(Database, Settings, _transcriber, new CloudSummaryProvider(AiModels.Create(Settings, _http)));
        _recorder.AutoStart = Settings.AutoTranscribe;
        _recorder.StartOnSystemAudio = Settings.AutoStartOnSystemAudio;
        RegisterHotkey();
        Log("Settings saved.");
    }

    public void Log(string message)
    {
        try
        {
            lock (_meetingInfo)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2_000_000) File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        if (_recorder.IsRecording) _recorder.Stop();      // the open meeting is saved, not lost
        _capture?.Dispose();
        // Give meetings that are still being transcribed up to two minutes to finish saving.
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (Volatile.Read(ref _processing) > 0 && DateTime.UtcNow < deadline) Thread.Sleep(100);
        _stop.Cancel();
        _recorder.Dispose();
        _transcriber.Dispose();
        _http.Dispose();
    }
}
