using System.Text;
using System.Text.Json;

namespace OpenBird.Core;

/// <summary>A suggested next step, written by a model after a meeting. The user adds it to tasks or dismisses it.</summary>
public sealed record ActionCard(long Id, string CreatedAt, string? MeetingId, string? MeetingTitle, string Title, string Detail, string Status);

public sealed partial class Database
{
    public long AddCard(string? meetingId, string title, string detail)
    {
        using var conn = Open();
        Exec(conn, "INSERT INTO action_cards (meeting_id, title, detail) VALUES ($m, $t, $d);", ("$m", meetingId), ("$t", title), ("$d", detail));
        return Query(conn, "SELECT last_insert_rowid();", r => r.GetInt64(0))[0];
    }

    public List<ActionCard> GetCards(string status = "new", int limit = 20)
    {
        using var conn = Open();
        return Query(conn, """
            SELECT c.id, c.created_at, c.meeting_id, m.title, c.title, c.detail, c.status
            FROM action_cards c LEFT JOIN meetings m ON m.id = c.meeting_id
            WHERE c.status = $s ORDER BY c.id DESC LIMIT $l;
            """,
            r => new ActionCard(r.GetInt64(0), Str(r, 1), StrOrNull(r, 2), StrOrNull(r, 3), Str(r, 4), Str(r, 5), Str(r, 6)),
            ("$s", status), ("$l", limit));
    }

    public void DismissCard(long cardId)
    {
        using var conn = Open();
        Exec(conn, "UPDATE action_cards SET status = 'dismissed' WHERE id = $id;", ("$id", cardId));
    }

    /// <summary>Turns a suggestion into a tracked action item and marks the card as accepted.</summary>
    public void AcceptCard(long cardId, string owner)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var card = Query(conn, "SELECT meeting_id, title, detail FROM action_cards WHERE id = $id AND status = 'new';",
            r => (Meeting: StrOrNull(r, 0), Title: Str(r, 1), Detail: Str(r, 2)), ("$id", cardId)).FirstOrDefault();
        if (card.Title is null) return;
        var task = string.IsNullOrWhiteSpace(card.Detail) ? card.Title : $"{card.Title}: {card.Detail}";
        Exec(conn, "INSERT INTO action_items (meeting_id, owner, task, status) VALUES ($m, $o, $t, 'pending');", ("$m", card.Meeting), ("$o", owner), ("$t", task));
        Exec(conn, "UPDATE action_cards SET status = 'accepted' WHERE id = $id;", ("$id", cardId));
        tx.Commit();
    }
}

/// <summary>
/// After a meeting, asks the selected model (local or cloud) for a few concrete
/// next steps that are not already in the action items. Does nothing when no
/// model is selected or the meeting had no transcript.
/// </summary>
public static class ActionCardGenerator
{
    public const int MaxCards = 3;

    public static async Task<IReadOnlyList<ActionCard>> GenerateAsync(Database db, IReadOnlyList<IChatModel> models,
        string meetingId, string userName, CancellationToken ct)
    {
        if (models.Count == 0) return Array.Empty<ActionCard>();
        var meeting = db.GetMeeting(meetingId);
        if (meeting is null || meeting.Transcript.Count == 0) return Array.Empty<ActionCard>();

        var prompt = BuildPrompt(meeting, userName);
        foreach (var model in models)
        {
            try
            {
                var json = CloudJson.StripFence(await model.CompleteAsync(prompt, jsonOutput: true, ct));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
                    continue;

                var existing = meeting.ActionItems.Select(a => a.Task).ToList();
                foreach (var c in cards.EnumerateArray().Take(MaxCards))
                {
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                    var title = t.GetString()!.Trim();
                    var detail = c.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : "";
                    if (title.Length is < 3 or > 160) continue;
                    if (existing.Any(e => e.Contains(title, StringComparison.OrdinalIgnoreCase))) continue;    // already an action item
                    db.AddCard(meetingId, title, detail.Length > 600 ? detail[..600] : detail);
                }
                return db.GetCards().Where(c => c.MeetingId == meetingId).ToList();
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
            }
        }
        return Array.Empty<ActionCard>();
    }

    public static string BuildPrompt(MeetingRecord meeting, string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You help {userName} follow up after a meeting. Suggest at most {MaxCards} concrete next steps for {userName} that are useful and NOT already in the action items below.");
        sb.AppendLine("Good suggestions: a follow-up message to send, something to check or prepare, a decision to confirm in writing. Use only what was said; do not invent facts, names or dates.");
        sb.AppendLine("If nothing useful remains, return an empty list.");
        sb.AppendLine().AppendLine($"Meeting: {meeting.Title}");
        if (meeting.ActionItems.Count > 0)
        {
            sb.AppendLine("Action items already recorded:");
            foreach (var a in meeting.ActionItems) sb.AppendLine($"- {a.Owner}: {a.Task}");
        }
        sb.AppendLine().AppendLine("Transcript:");
        var budget = 12000;      // keeps the prompt inside small local models' context
        foreach (var turn in meeting.Transcript)
        {
            var line = $"[{turn.Timestamp}] {turn.Speaker}: {turn.Text}";
            if ((budget -= line.Length) < 0) { sb.AppendLine("[transcript shortened]"); break; }
            sb.AppendLine(line);
        }
        sb.AppendLine().AppendLine("Return ONLY JSON: {\"cards\": [{\"title\": \"short imperative, under 12 words\", \"detail\": \"one sentence of context\"}]}");
        return sb.ToString();
    }
}
