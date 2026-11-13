namespace OpenBird.Core;

public sealed record AskResult(string Question, string Answer, IReadOnlyList<SearchHit> Sources,
    int MeetingMatches, int ScreenMatches, int ActionMatches, string? Notice = null);

/// <summary>
/// Answers a question from local memory: meeting transcripts, the screen
/// timeline and tracked actions. Retrieval uses the FTS5 indexes; the answer is
/// extractive (quotes with sources), so it works with no model and no network.
/// </summary>
public sealed class AskEngine
{
    private readonly Database _db;
    private readonly string _userName;

    public AskEngine(Database db, string userName)
    {
        _db = db;
        _userName = userName;
    }

    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "what", "about", "how", "also", "the", "that", "this", "those", "these", "they", "them", "then", "who", "when",
        "where", "why", "which", "did", "does", "was", "were", "are", "for", "with", "from", "more", "else", "any", "said", "say",
    };

    /// <summary>
    /// A follow-up such as "and what about Monday?" has too few words of its own to
    /// search with, so the previous question's words are searched together with it.
    /// </summary>
    public static bool IsFollowUp(string question) =>
        question.Split(new[] { ' ', ',', '?', '.', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Length > 2 && !FillerWords.Contains(w)) < 2;

    public AskResult Ask(string question, int maxResults = 8, string? previousQuestion = null, IReadOnlyList<string>? relatedTerms = null)
    {
        var q = (question ?? "").Trim();
        if (q.Length == 0)
            return new AskResult(q, "Type a question to search your meetings and screen history.", Array.Empty<SearchHit>(), 0, 0, 0);

        var asked = q;
        if (!string.IsNullOrWhiteSpace(previousQuestion) && IsFollowUp(q))
            q = string.Join(' ', (previousQuestion + " " + q).Split(new[] { ' ', ',', '?', '.', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !FillerWords.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (q.Length == 0) q = asked;

        var meetingHits = _db.SearchTranscripts(q, maxResults);
        foreach (var m in _db.SearchMeetingTitles(q))
            if (meetingHits.All(h => h.MeetingId != m.Id))
                meetingHits.Add(new SearchHit("meeting", m.Title, m.CreatedAt, $"Tags: {(string.IsNullOrEmpty(m.Tags) ? "General" : m.Tags)}", m.Id, m.MarkdownPath, 0));
        var screenHits = _db.SearchScreen(q, maxResults);
        var actionHits = _db.SearchActions(q);

        // Related words suggested by a model widen the search: anything that matches one of
        // them is added after the direct matches, so exact hits still come first.
        if (relatedTerms is { Count: > 0 })
        {
            var related = string.Join(' ', relatedTerms);
            foreach (var h in _db.SearchTranscripts(related, maxResults))
                if (meetingHits.Count < maxResults && meetingHits.All(x => x.Snippet != h.Snippet)) meetingHits.Add(h);
            foreach (var h in _db.SearchScreen(related, maxResults))
                if (screenHits.Count < maxResults && screenHits.All(x => x.Snippet != h.Snippet)) screenHits.Add(h);
        }

        var sources = new List<SearchHit>(meetingHits);
        sources.AddRange(screenHits);
        sources.AddRange(actionHits.Select(a => new SearchHit("action", $"Action: {(a.Task.Length > 40 ? a.Task[..40] : a.Task)}",
            a.CreatedAt ?? "", $"Owner: {a.Owner} | Status: {a.Status} | {a.Task}", a.MeetingId, null, 0)));

        return new AskResult(asked, Extractive(asked, meetingHits, screenHits, actionHits), sources, meetingHits.Count, screenHits.Count, actionHits.Count);
    }

    /// <summary>
    /// Same retrieval as <see cref="Ask"/>, but lets an allowed cloud model write the
    /// answer from the retrieved excerpts. Falls back to the extractive answer when
    /// no model is allowed, nothing was found, or every model fails.
    /// </summary>
    public async Task<AskResult> AskAsync(string question, IReadOnlyList<IChatModel> models, int maxResults = 8,
        CancellationToken ct = default, string? previousQuestion = null, string? previousAnswer = null)
    {
        if (models.Count == 0) return Ask(question, maxResults, previousQuestion);
        IReadOnlyList<string> related;
        using (var quick = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            quick.CancelAfter(TimeSpan.FromSeconds(20));      // widening the search is optional: never let it stall the answer
            try { related = await RelatedTermsAsync(question, models, quick.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { related = Array.Empty<string>(); }
        }
        var result = Ask(question, maxResults, previousQuestion, related);
        if (result.Sources.Count == 0) return result;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are OpenBird, {_userName}'s assistant. Answer the question using ONLY the numbered evidence below.");
        if (!string.IsNullOrWhiteSpace(previousQuestion) && !string.IsNullOrWhiteSpace(previousAnswer))
        {
            var earlier = previousAnswer.Length > 1500 ? previousAnswer[..1500] : previousAnswer;
            sb.AppendLine("Earlier in this conversation:").AppendLine($"Q: {previousQuestion}").AppendLine($"A: {earlier}").AppendLine();
        }
        sb.AppendLine($"Question: \"{result.Question}\"").AppendLine().AppendLine("Evidence:");
        for (var i = 0; i < result.Sources.Count; i++)
        {
            var s = result.Sources[i];
            sb.AppendLine($"[{i + 1}] {s.Kind} | {s.Title} | {s.When}").AppendLine($"    {s.Snippet}");
        }
        sb.AppendLine().AppendLine("Be direct and concise. Cite evidence numbers like [1]. If the evidence does not answer the question, say so.");

        string? failure = null;
        foreach (var model in models)
        {
            try
            {
                var answer = (await model.CompleteAsync(sb.ToString(), jsonOutput: false, ct)).Trim();
                if (answer.Length > 0) return result with { Answer = answer };
                failure = $"{model.Name} returned an empty reply";
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                failure = $"{model.Name}: {e.Message}";
            }
        }
        return result with { Notice = $"The AI model did not answer ({failure}). These are the matching excerpts instead." };
    }

    /// <summary>
    /// Asks a model for other words the same thing might have been written with
    /// ("invoice" for "bill"). Returns nothing on any failure: search then runs on the question alone.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RelatedTermsAsync(string question, IReadOnlyList<IChatModel> models, CancellationToken ct)
    {
        var prompt = "Someone is searching their meeting notes and screen history. Give up to 6 other single words or short phrases " +
                     "(synonyms, related terms, abbreviations) that relevant notes might use instead of the words in the question. " +
                     "Do not repeat words from the question. Return ONLY JSON: {\"terms\": [\"...\"]}\n\nQuestion: " + question;
        foreach (var model in models)
        {
            try
            {
                var json = CloudJson.StripFence(await model.CompleteAsync(prompt, jsonOutput: true, ct));
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !doc.RootElement.TryGetProperty("terms", out var terms)
                    || terms.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                return terms.EnumerateArray().Where(t => t.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(t => t.GetString()!.Trim()).Where(t => t.Length is > 1 and < 40).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
            }
        }
        return Array.Empty<string>();
    }

    private string Extractive(string question, List<SearchHit> meetings, List<SearchHit> screens, List<ActionItem> actions)
    {
        if (meetings.Count == 0 && screens.Count == 0 && actions.Count == 0)
            return $"### No matches\n\nNothing matched **\"{question}\"** in your meetings or screen history.\n\n*Try fewer or broader words.*";

        var lines = new List<string> { $"### Results for: \"{question}\"\n" };
        if (meetings.Count > 0)
        {
            lines.Add("#### Meetings");
            foreach (var m in meetings.Take(4))
            {
                lines.Add($"- **{m.Title}** ({(m.When.Length > 16 ? m.When[..16] : m.When)}):");
                lines.Add($"  > {m.Snippet}");
            }
            lines.Add("");
        }
        if (actions.Count > 0)
        {
            lines.Add("#### Action items");
            foreach (var a in actions.Take(4))
                lines.Add($"- {(a.IsCompleted ? "Done" : "Open")} · **{(string.IsNullOrEmpty(a.Owner) ? _userName : a.Owner)}**: {a.Task} *(Meeting: {a.MeetingTitle ?? "General"})*");
            lines.Add("");
        }
        if (screens.Count > 0)
        {
            lines.Add("#### Screen history");
            foreach (var s in screens.Take(4))
            {
                lines.Add($"- **{s.Title}** at `{s.When}`:");
                lines.Add($"  `{s.Snippet}`");
            }
            lines.Add("");
        }
        lines.Add("---");
        lines.Add($"*Found {meetings.Count} meeting matches, {actions.Count} action items and {screens.Count} screen matches.*");
        return string.Join("\n", lines);
    }
}
