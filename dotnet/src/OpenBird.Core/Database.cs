using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace OpenBird.Core;

/// <summary>
/// SQLite store. Tables and columns match the Python app, so an existing
/// database opens as it is. On top of that it adds FTS5 full-text indexes
/// (kept in step by triggers), replacing the old LIKE '%term%' scans.
/// </summary>
public sealed partial class Database
{
    private readonly string _connectionString;
    public string DbPath { get; }
    public string NotesDir { get; }

    public Database(string dbPath, string notesDir)
    {
        DbPath = dbPath;
        NotesDir = notesDir;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        Directory.CreateDirectory(notesDir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 10 }.ToString();
        Initialise();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 10000;");
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static string? StrOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static double Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    private void Initialise()
    {
        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS meetings (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                duration_sec REAL,
                wav_path TEXT,
                markdown_path TEXT,
                summary_json TEXT,
                tags TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS transcripts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                meeting_id TEXT,
                start_sec REAL,
                timestamp_str TEXT,
                speaker TEXT,
                text TEXT,
                FOREIGN KEY(meeting_id) REFERENCES meetings(id)
            );
            CREATE TABLE IF NOT EXISTS action_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                meeting_id TEXT,
                owner TEXT,
                task TEXT,
                status TEXT DEFAULT 'pending',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY(meeting_id) REFERENCES meetings(id)
            );
            CREATE TABLE IF NOT EXISTS screen_context (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                meeting_id TEXT,
                timestamp REAL,
                window_title TEXT,
                image_path TEXT,
                ocr_text TEXT
            );
            CREATE TABLE IF NOT EXISTS screen_timeline (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp REAL,
                time_str TEXT,
                window_title TEXT,
                image_path TEXT,
                ocr_text TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_actions_meeting ON action_items(meeting_id);
            CREATE INDEX IF NOT EXISTS idx_actions_status ON action_items(status);
            CREATE INDEX IF NOT EXISTS idx_transcripts_meeting ON transcripts(meeting_id);
            CREATE INDEX IF NOT EXISTS idx_meetings_tags ON meetings(tags);
            CREATE INDEX IF NOT EXISTS idx_screen_timestamp ON screen_timeline(timestamp);
            """);

        // Databases created by OpenBird 2.1/2.2 have no tags column.
        var columns = Query(conn, "PRAGMA table_info(meetings);", r => r.GetString(1));
        if (!columns.Contains("tags")) Exec(conn, "ALTER TABLE meetings ADD COLUMN tags TEXT DEFAULT '';");

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS action_cards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                meeting_id TEXT,
                title TEXT NOT NULL,
                detail TEXT,
                status TEXT DEFAULT 'new'
            );
            CREATE INDEX IF NOT EXISTS idx_cards_status ON action_cards(status);
            """);

        EnsureFts(conn, "transcripts_fts", "transcripts", new[] { "text", "speaker" });
        EnsureFts(conn, "screen_fts", "screen_timeline", new[] { "window_title", "ocr_text" });
    }

