// OpenBird hardware check. Run on the target PC:
//     dotnet run --project src/OpenBird.Probe
// It exercises real audio capture, screen capture, OCR, transcription and the
// hotkey, using a temporary folder (your OpenBird data is only read, never
// changed), and writes probe-report.txt next to OpenBird.slnx.
// The report holds no screen text and no audio content, only sizes and levels.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenBird.Core;
using OpenBird.Speech;
using OpenBird.Windows;

var report = new StringBuilder();
var failures = 0;

void Line(string text)
{
    Console.WriteLine(text);
    report.AppendLine(text);
}

async Task Step(string name, Func<Task> body)
{
    Line($"\n== {name}");
    var clock = Stopwatch.StartNew();
    try
    {
        await body();
        Line($"   RESULT: ok ({clock.Elapsed.TotalSeconds:0.0}s)");
    }
    catch (Exception e)
    {
        failures++;
        Line($"   RESULT: FAILED - {e.GetType().Name}: {e.Message}");
        Line("   " + (e.StackTrace ?? "").Replace("\n", "\n   "));
    }
}

var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 8;
var work = Path.Combine(Path.GetTempPath(), "OpenBirdProbe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(work);
WindowsScreenSource.EnablePerMonitorDpiAwareness();

Line($"OpenBird probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Line($"OS: {RuntimeInformation.OSDescription} | process: {RuntimeInformation.ProcessArchitecture} | .NET {Environment.Version}");

AppSettings settings = new();
await Step("Settings (read only)", () =>
{
    var store = new SettingsStore();
    Line($"   file: {store.ConfigPath} (exists: {File.Exists(store.ConfigPath)})");
    settings = File.Exists(store.ConfigPath) ? store.Load() : store.Defaults();
    Line($"   AI mode: {settings.EffectiveAiMode} | hotkey: {settings.Hotkey} | exclusions: {settings.CaptureExclusions.Count}");
    Line($"   database exists: {File.Exists(settings.DbPath)} | speech model installed: {WhisperModel.IsInstalled(settings.WhisperModelDir)}");
    return Task.CompletedTask;
});

await Step("Existing database opens (on a copy)", () =>
{
    if (!File.Exists(settings.DbPath)) { Line("   no database yet; skipped"); return Task.CompletedTask; }
    var copy = Path.Combine(work, "copy.db");
    File.Copy(settings.DbPath, copy);
    foreach (var ext in new[] { "-wal", "-shm" })
        if (File.Exists(settings.DbPath + ext)) File.Copy(settings.DbPath + ext, copy + ext);
    var db = new Database(copy, Path.Combine(work, "notes"));
    Line($"   meetings: {db.ListMeetings(limit: 100000).Count} | action items: {db.GetActions().Count} | screen frames: {db.GetScreenFrames(0, double.MaxValue, 1000000).Count}");
    Line($"   full-text search for 'the': {db.SearchScreen("the", 5).Count} screen hits, {db.SearchTranscripts("the", 5).Count} transcript hits");
    return Task.CompletedTask;
});

await Step("Audio devices", () =>
{
    var devices = WasapiCaptureService.DescribeDevices();
    Line($"   microphone: {devices.Microphone ?? "NONE"}");
    Line($"   speakers:   {devices.Speakers ?? "NONE"}");
    return Task.CompletedTask;
});

string? wavPath = null;
await Step($"Record {seconds} s (talk, and play some sound, while this runs)", async () =>
{
    using var recorder = new MeetingRecorder(new RecorderOptions { OutputDir = Path.Combine(work, "rec"), AutoStart = false });
    FinishedRecording? finished = null;
    recorder.MeetingFinished += f => finished = f;
    var mixer = new CaptureMixer(recorder);
    using var capture = new WasapiCaptureService(mixer, m => Line("   [audio] " + m));
    capture.Start();
    await Task.Delay(700);
    recorder.Start("Probe", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

    float micPeak = 0, loopPeak = 0;
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        await Task.Delay(100);
        micPeak = Math.Max(micPeak, recorder.MicRms);
        loopPeak = Math.Max(loopPeak, recorder.LoopRms);
    }
    recorder.Stop();
    capture.Stop();

    if (finished is null) throw new InvalidOperationException("The recorder produced no recording.");
    wavPath = finished.WavPath;
    var audio = WavReader.Read(wavPath!);
    Line($"   recorded {finished.DurationSec:0.00}s of audio in {seconds}s of wall time ({audio.Frames} frames at {audio.SampleRate} Hz, {audio.Channels.Length} channels)");
    Line($"   loudest block: microphone {micPeak:0.0000}, system audio {loopPeak:0.0000}  (speech is usually above 0.015)");
    Line($"   whole-file level: microphone {AudioMath.Rms(audio.Channels[0]):0.0000}, system audio {AudioMath.Rms(audio.Channels[1]):0.0000}");
    if (Math.Abs(finished.DurationSec - seconds) > 1.5) throw new InvalidOperationException("Recorded length is far from wall time: audio is being lost or duplicated.");
    if (!capture.MicrophoneActive && micPeak == 0) Line("   NOTE: no microphone signal at all.");
});

await Step("Transcription (offline Whisper)", () =>
{
    if (wavPath is null) throw new InvalidOperationException("No recording to transcribe.");
    if (!WhisperModel.IsInstalled(settings.WhisperModelDir)) { Line($"   model not installed in {settings.WhisperModelDir}; skipped"); return Task.CompletedTask; }
    using var whisper = new WhisperRecognizer(settings.WhisperModelDir, settings.WhisperThreads);
    var clock = Stopwatch.StartNew();
    var turns = new DualChannelTranscriber(whisper).Transcribe(wavPath, "Host", "Remote");
    Line($"   {turns.Count} turns in {clock.Elapsed.TotalSeconds:0.0}s: " +
         string.Join("; ", turns.Select(t => $"[{t.Timestamp}] {t.Speaker} {t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words")));
    return Task.CompletedTask;
});

await Step("Screen capture and OCR", async () =>
{
    var screen = new WindowsScreenSource(m => Line("   [screen] " + m));
    var window = screen.GetForegroundWindow();
    Line($"   foreground process: {window?.ProcessName ?? "none"} | title length: {window?.Title.Length ?? 0} | excluded by privacy rules: {(window is not null && Privacy.IsExcluded(window.Title, window.ProcessName, settings.CaptureExclusions))}");
    foreach (var all in new[] { false, true })
    {
        var path = Path.Combine(work, all ? "all.jpg" : "primary.jpg");
        var clock = Stopwatch.StartNew();
        var shot = screen.Capture(path, all) ?? throw new InvalidOperationException("Screen capture returned nothing.");
        var captureMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var text = await screen.ReadTextAsync(path, CancellationToken.None);
        var flat = shot.Thumbnail8x8.Distinct().Count() == 1;
        Line($"   {(all ? "all monitors" : "primary")}: {shot.Width}x{shot.Height}, {new FileInfo(path).Length / 1024} KB JPEG, capture {captureMs} ms, OCR {clock.ElapsedMilliseconds} ms, {text.Length} characters read{(flat ? ", IMAGE IS ONE FLAT COLOUR" : "")}");
        if (flat) throw new InvalidOperationException("The capture is a single flat colour (black screen?).");
    }
});

await Step("Global hotkey", () =>
{
    using var hotkey = new HotkeyListener(settings.Hotkey);
    Line(hotkey.Registered ? $"   {settings.Hotkey} registered" : $"   NOT registered: {hotkey.FailureReason}");
    using var spare = new HotkeyListener("ctrl+alt+shift+f9");
    Line($"   ctrl+alt+shift+f9 (control check): {(spare.Registered ? "registered" : spare.FailureReason)}");
    if (!spare.Registered) throw new InvalidOperationException("Even a rarely used hotkey could not be registered.");
    return Task.CompletedTask;
});

Line($"\nSUMMARY: {(failures == 0 ? "all steps ok" : failures + " step(s) FAILED")}");

try { Directory.Delete(work, recursive: true); } catch (IOException) { }

// Write the report next to the solution file so it is easy to find and share.
var dir = new DirectoryInfo(AppContext.BaseDirectory);
while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBird.slnx"))) dir = dir.Parent;
var reportPath = Path.Combine(dir?.FullName ?? Environment.CurrentDirectory, "probe-report.txt");
File.WriteAllText(reportPath, report.ToString());
Console.WriteLine($"\nReport written to {reportPath}");
return failures == 0 ? 0 : 1;
