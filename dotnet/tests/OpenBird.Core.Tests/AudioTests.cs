using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

public class AudioTests
{
    private const int Rate = 16000;
    private const int Block = 1600;     // 0.1 s

    private static float[] Tone(int samples, float amplitude = 0.1f, double hz = 200, int offset = 0) =>
        Enumerable.Range(offset, samples).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    private static float[] Silence(int samples = Block) => new float[samples];

    private static RecorderOptions Options(TempDir tmp, bool autoStart = true) => new()
    {
        SampleRate = Rate, OutputDir = tmp.File("rec"), AutoStart = autoStart,
        MinSpeechSeconds = 2.0, SilenceTimeoutSeconds = 5.0, PreRollSeconds = 3.0,
    };

    [Fact]
    public void Speech_detection_rejects_silence_clicks_and_hiss()
    {
        var tone = Tone(Block);
        Assert.True(AudioMath.IsSpeech(tone, AudioMath.Rms(tone), 0.015f));

        Assert.False(AudioMath.IsSpeech(Silence(), 0, 0.015f));

        var click = Silence();
        click[800] = 0.9f;                                            // one loud sample: a key press
        Assert.False(AudioMath.IsSpeech(click, AudioMath.Rms(click), 0.015f));

        var hiss = Enumerable.Range(0, Block).Select(i => i % 2 == 0 ? 0.05f : -0.05f).ToArray();
        Assert.False(AudioMath.IsSpeech(hiss, AudioMath.Rms(hiss), 0.015f));
    }

    [Fact]
    public void Wav_round_trip_keeps_channels_apart()
    {
        using var tmp = new TempDir();
        var path = tmp.File("a.wav");
        var mic = Tone(Rate);
        using (var w = new StereoWavWriter(path, Rate))
        {
            w.Write(mic.AsSpan(0, 6000), Silence(6000));
            w.Write(mic.AsSpan(6000), Silence(10000));
        }
        Assert.Equal(44 + Rate * 4, new FileInfo(path).Length);
        var audio = WavReader.Read(path);
        Assert.Equal((Rate, 2, Rate), (audio.SampleRate, audio.Channels.Length, audio.Frames));
        Assert.InRange(AudioMath.Rms(audio.Channels[0]), 0.069f, 0.072f);
        Assert.Equal(0f, AudioMath.Rms(audio.Channels[1]));
        Assert.Throws<InvalidDataException>(() => { File.WriteAllText(tmp.File("x.wav"), "nope nope nope"); WavReader.Read(tmp.File("x.wav")); });
    }

    [Fact]
    public void Meeting_starts_on_sustained_speech_keeps_the_pre_roll_and_ends_on_silence()
    {
        using var tmp = new TempDir();
        using var rec = new MeetingRecorder(Options(tmp));
        var started = new List<string>();
        var finished = new List<FinishedRecording>();
        rec.MeetingStarted += (id, title) => started.Add(title);
        rec.MeetingFinished += finished.Add;

        double now = 1_800_000_000;
        void Feed(float[] mic, float[] loop, int blocks) { for (var i = 0; i < blocks; i++) { rec.Push(mic, loop, now); now += 0.1; } }

        Feed(Silence(), Silence(), 50);                 // 5 s of quiet: nothing happens
        Feed(Tone(Block), Silence(), 10);               // 1 s of speech: not enough
        Assert.False(rec.IsRecording);
        Feed(Tone(Block), Silence(), 10);               // 2 s reached on the last block
        Assert.True(rec.IsRecording);
        Assert.Equal(new[] { "Spoken Conversation" }, started);

        Feed(Tone(Block), Silence(), 30);               // 3 s more speech
        Feed(Silence(), Silence(), 49);                 // 4.9 s silence: still recording
        Assert.True(rec.IsRecording);
        Feed(Silence(), Silence(), 1);                  // 5.0 s: ends
        Assert.False(rec.IsRecording);

        var f = Assert.Single(finished);
        // 3 s pre-roll (1 s quiet + 2 s speech) + 3 s speech + 5 s silence
        Assert.Equal(11.0, f.DurationSec, 3);
        Assert.Equal(Rate * 11, WavReader.Read(f.WavPath!).Frames);
        Assert.StartsWith("mtg_", f.MeetingId);
        Assert.Equal(1_800_000_000 + 7.0 - 0.1 - 3.0, f.StartTimestamp, 3);   // start moved back by the pre-roll

        Feed(Silence(), Silence(), 100);                // more silence must not start or finish anything
        Assert.Single(finished);
    }

