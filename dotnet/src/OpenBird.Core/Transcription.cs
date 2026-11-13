namespace OpenBird.Core;

/// <summary>Turns a short stretch of mono audio into text. Implemented by the Whisper engine.</summary>
public interface ISpeechRecognizer
{
    string Recognize(float[] samples, int sampleRate);
}

/// <summary>
/// Transcribes a two-channel recording into dialogue. Speakers are separated by
/// channel: channel 0 is the host microphone, channel 1 is system audio.
///
/// Each channel is split into stretches where that side is actually talking,
/// and each stretch becomes one turn with its own start time. The Python
/// version cut both channels into fixed 10-second blocks and kept only the
/// louder side of each block, which dropped the first words of a reply and
/// split words at block edges.
/// </summary>
public sealed class DualChannelTranscriber(ISpeechRecognizer recognizer) : ITranscriber
{
    private const double FrameSeconds = 0.05;
    private const float ActiveRms = 0.008f;        // quieter than this is not speech
    private const float BleedRatio = 0.5f;         // a side must be at least half as loud as the other to count
    private const double MaxGapSeconds = 0.6;      // pauses shorter than this stay inside one turn
    private const double MinTurnSeconds = 0.3;
    private const double PadSeconds = 0.15;
    private const double MaxTurnSeconds = 20.0;    // Whisper works on windows of up to 30 s

    private static readonly HashSet<string> Hallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "[buzz]", "[silence]", "[music]", "(buzz)", "you", "thank you.", "",
    };

    public IReadOnlyList<DialogueTurn> Transcribe(string wavPath, string hostSpeaker, string remoteSpeaker)
    {
        var audio = WavReader.Read(wavPath);
        var host = audio.Channels[0];
        var remote = audio.Channels.Length > 1 ? audio.Channels[1] : new float[host.Length];
        var frame = Math.Max(1, (int)(FrameSeconds * audio.SampleRate));
        var hostLevels = Levels(host, frame);
        var remoteLevels = Levels(remote, frame);

        var dialogue = new List<DialogueTurn>();
        Collect(dialogue, host, hostLevels, remoteLevels, frame, audio.SampleRate, hostSpeaker, 0);
        Collect(dialogue, remote, remoteLevels, hostLevels, frame, audio.SampleRate, remoteSpeaker, 1);
        return dialogue.OrderBy(d => d.StartSec).ThenBy(d => d.Channel).ToList();
    }

    private static float[] Levels(float[] samples, int frame)
    {
        var levels = new float[(samples.Length + frame - 1) / frame];
        for (var i = 0; i < levels.Length; i++)
            levels[i] = AudioMath.Rms(samples.AsSpan(i * frame, Math.Min(frame, samples.Length - i * frame)));
        return levels;
    }

    private void Collect(List<DialogueTurn> dialogue, float[] samples, float[] own, float[] other, int frame, int sampleRate, string speaker, int channel)
    {
        var maxGap = (int)Math.Round(MaxGapSeconds / FrameSeconds);
        var minLen = (int)Math.Round(MinTurnSeconds / FrameSeconds);
        var pad = (int)Math.Round(PadSeconds / FrameSeconds);
        var maxLen = (int)Math.Round(MaxTurnSeconds / FrameSeconds);

        var start = -1;
        var lastActive = -1;
        for (var i = 0; i <= own.Length; i++)
        {
            var active = i < own.Length && own[i] > ActiveRms && own[i] >= other[i] * BleedRatio;
            if (active)
            {
                if (start < 0) start = i;
                lastActive = i;
            }
            if (start >= 0 && (i == own.Length || i - lastActive > maxGap))
            {
                if (lastActive - start + 1 >= minLen)
                {
                    var from = Math.Max(0, start - pad);
                    var to = Math.Min(own.Length, lastActive + 1 + pad);
                    while (to - from > maxLen)       // very long monologue: cut at its quietest point near the limit
                    {
                        var cut = QuietestFrame(own, from + maxLen - (int)Math.Round(3.0 / FrameSeconds), from + maxLen);
                        Recognize(dialogue, samples, from, cut, frame, sampleRate, speaker, channel);
                        from = cut;
                    }
                    Recognize(dialogue, samples, from, to, frame, sampleRate, speaker, channel);
                }
                start = -1;
            }
        }
    }

    /// <summary>Index of the quietest frame in [from, to); the caller guarantees from &lt; to.</summary>
    private static int QuietestFrame(float[] levels, int from, int to)
    {
        var best = from;
        for (var i = from + 1; i < to; i++) if (levels[i] < levels[best]) best = i;
        return best;
    }

    private void Recognize(List<DialogueTurn> dialogue, float[] samples, int fromFrame, int toFrame, int frame, int sampleRate, string speaker, int channel)
    {
        var from = fromFrame * frame;
        var to = Math.Min(samples.Length, toFrame * frame);
        if (to <= from) return;
        var text = recognizer.Recognize(samples.AsSpan(from, to - from).ToArray(), sampleRate).Trim();
        if (Hallucinations.Contains(text)) return;
        var startSec = from / (double)sampleRate;
        var total = (int)startSec;
        dialogue.Add(new DialogueTurn(Math.Round(startSec, 2), $"{total / 60:00}:{total % 60:00}", speaker, channel, text));
    }
}
