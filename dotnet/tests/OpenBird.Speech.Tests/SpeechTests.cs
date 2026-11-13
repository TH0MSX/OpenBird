using System.Net;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using OpenBird.Core;
using OpenBird.Speech;
using Xunit;
using Xunit.Abstractions;

namespace OpenBird.Speech.Tests;

public class ModelInstallTests
{
    private sealed class ArchiveHandler(byte[] archive, bool truncate = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(truncate ? archive[..(archive.Length / 2)] : archive);
            content.Headers.ContentLength = archive.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static byte[] MakeArchive(params string[] names)
    {
        using var ms = new MemoryStream();
        using (var bz = new BZip2OutputStream(ms) { IsStreamOwner = false })
        using (var tar = new TarOutputStream(bz, System.Text.Encoding.UTF8))
        {
            foreach (var name in names)
            {
                var data = System.Text.Encoding.UTF8.GetBytes("content of " + name);
                var entry = TarEntry.CreateTarEntry(name);
                entry.Size = data.Length;
                tar.PutNextEntry(entry);
                tar.Write(data);
                tar.CloseEntry();
            }
        }
        return ms.ToArray();
    }

    private static string NewDir() => Directory.CreateTempSubdirectory("openbird_model_").FullName;

    [Fact]
    public async Task Install_keeps_only_the_required_files_and_reports_progress()
    {
        var dir = NewDir();
        var archive = MakeArchive(
            "sherpa-onnx-whisper-base.en/base.en-encoder.int8.onnx", "sherpa-onnx-whisper-base.en/base.en-decoder.int8.onnx",
            "sherpa-onnx-whisper-base.en/base.en-tokens.txt", "sherpa-onnx-whisper-base.en/base.en-encoder.onnx",
            "../../outside/base.en-tokens.txt.evil", "sherpa-onnx-whisper-base.en/test_wavs/0.wav");
        var reports = new List<(long Done, long Total)>();
        var progress = new Progress<(long, long)>();
        Assert.False(WhisperModel.IsInstalled(dir));

        await WhisperModel.InstallAsync(dir, new HttpClient(new ArchiveHandler(archive)), new SyncProgress(reports));

        Assert.True(WhisperModel.IsInstalled(dir));
        Assert.Equal(WhisperModel.RequiredFiles.Order(), Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        Assert.Equal("content of sherpa-onnx-whisper-base.en/base.en-tokens.txt", File.ReadAllText(Path.Combine(dir, WhisperModel.Tokens)));
        Assert.Equal((archive.Length, archive.Length), ((int)reports[^1].Done, (int)reports[^1].Total));
    }

    private sealed class SyncProgress(List<(long, long)> sink) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => sink.Add(value);
    }

    [Fact]
    public async Task A_cut_off_download_fails_cleanly_and_leaves_nothing_behind()
    {
        var dir = NewDir();
        var archive = MakeArchive("m/base.en-encoder.int8.onnx", "m/base.en-decoder.int8.onnx", "m/base.en-tokens.txt");
        await Assert.ThrowsAnyAsync<Exception>(() => WhisperModel.InstallAsync(dir, new HttpClient(new ArchiveHandler(archive, truncate: true))));
        Assert.False(WhisperModel.IsInstalled(dir));
        Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".tar.bz2.part"));
    }

    [Fact]
    public void Recognizer_reports_which_model_files_are_missing()
    {
        var e = Assert.Throws<FileNotFoundException>(() => new WhisperRecognizer(NewDir()));
        Assert.Contains("base.en-encoder.int8.onnx", e.Message);
    }
}

/// <summary>
/// Runs the real Whisper model. Needs OPENBIRD_TEST_MODEL_DIR pointing at an unpacked
/// sherpa-onnx-whisper-base.en folder (with its test_wavs); passes without checking anything otherwise.
/// </summary>
public class RealModelTests(ITestOutputHelper output)
{
    [Fact]
    public void Two_channel_recording_is_transcribed_with_the_right_speaker_per_channel()
    {
        var modelDir = Environment.GetEnvironmentVariable("OPENBIRD_TEST_MODEL_DIR");
        if (string.IsNullOrEmpty(modelDir) || !WhisperModel.IsInstalled(modelDir))
        {
            output.WriteLine("SKIPPED: OPENBIRD_TEST_MODEL_DIR is not set.");
            return;
        }

        var host = WavReader.Read(Path.Combine(modelDir, "test_wavs", "0.wav")).Channels[0];      // 6.6 s
        var remote = WavReader.Read(Path.Combine(modelDir, "test_wavs", "1.wav")).Channels[0];    // 16.7 s
        var path = Path.Combine(Directory.CreateTempSubdirectory("openbird_real_").FullName, "meeting.wav");
        using (var w = new StereoWavWriter(path, 16000))
        {
            w.Write(host, new float[host.Length]);                     // host speaks first
            w.Write(new float[8000], new float[8000]);                 // half a second of quiet
            w.Write(new float[remote.Length], remote);                 // then the remote side
        }

        using var whisper = new WhisperRecognizer(modelDir, threads: 2);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var turns = new DualChannelTranscriber(whisper).Transcribe(path, "Tester", "Remote Participants");
        clock.Stop();
        foreach (var t in turns) output.WriteLine($"[{t.Timestamp}] {t.Speaker} (ch{t.Channel}): {t.Text}");
        var seconds = (host.Length + 8000 + remote.Length) / 16000.0;
        output.WriteLine($"Audio {seconds:0.0}s transcribed in {clock.Elapsed.TotalSeconds:0.0}s");

        var hostText = string.Join(" ", turns.Where(t => t.Channel == 0).Select(t => t.Text)).ToLowerInvariant();
        var remoteText = string.Join(" ", turns.Where(t => t.Channel == 1).Select(t => t.Text)).ToLowerInvariant();
        Assert.Contains("nightfall", hostText);
        Assert.Contains("yellow lamps", hostText);
        Assert.Contains("lovely child", remoteText);
        Assert.DoesNotContain("nightfall", remoteText);
        Assert.All(turns.Where(t => t.Channel == 0), t => Assert.Equal("Tester", t.Speaker));
    }
}
