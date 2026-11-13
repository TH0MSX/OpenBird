using System.Globalization;

namespace OpenBird.Core;

public sealed partial class Database
{
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Meetings recorded on a local calendar day. created_at is stored in UTC, so it is converted first.</summary>
    public List<MeetingRecord> GetMeetingsOn(DateOnly date)
    {
        using var conn = Open();
        return Query(conn, """
            SELECT id, title, strftime('%H:%M', created_at, 'localtime'), duration_sec, markdown_path, summary_json, tags
            FROM meetings WHERE date(created_at, 'localtime') = $d ORDER BY created_at ASC, rowid ASC;
            """,
            r => new MeetingRecord { Id = r.GetString(0), Title = Str(r, 1), CreatedAt = Str(r, 2), DurationSec = Dbl(r, 3), MarkdownPath = StrOrNull(r, 4), Summary = ParseSummary(StrOrNull(r, 5)), Tags = Str(r, 6) },
            ("$d", Day(date)));
    }

    /// <summary>Action items created on a local calendar day, or belonging to a meeting from that day.</summary>
    public List<ActionItem> GetActionsOn(DateOnly date)
    {
        using var conn = Open();
        return Query(conn, """
            SELECT a.id, a.meeting_id, m.title, a.owner, a.task, a.status, a.created_at
            FROM action_items a LEFT JOIN meetings m ON a.meeting_id = m.id
            WHERE date(a.created_at, 'localtime') = $d OR date(m.created_at, 'localtime') = $d
            ORDER BY a.created_at ASC, a.id ASC;
            """,
            r => new ActionItem { Id = r.GetInt64(0), MeetingId = StrOrNull(r, 1), MeetingTitle = StrOrNull(r, 2), Owner = Str(r, 3), Task = Str(r, 4), Status = Str(r, 5), CreatedAt = StrOrNull(r, 6) },
            ("$d", Day(date)));
    }

    /// <summary>Window titles of the screen frames captured on a local day (time_str is already local time).</summary>
    public List<string> GetScreenWindowTitlesOn(DateOnly date)
    {
        using var conn = Open();
        return Query(conn, "SELECT window_title FROM screen_timeline WHERE time_str LIKE $d ORDER BY timestamp ASC;",
            r => Str(r, 0), ("$d", Day(date) + "%"));
    }
}