    [Fact]
    public void System_audio_alone_starts_a_meeting_and_flags_a_silent_microphone()
    {
        using var tmp = new TempDir();
        using var rec = new MeetingRecorder(Options(tmp));
        var micSilent = 0;
        rec.MicrophoneSilent += () => micSilent++;
        double now = 1_800_000_000;
        for (var i = 0; i < 80; i++) { rec.Push(Silence(), Tone(Block), now); now += 0.1; }
        Assert.True(rec.IsRecording);
        Assert.Equal(1, micSilent);
    }

    [Fact]
    public void Manual_start_and_stop_work_with_auto_start_off()
    {
        using var tmp = new TempDir();
        using var rec = new MeetingRecorder(Options(tmp, autoStart: false));
        FinishedRecording? finished = null;
        rec.MeetingFinished += f => finished = f;
        double now = 1_800_000_000;
        for (var i = 0; i < 40; i++) { rec.Push(Tone(Block), Silence(), now); now += 0.1; }
        Assert.False(rec.IsRecording);                                   // speech alone does not start it

        var id = rec.Start("Manual Meeting", now);
        Assert.Equal(id, rec.Start("Again", now));                       // second start is ignored
        for (var i = 0; i < 20; i++) { rec.Push(Tone(Block), Silence(), now); now += 0.1; }
        Assert.Equal(5.0, rec.ElapsedSeconds, 3);                        // 3 s pre-roll + 2 s
        Assert.True(rec.Stop());
        Assert.False(rec.Stop());
        Assert.Equal(("Manual Meeting", 5.0), (finished!.Title, Math.Round(finished.DurationSec, 3)));
    }

    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public List<int> Lengths { get; } = new();
        public Queue<string> Replies { get; } = new();
        public string Recognize(float[] samples, int sampleRate)
        {
            Lengths.Add(samples.Length);
            return Replies.Count > 0 ? Replies.Dequeue() : $"text {Lengths.Count}";
        }
    }

    [Fact]
    public void Transcriber_makes_one_turn_per_stretch_of_speech_on_each_channel()
    {
        using var tmp = new TempDir();
        var path = tmp.File("m.wav");
        using (var w = new StereoWavWriter(path, Rate))
        {
            w.Write(Tone(Rate * 4), Silence(Rate * 4));                          // 0-4 s: host speaks
            w.Write(Tone(Rate / 5, 0f), Silence(Rate / 5));                      // 4-4.2 s: short pause, same turn
            w.Write(Tone(Rate * 2), Silence(Rate * 2));                          // 4.2-6.2 s: host continues
            w.Write(Silence(Rate * 2), Silence(Rate * 2));                       // 6.2-8.2 s: nobody
            w.Write(Tone(Rate * 3, 0.01f), Tone(Rate * 3));                      // 8.2-11.2 s: remote speaks, mic only hears bleed
            w.Write(Tone(Rate * 2), Tone(Rate * 2));                             // 11.2-13.2 s: both talk at once
            w.Write(Tone(Rate / 10), Silence(Rate / 10));                        // 13.2-13.3 s: a blip on the mic
        }
        var fake = new FakeRecognizer();

        var turns = new DualChannelTranscriber(fake).Transcribe(path, "Tester", "Remote Participants");

        Assert.Equal(new[] { ("Tester", "00:00"), ("Remote Participants", "00:08"), ("Tester", "00:11") },
            turns.Select(t => (t.Speaker, t.Timestamp)));
        Assert.Equal(3, fake.Lengths.Count);                                     // bleed and the 0.1 s blip never reach the recognizer
        Assert.InRange(fake.Lengths[0], (int)(Rate * 6.2), (int)(Rate * 6.4));   // the pause did not split the first turn
        Assert.InRange(turns[1].StartSec, 8.0, 8.2);                             // remote turn starts where the remote starts
        Assert.InRange(turns.Single(t => t.Channel == 1).StartSec + fake.Lengths[2] / (double)Rate, 13.2, 13.4);   // and runs through the overlap
    }

    [Fact]
    public void A_long_monologue_is_split_into_windows_the_engine_can_handle()
    {
        using var tmp = new TempDir();
        var path = tmp.File("long.wav");
        using (var w = new StereoWavWriter(path, Rate))
        {
            w.Write(Tone(Rate * 18), Silence(Rate * 18));
            w.Write(Tone(Rate / 2, 0.02f), Silence(Rate / 2));                   // a quieter half second at 18-18.5 s
            w.Write(Tone(Rate * 27), Silence(Rate * 27));
        }
        var fake = new FakeRecognizer();
        var turns = new DualChannelTranscriber(fake).Transcribe(path, "Tester", "Remote");
        Assert.All(fake.Lengths, n => Assert.InRange(n, 1, Rate * 20 + Rate / 10));
        Assert.Equal(45.5, fake.Lengths.Sum() / (double)Rate, 2);               // nothing lost, nothing transcribed twice
        Assert.InRange(turns[1].StartSec, 18.0, 18.5);                           // first cut lands in the quiet half second
    }

    [Fact]
    public void Known_whisper_hallucinations_and_silence_produce_no_turns()
    {
        using var tmp = new TempDir();
        var path = tmp.File("h.wav");
        using (var w = new StereoWavWriter(path, Rate)) w.Write(Tone(Rate * 3), Silence(Rate * 3));
        var fake = new FakeRecognizer();
        fake.Replies.Enqueue(" Thank you. ");
        Assert.Empty(new DualChannelTranscriber(fake).Transcribe(path, "Tester", "Remote"));

        var quiet = tmp.File("q.wav");
        using (var w = new StereoWavWriter(quiet, Rate)) w.Write(Silence(Rate * 3), Silence(Rate * 3));
        Assert.Empty(new DualChannelTranscriber(fake).Transcribe(quiet, "Tester", "Remote"));
        Assert.Single(fake.Lengths);                                             // silence never reaches the recognizer
    }
}