    /// <summary>Creates an external-content FTS5 index with sync triggers, and fills it from existing rows once.</summary>
    private static void EnsureFts(SqliteConnection conn, string fts, string table, string[] cols)
    {
        var exists = Query(conn, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;", r => 1, ("$n", fts)).Count > 0;
        var colList = string.Join(", ", cols);
        var newVals = string.Join(", ", cols.Select(c => "new." + c));
        var oldVals = string.Join(", ", cols.Select(c => "old." + c));
        Exec(conn, $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {fts} USING fts5({colList}, content='{table}', content_rowid='id', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS {fts}_ai AFTER INSERT ON {table} BEGIN
                INSERT INTO {fts}(rowid, {colList}) VALUES (new.id, {newVals});
            END;
            CREATE TRIGGER IF NOT EXISTS {fts}_ad AFTER DELETE ON {table} BEGIN
                INSERT INTO {fts}({fts}, rowid, {colList}) VALUES ('delete', old.id, {oldVals});
            END;
            CREATE TRIGGER IF NOT EXISTS {fts}_au AFTER UPDATE ON {table} BEGIN
                INSERT INTO {fts}({fts}, rowid, {colList}) VALUES ('delete', old.id, {oldVals});
                INSERT INTO {fts}(rowid, {colList}) VALUES (new.id, {newVals});
            END;
            """);
        if (!exists) Exec(conn, $"INSERT INTO {fts}({fts}) VALUES ('rebuild');");
    }

    // ------------------------------------------------------------ meetings

    /// <summary>
    /// Saves a meeting and its note file. Saving the same id again replaces the
    /// earlier rows instead of duplicating the transcript and action items.
    /// </summary>
    public string SaveMeeting(string meetingId, string title, double durationSec, string? wavPath,
        MeetingSummary summary, IReadOnlyList<DialogueTurn> dialogue, IReadOnlyList<ScreenFrame>? screenContext,
        string markdown, string tags = "", string? defaultOwner = null, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var mdPath = Path.Combine(NotesDir, $"{meetingId}_{stamp}.md");
        File.WriteAllText(mdPath, markdown, new UTF8Encoding(false));

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Exec(conn, "DELETE FROM transcripts WHERE meeting_id = $id;", ("$id", meetingId));
        Exec(conn, "DELETE FROM action_items WHERE meeting_id = $id;", ("$id", meetingId));
        Exec(conn, "DELETE FROM screen_context WHERE meeting_id = $id;", ("$id", meetingId));
        Exec(conn, """
            INSERT INTO meetings (id, title, duration_sec, wav_path, markdown_path, summary_json, tags)
            VALUES ($id, $title, $dur, $wav, $md, $json, $tags)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, duration_sec = excluded.duration_sec,
                wav_path = excluded.wav_path, markdown_path = excluded.markdown_path,
                summary_json = excluded.summary_json, tags = excluded.tags;
            """,
            ("$id", meetingId), ("$title", title), ("$dur", durationSec), ("$wav", wavPath), ("$md", mdPath),
            ("$json", JsonSerializer.Serialize(summary)), ("$tags", tags));

        foreach (var d in dialogue)
            Exec(conn, "INSERT INTO transcripts (meeting_id, start_sec, timestamp_str, speaker, text) VALUES ($id, $s, $ts, $sp, $tx);",
                ("$id", meetingId), ("$s", d.StartSec), ("$ts", d.Timestamp), ("$sp", d.Speaker), ("$tx", d.Text));

        foreach (var a in summary.ActionItems)
            Exec(conn, "INSERT INTO action_items (meeting_id, owner, task, status) VALUES ($id, $o, $t, $st);",
                ("$id", meetingId), ("$o", string.IsNullOrEmpty(a.Owner) ? defaultOwner ?? "Me" : a.Owner), ("$t", a.Task), ("$st", a.Status));

        foreach (var s in screenContext ?? Array.Empty<ScreenFrame>())
            Exec(conn, "INSERT INTO screen_context (meeting_id, timestamp, window_title, image_path, ocr_text) VALUES ($id, $t, $w, $p, $o);",
                ("$id", meetingId), ("$t", s.Timestamp), ("$w", s.WindowTitle), ("$p", s.ImagePath ?? ""), ("$o", s.OcrText));
        tx.Commit();
        return mdPath;
    }

    public List<MeetingRecord> ListMeetings(string? tag = null, int limit = 50)
    {
        using var conn = Open();
        var where = string.IsNullOrEmpty(tag) ? "" : "WHERE tags LIKE $tag";
        return Query(conn, $"SELECT id, title, created_at, duration_sec, markdown_path, tags FROM meetings {where} ORDER BY created_at DESC, rowid DESC LIMIT $limit;",
            r => new MeetingRecord { Id = r.GetString(0), Title = Str(r, 1), CreatedAt = Str(r, 2), DurationSec = Dbl(r, 3), MarkdownPath = StrOrNull(r, 4), Tags = Str(r, 5) },
            ("$tag", $"%{tag}%"), ("$limit", limit));
    }

    public MeetingRecord? GetMeeting(string meetingId)
    {
        using var conn = Open();
        var m = Query(conn, "SELECT id, title, created_at, duration_sec, wav_path, markdown_path, summary_json, tags FROM meetings WHERE id = $id;",
            r => new MeetingRecord
            {
                Id = r.GetString(0), Title = Str(r, 1), CreatedAt = Str(r, 2), DurationSec = Dbl(r, 3),
                WavPath = StrOrNull(r, 4), MarkdownPath = StrOrNull(r, 5), Summary = ParseSummary(StrOrNull(r, 6)), Tags = Str(r, 7),
            }, ("$id", meetingId)).FirstOrDefault();
        if (m is null) return null;
        m.Transcript = Query(conn, "SELECT start_sec, timestamp_str, speaker, text FROM transcripts WHERE meeting_id = $id ORDER BY start_sec ASC, id ASC;",
            r => new DialogueTurn(Dbl(r, 0), Str(r, 1), Str(r, 2), 0, Str(r, 3)), ("$id", meetingId));
        m.ActionItems = Query(conn, "SELECT id, owner, task, status FROM action_items WHERE meeting_id = $id ORDER BY id;",
            r => new ActionItem { Id = r.GetInt64(0), Owner = Str(r, 1), Task = Str(r, 2), Status = Str(r, 3), MeetingId = meetingId }, ("$id", meetingId));
        return m;
    }

    private static MeetingSummary ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new MeetingSummary();
        try { return JsonSerializer.Deserialize<MeetingSummary>(json) ?? new MeetingSummary(); }
        catch (JsonException) { return new MeetingSummary(); }
    }

    public void UpdateTags(string meetingId, string tags)
    {
        using var conn = Open();
        Exec(conn, "UPDATE meetings SET tags = $t WHERE id = $id;", ("$t", tags), ("$id", meetingId));
    }

    public List<string> GetAllTags()
    {
        using var conn = Open();
        return Query(conn, "SELECT tags FROM meetings WHERE tags IS NOT NULL AND tags != '';", r => r.GetString(0))
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    // ------------------------------------------------------------ actions

    public List<ActionItem> GetActions(bool pendingOnly = false)
    {
        using var conn = Open();
        var where = pendingOnly ? "WHERE a.status = 'pending'" : "";
        return Query(conn, $"""
            SELECT a.id, a.meeting_id, m.title, a.owner, a.task, a.status, a.created_at
            FROM action_items a LEFT JOIN meetings m ON a.meeting_id = m.id
            {where} ORDER BY a.created_at DESC, a.id DESC;
            """,
            r => new ActionItem { Id = r.GetInt64(0), MeetingId = StrOrNull(r, 1), MeetingTitle = StrOrNull(r, 2), Owner = Str(r, 3), Task = Str(r, 4), Status = Str(r, 5), CreatedAt = StrOrNull(r, 6) });
    }

    /// <summary>Marks an action done or pending in the database and in the meeting's note file.</summary>
    public void SetActionCompleted(long actionId, bool completed)
    {
        using var conn = Open();
        var row = Query(conn, "SELECT a.task, m.markdown_path FROM action_items a LEFT JOIN meetings m ON a.meeting_id = m.id WHERE a.id = $id;",
            r => (Task: Str(r, 0), Md: StrOrNull(r, 1)), ("$id", actionId)).FirstOrDefault();
        Exec(conn, "UPDATE action_items SET status = $s WHERE id = $id;", ("$s", completed ? "completed" : "pending"), ("$id", actionId));
        if (row.Md is null || !File.Exists(row.Md) || string.IsNullOrEmpty(row.Task)) return;

        var (oldTag, newTag) = completed ? ("- [ ]", "- [x]") : ("- [x]", "- [ ]");
        var lines = File.ReadAllText(row.Md).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(row.Task, StringComparison.Ordinal)) continue;
            var at = lines[i].IndexOf(oldTag, StringComparison.Ordinal);
            if (at >= 0) lines[i] = lines[i][..at] + newTag + lines[i][(at + oldTag.Length)..];
        }
        File.WriteAllText(row.Md, string.Join('\n', lines), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------ screen timeline

    public long AddScreenFrame(ScreenFrame frame)
    {
        using var conn = Open();
        Exec(conn, "INSERT INTO screen_timeline (timestamp, time_str, window_title, image_path, ocr_text) VALUES ($t, $ts, $w, $p, $o);",
            ("$t", frame.Timestamp), ("$ts", frame.TimeStr), ("$w", frame.WindowTitle), ("$p", frame.ImagePath ?? ""), ("$o", frame.OcrText));
        return Query(conn, "SELECT last_insert_rowid();", r => r.GetInt64(0))[0];
    }

    public List<ScreenFrame> GetScreenFrames(double fromTimestamp, double toTimestamp, int limit = 5000)
    {
        using var conn = Open();
        return Query(conn, "SELECT timestamp, time_str, window_title, image_path, ocr_text FROM screen_timeline WHERE timestamp >= $a AND timestamp <= $b ORDER BY timestamp ASC LIMIT $l;",
            r => new ScreenFrame(Dbl(r, 0), Str(r, 1), Str(r, 2), StrOrNull(r, 3), Str(r, 4)), ("$a", fromTimestamp), ("$b", toTimestamp), ("$l", limit));
    }

    // ------------------------------------------------------------ search

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();

    /// <summary>Turns free text into a safe FTS5 query: each word as a quoted prefix term.</summary>
    public static string BuildMatchQuery(string text, bool any = false)
    {
        var words = WordRegex().Matches(text ?? "").Select(m => m.Value).Where(w => w.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        if (words.Count == 0) return "";
        return string.Join(any ? " OR " : " ", words.Select(w => $"\"{w}\"*"));
    }

    public List<SearchHit> SearchTranscripts(string text, int limit = 10) => FtsSearch(text, limit, """
        SELECT 'meeting', m.title, m.created_at,
               '[' || t.timestamp_str || '] ' || t.speaker || ': ' || snippet(transcripts_fts, 0, '', '', '…', 24),
               t.meeting_id, m.markdown_path, bm25(transcripts_fts)
        FROM transcripts_fts JOIN transcripts t ON t.id = transcripts_fts.rowid JOIN meetings m ON m.id = t.meeting_id
        WHERE transcripts_fts MATCH $q ORDER BY bm25(transcripts_fts), t.id DESC LIMIT $l;
        """);

    public List<SearchHit> SearchScreen(string text, int limit = 10) => FtsSearch(text, limit, """
        SELECT 'screen', s.window_title, s.time_str, snippet(screen_fts, 1, '', '', '…', 24),
               NULL, s.image_path, bm25(screen_fts)
        FROM screen_fts JOIN screen_timeline s ON s.id = screen_fts.rowid
        WHERE screen_fts MATCH $q ORDER BY bm25(screen_fts), s.timestamp DESC LIMIT $l;
        """);

    private List<SearchHit> FtsSearch(string text, int limit, string sql)
    {
        using var conn = Open();
        foreach (var any in new[] { false, true })   // all words first, then any word
        {
            var q = BuildMatchQuery(text, any);
            if (q.Length == 0) return new List<SearchHit>();
            var hits = Query(conn, sql, r => new SearchHit(r.GetString(0), Str(r, 1), Str(r, 2), Str(r, 3).Replace('\n', ' ').Trim(), StrOrNull(r, 4), StrOrNull(r, 5), Dbl(r, 6)),
                ("$q", q), ("$l", limit));
            if (hits.Count > 0) return hits;
        }
        return new List<SearchHit>();
    }

    public List<MeetingRecord> SearchMeetingTitles(string text, int limit = 6)
    {
        using var conn = Open();
        return Query(conn, "SELECT id, title, created_at, duration_sec, markdown_path, tags FROM meetings WHERE title LIKE $q OR tags LIKE $q ORDER BY created_at DESC LIMIT $l;",
            r => new MeetingRecord { Id = r.GetString(0), Title = Str(r, 1), CreatedAt = Str(r, 2), DurationSec = Dbl(r, 3), MarkdownPath = StrOrNull(r, 4), Tags = Str(r, 5) },
            ("$q", $"%{text}%"), ("$l", limit));
    }

    public List<ActionItem> SearchActions(string text, int limit = 5)
    {
        using var conn = Open();
        return Query(conn, """
            SELECT a.id, a.meeting_id, m.title, a.owner, a.task, a.status, a.created_at
            FROM action_items a LEFT JOIN meetings m ON a.meeting_id = m.id
            WHERE a.task LIKE $q OR a.owner LIKE $q ORDER BY a.created_at DESC, a.id DESC LIMIT $l;
            """,
            r => new ActionItem { Id = r.GetInt64(0), MeetingId = StrOrNull(r, 1), MeetingTitle = StrOrNull(r, 2), Owner = Str(r, 3), Task = Str(r, 4), Status = Str(r, 5), CreatedAt = StrOrNull(r, 6) },
            ("$q", $"%{text}%"), ("$l", limit));
    }
}
