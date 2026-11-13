using System.Buffers.Binary;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

public class CapturePipelineTests
{
    private static byte[] FloatStereo(int rate, double hz, double seconds, float amplitude = 0.2f)
    {
        var frames = (int)(rate * seconds);
        var raw = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 8), v);         // left
            BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 8 + 4), v);     // right
        }
        return raw;
    }

    private static float[] Drain(CaptureChannel ch)
    {
        var all = new float[ch.Available];
        ch.TryRead(all);
        return all;
    }

    [Fact]
    public void Device_audio_at_48k_stereo_becomes_16k_mono_at_the_same_level()
    {
        var ch = new CaptureChannel();
        var raw = FloatStereo(48000, 1000, 2.0);
        var format = new CaptureFormat(48000, 2, 32, true);
        for (var at = 0; at < raw.Length; at += 3840) ch.Add(raw.AsSpan(at, Math.Min(3840, raw.Length - at)), format);   // 10 ms device blocks

        var mono = Drain(ch);
        Assert.InRange(mono.Length, 31900, 32100);                           // 2 s at 16 kHz
        Assert.InRange(AudioMath.Rms(mono.AsSpan(1600)), 0.13f, 0.15f);      // 0.2 peak sine = 0.141 RMS
    }

    [Fact]
    public void Content_above_the_new_nyquist_is_filtered_instead_of_aliasing_into_speech()
    {
        var ch = new CaptureChannel();
        ch.Add(FloatStereo(48000, 12000, 1.0), new CaptureFormat(48000, 2, 32, true));
        Assert.True(AudioMath.Rms(Drain(ch).AsSpan(1600)) < 0.03f);          // 0.141 in, strongly reduced out
    }

    [Fact]
    public void Sixteen_bit_mono_at_16k_passes_through_unchanged()
    {
        var ch = new CaptureChannel();
        var raw = new byte[3200 * 2];
        for (var i = 0; i < 3200; i++) BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), (short)(i % 2 == 0 ? 16384 : -16384));
        ch.Add(raw, new CaptureFormat(16000, 1, 16, false));
        var mono = Drain(ch);
        Assert.Equal(3200, mono.Length);
        Assert.Equal(0.5f, mono[0], 3);
        Assert.Equal(-0.5f, mono[1], 3);
    }

    [Fact]
    public void Mixer_paces_on_the_microphone_and_fills_silence_for_missing_system_audio()
    {
        using var tmp = new TempDir();
        using var recorder = new MeetingRecorder(new RecorderOptions { OutputDir = tmp.File("rec"), AutoStart = false });
        var mixer = new CaptureMixer(recorder);
        var f16 = new CaptureFormat(16000, 1, 32, true);
        byte[] Samples(int n, float v) { var b = new byte[n * 4]; for (var i = 0; i < n; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), i % 2 == 0 ? v : -v); return b; }

        recorder.Start("Manual", 1_800_000_000);
        mixer.Microphone.Add(Samples(4000, 0.1f), f16);                       // 2.5 blocks of mic
        mixer.SystemAudio.Add(Samples(1600, 0.3f), f16);                      // only 1 block of system audio
        Assert.Equal(2, mixer.Pump(1_800_000_000));                           // 2 whole blocks fed, 800 samples wait
        Assert.Equal(800, mixer.Microphone.Available);
        Assert.Equal(0f, recorder.LoopRms);                                   // second block had no system audio

        mixer.SystemAudio.Add(Samples(16000 * 5, 0.3f), f16);                 // system audio piles up with no mic to pace it
        mixer.Pump(1_800_000_001);
        Assert.Equal(16000 * 2, mixer.SystemAudio.Available);                 // capped at 2 s

        Assert.True(mixer.PumpWithoutMicrophone(1_800_000_002) == 20);        // no-microphone mode drains it
        recorder.Stop();
    }
}

public class ScreenMemoryTests
{
    private sealed class FakeScreen : IScreenSource
    {
        public ForegroundWindow? Window = new("Report.xlsx - Excel", "EXCEL.EXE");
        public ForegroundWindow? WindowAfterCapture;
        public byte[] Thumb = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
        public string Text = "Release rate 97.2%";
        public bool Locked;
        public int Captures, OcrRuns;

        public ForegroundWindow? GetForegroundWindow() => Captures > 0 && WindowAfterCapture is not null ? WindowAfterCapture : Window;