public class AutoStartAndScheduleTests
{
    private const int Rate = 16000;

    [Fact]
    public void With_system_audio_start_off_a_video_does_not_start_a_meeting_but_keeps_one_alive()
    {
        using var tmp = new TempDir();
        using var rec = new MeetingRecorder(new RecorderOptions { OutputDir = tmp.File("rec"), StartOnSystemAudio = false, MinSpeechSeconds = 2, SilenceTimeoutSeconds = 5 });
        var tone = Enumerable.Range(0, 1600).Select(i => (float)(0.1 * Math.Sin(2 * Math.PI * 200 * i / Rate))).ToArray();
        var quiet = new float[1600];
        double now = 1_800_000_000;
        void Feed(float[] mic, float[] loop, int blocks) { for (var i = 0; i < blocks; i++) { rec.Push(mic, loop, now); now += 0.1; } }

        Feed(quiet, tone, 100);                      // 10 s of video sound only
        Assert.False(rec.IsRecording);
        Feed(tone, quiet, 20);                       // 2 s of the user's voice
        Assert.True(rec.IsRecording);
        Feed(quiet, tone, 100);                      // the other side talks for 10 s: the meeting must not time out
        Assert.True(rec.IsRecording);
        Feed(quiet, quiet, 50);
        Assert.False(rec.IsRecording);
    }

    [Theory]
    [InlineData("17:30", "17:29", null, false)]              // not yet time
    [InlineData("17:30", "17:30", null, true)]               // time reached, nothing written today
    [InlineData("17:30", "21:05", null, true)]               // PC started late: still due
    [InlineData("17:30", "18:00", "17:31", false)]           // already written after the scheduled time
    [InlineData("17:30", "18:00", "09:12", true)]            // a manual digest from the morning does not count
    [InlineData("", "18:00", null, false)]                   // switched off
    [InlineData("25:99", "18:00", null, false)]              // invalid setting is treated as off
    public void Digest_schedule(string setting, string nowTime, string? writtenTime, bool expected)
    {
        var day = new DateTime(2026, 11, 13);
        DateTime At(string t) => day + TimeOnly.ParseExact(t, "HH:mm").ToTimeSpan();
        Assert.Equal(expected, DigestSchedule.IsDue(At(nowTime), setting, writtenTime is null ? null : At(writtenTime)));
    }
}
