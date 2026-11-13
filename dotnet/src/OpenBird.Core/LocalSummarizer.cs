using System.Text.RegularExpressions;

namespace OpenBird.Core;

/// <summary>
/// Offline, deterministic meeting notes: no model, no network.
/// Port of the Python `_synthesize_local`; output is checked against it in the parity tests.
/// </summary>
public static partial class LocalSummarizer
{
    private static readonly string[] ActionTriggers =
        { "will", "need to", "action", "follow up", "send", "check", "create", "deploy", "review" };

    [GeneratedRegex(@"^(yeah|okay|so|and|well)\b\s*,?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FillerPrefix();

    public static MeetingSummary Summarize(string meetingName, IReadOnlyList<DialogueTurn> dialogue,
        IReadOnlyList<ScreenFrame>? screenContext, string template, string userName)
    {
        var result = new MeetingSummary { Title = meetingName, Template = template };
        var actions = new List<ActionItem>();
        var forYou = new List<string>();

        if (dialogue.Count == 0)
        {
            result.Tldr = "Brief audio recording with minimal speech detected.";
            result.Summary = "Session completed. No distinct conversational turns were transcribed.";
            forYou.Add("Verify audio input levels and microphone selection.");
        }
        else
        {
            var wordCount = string.Join(" ", dialogue.Select(d => d.Text))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            result.Tldr = $"Discussion covering {wordCount} words across {dialogue.Count} conversational turns.";
            result.Summary = $"Session recorded with {dialogue.Count} speaker segments. Topics covered primary operational discussion, workflow updates, and coordination.";

            foreach (var d in dialogue)
            {
                var text = d.Text ?? "";
                var speaker = string.IsNullOrEmpty(d.Speaker) ? userName : d.Speaker;
                var lower = text.ToLowerInvariant();
                if (!ActionTriggers.Any(t => lower.Contains(t, StringComparison.Ordinal))) continue;

                var cleaned = FillerPrefix().Replace(text, "", 1);
                if (cleaned.Length > 15 && actions.All(a => a.Task != cleaned))
                {
                    actions.Add(new ActionItem { Owner = speaker, Task = cleaned, Status = "pending" });
                    if (speaker == userName) forYou.Add(cleaned);
                }
            }

            if (actions.Count == 0)
            {
                actions.Add(new ActionItem { Owner = userName, Task = "Review meeting notes and follow up on discussed points.", Status = "pending" });
                forYou.Add("Review session notes and timeline context.");
            }
        }

        if (screenContext is { Count: > 0 })
        {
            var apps = screenContext.Select(s => s.WindowTitle).Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(4).ToList();
            if (apps.Count > 0)
                result.Topics.Add(new Topic { Title = "On-Screen Context & Work Applications", Summary = $"Active during call: {string.Join(", ", apps)}" });
        }

        result.ForYou = forYou.Take(5).ToList();
        result.ActionItems = actions.Take(8).ToList();
        return result;
    }
}

/// <summary>Renders meeting notes as Markdown. Port of the Python `render_markdown`.</summary>
public static class MarkdownRenderer
{
    public static string Render(MeetingSummary s, IReadOnlyList<DialogueTurn> dialogue, IReadOnlyList<ScreenFrame>? screenContext, string userName)
    {
        var md = new List<string> { $"# Meeting: {s.Title}\n" };
        string Owner(ActionItem a) => string.IsNullOrEmpty(a.Owner) ? userName : a.Owner;

        if (s.Template == "actions_only")
        {
            md.Add("> **Actions & Decisions Brief**: Focused task commitments.\n");
            md.Add("## Action Items\n");
            foreach (var a in s.ActionItems) md.Add($"- [ ] **{Owner(a)}**: {a.Task}");
            md.Add("\n## Decisions Made\n");
            if (s.Decisions.Count > 0) foreach (var d in s.Decisions) md.Add($"- {d}");
            else md.Add("*No explicit decisions recorded.*");
            return string.Join("\n", md).Trim();
        }

        md.Add($"> **TL;DR**: {s.Tldr}\n");
        md.Add("## Executive Summary\n");
        md.Add($"{s.Summary}\n");

        if (s.ForYou.Count > 0)
        {
            md.Add("## For You\n");
            foreach (var item in s.ForYou) md.Add($"- {item}");
            md.Add("");
        }
        if (s.ActionItems.Count > 0)
        {
            md.Add("## Action Items\n");
            foreach (var a in s.ActionItems) md.Add($"- {(a.IsCompleted ? "[x]" : "[ ]")} **{Owner(a)}**: {a.Task}");
            md.Add("");
        }
        if (s.Decisions.Count > 0)
        {
            md.Add("## Decisions\n");
            foreach (var d in s.Decisions) md.Add($"- {d}");
            md.Add("");
        }
        if (s.Topics.Count > 0)
        {
            md.Add("## Key Discussion Topics\n");
            foreach (var t in s.Topics)
            {
                md.Add($"### {t.Title}");
                md.Add($"{t.Summary}\n");
            }
        }
        if (screenContext is { Count: > 0 })
        {
            md.Add("## Visual & Screen Context Timeline\n");
            foreach (var f in screenContext)
            {
                md.Add($"### `[{f.TimeStr}]` {f.WindowTitle}");
                if (!string.IsNullOrEmpty(f.OcrText))
                {
                    var snippet = (f.OcrText.Length > 240 ? f.OcrText[..240] : f.OcrText).Replace("\n", " ").Trim();
                    md.Add($"> {snippet}\n");
                }
            }
            md.Add("");
        }
        if (dialogue.Count > 0)
        {
            md.Add("## Full Transcript\n");
            foreach (var d in dialogue) md.Add($"**[{d.Timestamp}] {d.Speaker}**: {d.Text}\n");
        }
        return string.Join("\n", md).Trim();
    }
}
