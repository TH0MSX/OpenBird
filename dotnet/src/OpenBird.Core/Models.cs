using System.Text.Json.Serialization;

namespace OpenBird.Core;

public sealed record DialogueTurn(
    [property: JsonPropertyName("start_sec")] double StartSec,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("speaker")] string Speaker,
    [property: JsonPropertyName("channel")] int Channel,
    [property: JsonPropertyName("text")] string Text);

public sealed record ScreenFrame(
    [property: JsonPropertyName("timestamp")] double Timestamp,
    [property: JsonPropertyName("time_str")] string TimeStr,
    [property: JsonPropertyName("window_title")] string WindowTitle,
    [property: JsonPropertyName("image_path")] string? ImagePath,
    [property: JsonPropertyName("ocr_text")] string OcrText);

public sealed class ActionItem
{
    [JsonPropertyName("id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long Id { get; set; }
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("task")] public string Task { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [JsonIgnore] public string? MeetingId { get; set; }
    [JsonIgnore] public string? MeetingTitle { get; set; }
    [JsonIgnore] public string? CreatedAt { get; set; }
    [JsonIgnore] public bool IsCompleted => Status == "completed";
}

public sealed class Topic
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
}

/// <summary>Meeting notes. The JSON shape matches what the Python app stores in meetings.summary_json.</summary>
public sealed class MeetingSummary
{
    [JsonPropertyName("title")] public string Title { get; set; } = "Meeting";
    [JsonPropertyName("template")] public string Template { get; set; } = "executive";
    [JsonPropertyName("tldr")] public string Tldr { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("for_you")] public List<string> ForYou { get; set; } = new();
    [JsonPropertyName("action_items")] public List<ActionItem> ActionItems { get; set; } = new();
    [JsonPropertyName("decisions")] public List<string> Decisions { get; set; } = new();
    [JsonPropertyName("topics")] public List<Topic> Topics { get; set; } = new();
}

public sealed class MeetingRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public double DurationSec { get; set; }
    public string? WavPath { get; set; }
    public string? MarkdownPath { get; set; }
    public string Tags { get; set; } = "";
    public MeetingSummary Summary { get; set; } = new();
    public List<DialogueTurn> Transcript { get; set; } = new();
    public List<ActionItem> ActionItems { get; set; } = new();
}

public sealed record SearchHit(string Kind, string Title, string When, string Snippet, string? MeetingId, string? Path, double Rank);