        public CapturedScreen? Capture(string imagePath, bool allMonitors)
        {
            if (Locked) return null;
            Captures++;
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });
            return new CapturedScreen(imagePath, 1920, 1080, Thumb);
        }

        public Task<string> ReadTextAsync(string imagePath, CancellationToken ct)
        {
            OcrRuns++;
            return Task.FromResult(Text);
        }
    }

    private static (ScreenWatcher, FakeScreen, Database, AppSettings) Setup(TempDir tmp)
    {
        var settings = new SettingsStore(tmp.File("appdata"), tmp.File("docs")).Load();
        var db = new Database(settings.DbPath, settings.StorageDir);
        var screen = new FakeScreen();
        return (new ScreenWatcher(db, screen, settings), screen, db, settings);
    }

    private static readonly DateTime T0 = new(2026, 11, 13, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public async Task Stores_a_changed_screen_and_skips_an_unchanged_one_before_ocr()
    {
        using var tmp = new TempDir();
        var (watcher, screen, db, settings) = Setup(tmp);

        Assert.Equal(ScreenTick.Stored, await watcher.TickAsync(T0));
        Assert.Equal(ScreenTick.Unchanged, await watcher.TickAsync(T0.AddSeconds(20)));
        Assert.Equal(1, screen.OcrRuns);                                              // no OCR for the unchanged frame
        Assert.Single(Directory.GetFiles(settings.ScreenshotsDir));                   // and its image was removed

        screen.Thumb = screen.Thumb.Reverse().ToArray();                              // the screen content changed
        screen.Text = "NPS 4.6";
        Assert.Equal(ScreenTick.Stored, await watcher.TickAsync(T0.AddSeconds(40)));

        screen.Window = new ForegroundWindow("(3) Inbox - Outlook - Google Chrome", "chrome.exe");   // same pixels, other window
        Assert.Equal(ScreenTick.Stored, await watcher.TickAsync(T0.AddSeconds(60)));
        Assert.Equal(ScreenTick.Unchanged, await watcher.TickAsync(T0.AddSeconds(80)));
        Assert.Equal(ScreenTick.Stored, await watcher.TickAsync(T0.AddSeconds(60 + 300)));           // keep-alive after 5 min

        var frames = db.GetScreenFrames(0, double.MaxValue);
        Assert.Equal(new[] { "Report.xlsx", "Report.xlsx", "Inbox - Outlook", "Inbox - Outlook" }, frames.Select(f => f.WindowTitle));
        Assert.Equal("2026-11-13 10:00:00", frames[0].TimeStr);
        Assert.Single(db.SearchScreen("release rate"));
        var journal = File.ReadAllText(Path.Combine(settings.StorageDir, "timeline", "2026-11-13_daily_context.md"));
        Assert.StartsWith("# OpenBird screen journal: 2026-11-13", journal);
        Assert.Contains("### 10:00:40 · Report.xlsx\n\n> NPS 4.6", journal);
        Assert.Equal(4, watcher.StoredCount);
    }

    [Fact]
    public async Task Excluded_windows_are_never_captured_even_when_forced_or_when_they_appear_mid_capture()
    {
        using var tmp = new TempDir();
        var (watcher, screen, db, settings) = Setup(tmp);
        screen.Window = new ForegroundWindow("My Vault - 1Password", "1Password.exe");
        Assert.Equal(ScreenTick.Excluded, await watcher.TickAsync(T0));
        Assert.Equal(ScreenTick.Excluded, await watcher.TickAsync(T0.AddSeconds(20), force: true));
        Assert.Equal(0, screen.Captures);

        screen.Window = new ForegroundWindow("Notes - Word", "WINWORD.EXE");
        screen.WindowAfterCapture = new ForegroundWindow("Sign in - [InPrivate] - Microsoft Edge", "msedge.exe");
        screen.Captures = 0;
        // Before the grab the window is fine; by the time the grab finished a private window is in front.
        var first = screen.GetForegroundWindow();
        Assert.Equal("Notes - Word", first!.Title);
        Assert.Equal(ScreenTick.Excluded, await watcher.TickAsync(T0.AddSeconds(40)));
        Assert.Equal(0, screen.OcrRuns);
        Assert.False(Directory.Exists(settings.ScreenshotsDir) && Directory.GetFiles(settings.ScreenshotsDir).Length > 0);   // image deleted
        Assert.Empty(db.GetScreenFrames(0, double.MaxValue));
        Assert.Equal(3, watcher.ExcludedCount);
    }

    [Fact]
    public async Task Paused_locked_and_idle_states_store_nothing()
    {
        using var tmp = new TempDir();
        var (watcher, screen, db, _) = Setup(tmp);
        watcher.Paused = true;
        Assert.Equal(ScreenTick.Paused, await watcher.TickAsync(T0));
        watcher.Paused = false;
        screen.Locked = true;
        Assert.Equal(ScreenTick.Unavailable, await watcher.TickAsync(T0));
        screen.Locked = false;
        screen.Window = null;
        Assert.Equal(ScreenTick.Inactive, await watcher.TickAsync(T0));
        Assert.Equal(ScreenTick.Stored, await watcher.TickAsync(T0, force: true));    // "capture now" still works on the desktop
        Assert.Single(db.GetScreenFrames(0, double.MaxValue));
    }

    [Fact]
    public async Task Old_screenshots_are_deleted_after_the_retention_period()
    {
        using var tmp = new TempDir();
        var (watcher, _, _, settings) = Setup(tmp);
        Directory.CreateDirectory(settings.ScreenshotsDir);
        var old = Path.Combine(settings.ScreenshotsDir, "screen_20261101_090000.png");
        var recent = Path.Combine(settings.ScreenshotsDir, "screen_20261112_090000.png");
        var unrelated = Path.Combine(settings.ScreenshotsDir, "keep-me.txt");
        foreach (var f in new[] { old, recent, unrelated }) File.WriteAllText(f, "x");
        File.SetLastWriteTime(old, T0.AddDays(-12));
        File.SetLastWriteTime(recent, T0.AddDays(-1));
        File.SetLastWriteTime(unrelated, T0.AddDays(-400));

        await watcher.TickAsync(T0);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));                                           // only OpenBird's own screenshots are touched
    }

    [Fact]
    public void Frame_hash_and_title_cleaning()
    {
        var a = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
        var b = a.ToArray();
        b[10] += 3;                                                                    // tiny change (a blinking cursor)
        Assert.True(FrameHash.Distance(FrameHash.Compute(a), FrameHash.Compute(b)) <= 2);
        Assert.True(FrameHash.Distance(FrameHash.Compute(a), FrameHash.Compute(a.Reverse().ToArray())) > 20);

        Assert.Equal("Inbox - Outlook", WindowTitles.Clean("(12) Inbox - Outlook - Microsoft Edge"));
        Assert.Equal("main.py", WindowTitles.Clean("● main.py - Visual Studio Code"));
        Assert.Equal("Desktop / Inactive", WindowTitles.Clean("   "));
        Assert.Equal("(draft) plan", WindowTitles.Clean("(draft) plan"));
    }
}
