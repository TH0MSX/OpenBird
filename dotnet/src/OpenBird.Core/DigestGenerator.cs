using System.Globalization;
using System.Text;

namespace OpenBird.Core;

public sealed record DigestResult(DateOnly Date, string MarkdownPath, string? DocxPath, string Markdown,
    int Meetings, int Actions, int Pending, int Completed, int ScreenFrames);

/// <summary>
/// Daily rollup of meetings, action items and screen focus.
/// Differences from the Python version, on purpose: days are local calendar days
/// (Python compared a local date with UTC timestamps), the "Focus" column is
/// filled from the meeting's one-line summary (Python read a field that does not
/// exist), and the Word file contains the digest (Python exported the file path).
/// </summary>
public sealed class DigestGenerator
{
    private readonly Database _db;
    private readonly string _outputDir;
    private readonly string _userName;

    public DigestGenerator(Database db, string userName, string? outputDir = null)
    {
        _db = db;
        _userName = userName;
        _outputDir = outputDir ?? Path.Combine(db.NotesDir, "digests");
    }

    public DigestResult Generate(DateOnly? date = null, DateTime? now = null, bool writeDocx = true)
    {
        var stamp = now ?? DateTime.Now;
        var day = date ?? DateOnly.FromDateTime(stamp);
        var dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var inv = CultureInfo.InvariantCulture;

        var meetings = _db.GetMeetingsOn(day);
        var actions = _db.GetActionsOn(day);
        var windows = _db.GetScreenWindowTitlesOn(day);
        var pending = actions.Where(a => !a.IsCompleted).ToList();
        var completed = actions.Where(a => a.IsCompleted).ToList();
        var totalMinutes = Math.Round(meetings.Sum(m => m.DurationSec) / 60.0, 1);

        var lines = new List<string>
        {
            $"# OpenBird Daily Digest: {dayText}",
            $"> Generated {stamp.ToString("yyyy-MM-dd HH:mm", inv)} on this PC.\n",
            "## Summary",
            $"- **Meetings**: {meetings.Count} ({totalMinutes.ToString("0.#", inv)} minutes)",
            $"- **Action items**: {actions.Count} ({pending.Count} open, {completed.Count} done)",
            $"- **Screen captures**: {windows.Count}",
            "",
            "## Meetings",
        };

        if (meetings.Count > 0)
        {
            lines.Add("| Time | Meeting | Tags | Length | Focus |");
            lines.Add("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var m in meetings)
            {
                var focus = Cell(Shorten(string.IsNullOrWhiteSpace(m.Summary.Tldr) ? m.Summary.Summary : m.Summary.Tldr, 90));
                var minutes = Math.Round(m.DurationSec / 60.0, 1).ToString("0.#", inv);
                lines.Add($"| {m.CreatedAt} | **{Cell(m.Title)}** | {Cell(string.IsNullOrEmpty(m.Tags) ? "General" : m.Tags)} | {minutes} min | {focus} |");
            }
        }
        else lines.Add("*No meetings recorded on this day.*");
        lines.Add("");

        lines.Add("## Action items");
        if (actions.Count == 0) lines.Add("*No action items for this day.*");
        else
        {
            lines.Add("### Open");
            if (pending.Count == 0) lines.Add("*Everything from this day is done.*");
            foreach (var a in pending) lines.Add($"- [ ] **{Owner(a)}**: {a.Task} *(from {a.MeetingTitle ?? "General"})*");
            if (completed.Count > 0)
            {
                lines.Add("");
                lines.Add("### Done");
                foreach (var a in completed) lines.Add($"- [x] **{Owner(a)}**: {a.Task}");
            }
        }
        lines.Add("");

        lines.Add("## Screen focus");
        if (windows.Count == 0) lines.Add("*No screen captures for this day.*");
        else
        {
            var apps = windows.Select(AppName).GroupBy(a => a).Select(g => (App: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.App, StringComparer.Ordinal).Take(6);
            foreach (var (app, count) in apps)
                lines.Add($"- **{app}**: {count} {(count == 1 ? "capture" : "captures")} ({(count * 100.0 / windows.Count).ToString("0.#", inv)}%)");
        }

        var markdown = string.Join("\n", lines) + "\n";
        Directory.CreateDirectory(_outputDir);
        var mdPath = Path.Combine(_outputDir, $"{dayText}_Daily_Digest.md");
        File.WriteAllText(mdPath, markdown, new UTF8Encoding(false));

        string? docxPath = null;
        if (writeDocx)
        {
            docxPath = Path.Combine(_outputDir, $"{dayText}_Daily_Digest.docx");
            WordExporter.Export(markdown, docxPath);
        }
        return new DigestResult(day, mdPath, docxPath, markdown, meetings.Count, actions.Count, pending.Count, completed.Count, windows.Count);
    }

    private string Owner(ActionItem a) => string.IsNullOrEmpty(a.Owner) ? _userName : a.Owner;

    /// <summary>"Report.xlsx - Excel" groups under "Report.xlsx", as in the Python version.</summary>
    private static string AppName(string windowTitle)
    {
        var title = string.IsNullOrEmpty(windowTitle) ? "Desktop" : windowTitle;
        var cut = title.IndexOf(" - ", StringComparison.Ordinal);
        if (cut > 0) title = title[..cut];
        return title.Length > 28 ? title[..28] : title;
    }

    private static string Shorten(string text, int max) => text.Length > max ? text[..max].TrimEnd() + "…" : text;
    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}

/// <summary>Writes the open action items to one checklist file (tasks.md).</summary>
public static class TasksExporter
{
    public static string Export(IEnumerable<ActionItem> pendingActions, string outputPath, string userName, DateTime? now = null)
    {
        var lines = new List<string>
        {
            "# OpenBird Tasks",
            $"> Updated {(now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
            "",
            "## Open",
            "",
        };
        var items = pendingActions.ToList();
        if (items.Count == 0) lines.Add("*No open action items.*");
        foreach (var a in items)
        {
            var created = a.CreatedAt is { Length: >= 10 } c ? c[..10] : "";
            lines.Add($"- [ ] **{a.Task}** (Owner: `{(string.IsNullOrEmpty(a.Owner) ? userName : a.Owner)}` • From: *{a.MeetingTitle ?? "Session"}* • `{created}`)");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return outputPath;
    }
}

/// <summary>Decides when the automatic daily digest should be written.</summary>
public static class DigestSchedule
{
    public static bool TryParse(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact((text ?? "").Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// Due when the scheduled time has passed today and today's digest file was not
    /// already written after that time. Starting the PC late still produces the digest.
    /// </summary>
    public static bool IsDue(DateTime now, string? scheduledTime, DateTime? todaysDigestWrittenAt)
    {
        if (!TryParse(scheduledTime, out var time)) return false;
        var due = now.Date + time.ToTimeSpan();
        if (now < due) return false;
        return todaysDigestWrittenAt is null || todaysDigestWrittenAt < due;
    }
}
