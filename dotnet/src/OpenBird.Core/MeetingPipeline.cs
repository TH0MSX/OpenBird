namespace OpenBird.Core;

/// <summary>Speech-to-text for a finished two-channel recording (channel 0 = host mic, 1 = system audio).</summary>
public interface ITranscriber
{
    IReadOnlyList<DialogueTurn> Transcribe(string wavPath, string hostSpeaker, string remoteSpeaker);
}

/// <summary>Optional cloud or local-model summary. Return null to fall back to the offline summarizer.</summary>
public interface ISummaryProvider
{
    Task<MeetingSummary?> SummarizeAsync(string meetingName, IReadOnlyList<DialogueTurn> dialogue,
        IReadOnlyList<ScreenFrame> screenContext, string template, string userName, CancellationToken ct);
}

public sealed record FinishedRecording(string MeetingId, string Title, string Tags, double StartTimestamp, double DurationSec, string? WavPath);

public sealed record ProcessedMeeting(string MeetingId, string Title, string MarkdownPath, MeetingSummary Summary, int Turns, string? Warning);

/// <summary>
/// Turns a finished recording into saved notes: transcribe, gather screen
/// context, summarize, render, store. Each meeting id is processed once; the
/// Python app processed manually stopped meetings twice.
/// </summary>
public sealed class MeetingPipeline
{
    private readonly Database _db;
    private readonly AppSettings _settings;
    private readonly ITranscriber? _transcriber;
    private readonly ISummaryProvider? _provider;
    private readonly HashSet<string> _seen = new();
    private readonly object _gate = new();

    public MeetingPipeline(Database db, AppSettings settings, ITranscriber? transcriber = null, ISummaryProvider? provider = null)
    {
        _db = db;
        _settings = settings;
        _transcriber = transcriber;
        _provider = provider;
    }

    public async Task<ProcessedMeeting?> ProcessAsync(FinishedRecording rec, string? template = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_seen.Add(rec.MeetingId)) return null;   // already handled
        }

        var user = _settings.UserName;
        string? warning = null;
        IReadOnlyList<DialogueTurn> dialogue = Array.Empty<DialogueTurn>();
        if (rec.WavPath is not null && File.Exists(rec.WavPath) && rec.DurationSec > 1.0)
        {
            if (_transcriber is null) warning = "No speech model is installed; notes were saved without a transcript.";
            else
            {
                try { dialogue = _transcriber.Transcribe(rec.WavPath, user, "Remote Participants"); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    warning = $"Speech model not found; notes were saved without a transcript. ({e.Message})";
                }
            }
        }

        var frames = _db.GetScreenFrames(rec.StartTimestamp, rec.StartTimestamp + rec.DurationSec + 5);
        template ??= _settings.DefaultTemplate;

        MeetingSummary? summary = null;
        if (_provider is not null && _settings.EffectiveAiMode != "off" && template != "verbatim")
        {
            try { summary = await _provider.SummarizeAsync(rec.Title, dialogue, frames, template, user, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                warning = (warning is null ? "" : warning + " ") + $"The AI summary failed, so the built-in summary was used. ({e.Message})";
            }
        }
        summary ??= LocalSummarizer.Summarize(rec.Title, dialogue, frames, template, user);

        var markdown = MarkdownRenderer.Render(summary, dialogue, frames, user);
        var path = _db.SaveMeeting(rec.MeetingId, rec.Title, rec.DurationSec, rec.WavPath, summary, dialogue, frames, markdown, rec.Tags, user);
        return new ProcessedMeeting(rec.MeetingId, rec.Title, path, summary, dialogue.Count, warning);
    }
}
