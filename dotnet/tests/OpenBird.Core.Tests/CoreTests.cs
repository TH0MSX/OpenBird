using System.Text.Json;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openbird_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Fixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);
}

public class SettingsTests
{
    [Fact]
    public void First_run_creates_file_with_local_only_defaults_under_app_data()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.File("appdata"), tmp.File("docs"));
        var s = store.Load();
        Assert.True(File.Exists(store.ConfigPath));
        Assert.False(s.CloudAiEnabled);
        Assert.StartsWith(tmp.File("appdata"), s.DbPath);
        Assert.StartsWith(tmp.File("docs"), s.StorageDir);
        Assert.NotEmpty(s.UserName);
    }

    [Fact]
    public void Reads_the_python_settings_file_and_keeps_unknown_keys()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.File("appdata"), tmp.File("docs"));
        Directory.CreateDirectory(store.AppDataDir);
        File.WriteAllText(store.ConfigPath, """
            { "app_name": "OpenBird", "version": "2.5.0", "user_name": "Alex", "db_path": "", "storage_dir": "D:\\Notes",
              "hotkey": "ctrl+alt+r", "cloud_ai_enabled": false, "groq_api_key": "secret", "capture_exclusions": ["payroll"] }
            """);
        var s = store.Load();
        Assert.Equal("Alex", s.UserName);
        Assert.Equal("D:\\Notes", s.StorageDir);
        Assert.EndsWith("openbird.db", s.DbPath);            // empty path falls back to the default
        Assert.Equal(new[] { "payroll" }, s.CaptureExclusions);
        Assert.Equal("", s.EffectiveGroqKey);                 // key ignored while local only
        s.CloudAiEnabled = true;
        Assert.Equal("secret", s.EffectiveGroqKey);

        store.Save(s);
        using var doc = JsonDocument.Parse(File.ReadAllText(store.ConfigPath));
        Assert.Equal("2.5.0", doc.RootElement.GetProperty("version").GetString());   // unknown key preserved
    }

    [Fact]
    public void Unreadable_file_is_kept_aside_and_defaults_are_used()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.File("appdata"), tmp.File("docs"));
        Directory.CreateDirectory(store.AppDataDir);
        File.WriteAllText(store.ConfigPath, "{ not json");
        var s = store.Load();
        Assert.False(s.CloudAiEnabled);
        Assert.True(File.Exists(store.ConfigPath + ".unreadable"));
    }
}

public class PrivacyAndHotkeyTests
{
    [Theory]
    [InlineData("Vault - 1Password", "1Password.exe", true)]
    [InlineData("anything", "KeePassXC.exe", true)]
    [InlineData("New tab - [InPrivate] - Microsoft Edge", "msedge.exe", true)]
    [InlineData("Reset your password - Contoso", "chrome.exe", true)]
    [InlineData("Q3 audit.xlsx - Excel", "EXCEL.EXE", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void Default_exclusions(string? title, string? process, bool expected) =>
        Assert.Equal(expected, Privacy.IsExcluded(title, process));

    [Fact]
    public void Custom_patterns_are_used() =>
        Assert.True(Privacy.IsExcluded("Payroll - HR Portal", "msedge.exe", new[] { " Payroll " }));

    [Fact]
    public void Hotkey_parses()
    {
        Assert.Equal((Hotkey.ModWin | Hotkey.ModAlt, (uint)0x52), Hotkey.Parse("win+alt+r"));
        Assert.Equal((Hotkey.ModCtrl | Hotkey.ModShift, (uint)0x78), Hotkey.Parse("Ctrl+Shift+F9"));
    }

    [Theory]
    [InlineData("r")]
    [InlineData("win+alt")]
    [InlineData("ctrl+a+b")]
    [InlineData("ctrl+foo")]
    [InlineData("")]
    public void Hotkey_rejects_invalid(string spec) => Assert.Throws<FormatException>(() => Hotkey.Parse(spec));
}

public class SummarizerParityTests
{
    public sealed class Case
    {
        public string name { get; set; } = "";
        public string user { get; set; } = "";
        public string template { get; set; } = "";
        public List<DialogueTurn> dialogue { get; set; } = new();
        public List<ScreenFrame> screen { get; set; } = new();
        public JsonElement expected_summary { get; set; }
        public string expected_markdown { get; set; } = "";
    }

    public static IEnumerable<object[]> Cases() =>
        JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Fixtures.PathOf("summarizer_parity.json")))!
            .Select(c => new object[] { c.name });

    private static Case Load(string name) =>
        JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(Fixtures.PathOf("summarizer_parity.json")))!.Single(c => c.name == name);

    /// <summary>The C# summary and Markdown must equal what Python v2.5 produced for the same input.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_python_output(string name)
    {
        var c = Load(name);
        var summary = LocalSummarizer.Summarize($"Case {c.name}", c.dialogue, c.screen, c.template, c.user);

        var expected = c.expected_summary;
        Assert.Equal(expected.GetProperty("tldr").GetString(), summary.Tldr);
        Assert.Equal(expected.GetProperty("summary").GetString(), summary.Summary);
        Assert.Equal(expected.GetProperty("for_you").EnumerateArray().Select(e => e.GetString()), summary.ForYou);
        Assert.Equal(expected.GetProperty("action_items").EnumerateArray().Select(e => (e.GetProperty("owner").GetString(), e.GetProperty("task").GetString())),
            summary.ActionItems.Select(a => ((string?)a.Owner, (string?)a.Task)));
        Assert.Equal(expected.GetProperty("topics").EnumerateArray().Select(e => e.GetProperty("summary").GetString()), summary.Topics.Select(t => t.Summary));

        Assert.Equal(c.expected_markdown, MarkdownRenderer.Render(summary, c.dialogue, c.screen, c.user));
    }

    [Fact]
    public void Filler_prefix_needs_a_word_boundary()
    {
        var d = new[] { new DialogueTurn(0, "00:00", "A", 0, "Sometimes we will check the queue twice.") };
        var s = LocalSummarizer.Summarize("x", d, null, "executive", "A");
        Assert.Equal("Sometimes we will check the queue twice.", s.ActionItems[0].Task);   // not "metimes ..."
    }
}

public class DatabaseTests
{
    private static (MeetingSummary, List<DialogueTurn>) Sample()
    {
        var d = new List<DialogueTurn>
        {
            new(1, "00:01", "Tester", 0, "I will send the escalation matrix to Anna tomorrow."),
            new(9, "00:09", "Remote Participants", 1, "Dispatch procedures change on Monday for the Lisbon hub."),
        };
        return (LocalSummarizer.Summarize("Ops sync", d, null, "executive", "Tester"), d);
    }

    [Fact]
    public void Saving_twice_replaces_rows_instead_of_duplicating()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("db", "o.db"), tmp.File("notes"));
        var (summary, dialogue) = Sample();
        var md = MarkdownRenderer.Render(summary, dialogue, null, "Tester");
        db.SaveMeeting("m1", "Ops sync", 30, null, summary, dialogue, null, md, "Client-Acme, 1on1");
        var path = db.SaveMeeting("m1", "Ops sync (renamed)", 30, null, summary, dialogue, null, md, "Client-Acme");

        var m = db.GetMeeting("m1")!;
        Assert.Equal("Ops sync (renamed)", m.Title);
        Assert.Equal(2, m.Transcript.Count);
        Assert.Single(m.ActionItems);
        Assert.Equal(md, File.ReadAllText(path));
        Assert.Single(db.ListMeetings());
        Assert.Single(db.SearchTranscripts("escalation"));     // index not doubled either
        Assert.Equal(new[] { "Client-Acme" }, db.GetAllTags());
    }

    [Fact]
    public void Full_text_search_finds_words_prefixes_and_accents()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var (summary, dialogue) = Sample();
        db.SaveMeeting("m1", "Ops sync", 30, null, summary, dialogue, null, "x");
        db.AddScreenFrame(new ScreenFrame(1000, "2026-11-13 10:00:00", "Coaching plan - Word", "a.png", "Session with Jordan about NPS recovery"));

        Assert.Single(db.SearchTranscripts("dispatch monday"));           // all words
        Assert.Single(db.SearchTranscripts("escal"));                     // prefix
        Assert.Single(db.SearchTranscripts("dispatch unicorn"));          // falls back to any word
        Assert.Empty(db.SearchTranscripts("unicorn"));
        Assert.Empty(db.SearchTranscripts("\"*) OR ("));                  // hostile input is harmless
        var screen = db.SearchScreen("jordan");                           // accent-insensitive
        Assert.Single(screen);
        Assert.Equal("Coaching plan - Word", screen[0].Title);
        Assert.Contains("NPS", screen[0].Snippet);
    }

    [Fact]
    public void Completing_an_action_updates_database_and_note_file()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var (summary, dialogue) = Sample();
        var path = db.SaveMeeting("m1", "Ops sync", 30, null, summary, dialogue, null, MarkdownRenderer.Render(summary, dialogue, null, "Tester"));
        var action = db.GetActions(pendingOnly: true).Single();

        db.SetActionCompleted(action.Id, true);
        Assert.Empty(db.GetActions(pendingOnly: true));
        Assert.Contains("- [x] **Tester**: I will send the escalation matrix", File.ReadAllText(path));

        db.SetActionCompleted(action.Id, false);
        Assert.Contains("- [ ] **Tester**: I will send the escalation matrix", File.ReadAllText(path));
    }

    /// <summary>A database written by the Python app opens unchanged and its old rows become searchable.</summary>
    [Fact]
    public void Opens_a_python_database_and_indexes_existing_rows()
    {
        using var tmp = new TempDir();
        var dbPath = tmp.File("legacy.db");
        File.Copy(Fixtures.PathOf("legacy_v24.db"), dbPath);
        var db = new Database(dbPath, tmp.File("notes"));

        var meeting = db.ListMeetings().Single();
        Assert.Equal("Acme weekly sync", meeting.Title);
        Assert.Equal("Client-Acme", meeting.Tags);
        var full = db.GetMeeting(meeting.Id)!;
        Assert.Equal(2, full.Transcript.Count);
        Assert.NotEmpty(full.Summary.Tldr);
        Assert.Single(db.SearchTranscripts("release rate"));
        Assert.Single(db.SearchScreen("coaching jordan"));
        Assert.Single(db.GetActions(pendingOnly: true));

        // Re-opening must not rebuild or duplicate anything.
        var again = new Database(dbPath, tmp.File("notes"));
        Assert.Single(again.SearchTranscripts("release rate"));
    }
}

public class AskAndPipelineTests
{
    private sealed class FakeTranscriber : ITranscriber
    {
        public int Calls;
        public IReadOnlyList<DialogueTurn> Transcribe(string wavPath, string hostSpeaker, string remoteSpeaker)
        {
            Calls++;
            return new[]
            {
                new DialogueTurn(2, "00:02", hostSpeaker, 0, "I will review the audit findings and send feedback."),
                new DialogueTurn(11, "00:11", remoteSpeaker, 1, "The coaching backlog is cleared."),
            };
        }
    }

    private sealed class MissingModel : ITranscriber
    {
        public IReadOnlyList<DialogueTurn> Transcribe(string wavPath, string hostSpeaker, string remoteSpeaker) =>
            throw new FileNotFoundException("base.en-encoder.int8.onnx");
    }

    private static (Database, AppSettings, string) Setup(TempDir tmp)
    {
        var settings = new SettingsStore(tmp.File("appdata"), tmp.File("docs")).Load();
        settings.UserName = "Tester";
        var wav = tmp.File("rec.wav");
        File.WriteAllBytes(wav, new byte[64]);
        return (new Database(settings.DbPath, settings.StorageDir), settings, wav);
    }

    [Fact]
    public async Task Pipeline_processes_each_meeting_once_and_ask_finds_it()
    {
        using var tmp = new TempDir();
        var (db, settings, wav) = Setup(tmp);
        db.AddScreenFrame(new ScreenFrame(1005, "10:00:05", "Audit tracker - Excel", "a.png", "Findings: 3 open, 2 closed"));
        db.AddScreenFrame(new ScreenFrame(5000, "11:06:40", "Unrelated", "b.png", "later"));
        var transcriber = new FakeTranscriber();
        var pipeline = new MeetingPipeline(db, settings, transcriber);
        var rec = new FinishedRecording("mtg_1", "Audit review", "Client-Acme", 1000, 30, wav);

        var first = await pipeline.ProcessAsync(rec);
        var second = await pipeline.ProcessAsync(rec);      // the Python app transcribed and saved again here

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, transcriber.Calls);
        Assert.Null(first!.Warning);
        var note = File.ReadAllText(first.MarkdownPath);
        Assert.Contains("# Meeting: Audit review", note);
        Assert.Contains("Audit tracker - Excel", note);     // only the frame inside the meeting window
        Assert.DoesNotContain("Unrelated", note);

        var ask = new AskEngine(db, "Tester").Ask("audit findings");
        Assert.True(ask.MeetingMatches >= 1 && ask.ScreenMatches == 1 && ask.ActionMatches == 1);
        Assert.Contains("Audit review", ask.Answer);
        Assert.Contains("No matches", new AskEngine(db, "Tester").Ask("zeppelin").Answer);
    }

    [Fact]
    public async Task Missing_speech_model_still_saves_notes_with_a_warning()
    {
        using var tmp = new TempDir();
        var (db, settings, wav) = Setup(tmp);
        var result = await new MeetingPipeline(db, settings, new MissingModel()).ProcessAsync(new FinishedRecording("mtg_2", "No model", "", 0, 20, wav));
        Assert.NotNull(result);
        Assert.Contains("Speech model not found", result!.Warning);
        Assert.Equal(0, result.Turns);
        Assert.NotNull(db.GetMeeting("mtg_2"));
    }
}

public class AskFollowUpTests
{
    [Theory]
    [InlineData("and what about Monday?", true)]
    [InlineData("who said that?", true)]
    [InlineData("why?", true)]
    [InlineData("what did Anna say about the escalation matrix", false)]
    [InlineData("audit findings", false)]
    public void Detects_follow_up_questions(string question, bool expected) => Assert.Equal(expected, AskEngine.IsFollowUp(question));

    [Fact]
    public void A_follow_up_is_searched_together_with_the_previous_question()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var dialogue = new List<DialogueTurn>
        {
            new(1, "00:01", "Tester", 0, "The audit for the Lisbon hub is moved to Monday."),
            new(9, "00:09", "Remote", 1, "On Monday we also have the quarterly lunch."),
        };
        db.SaveMeeting("m1", "Planning", 20, null, LocalSummarizer.Summarize("Planning", dialogue, null, "executive", "Tester"), dialogue, null, "x");
        var ask = new AskEngine(db, "Tester");

        var alone = ask.Ask("and what about Monday?");
        var followUp = ask.Ask("and what about Monday?", previousQuestion: "When is the Lisbon audit?");

        Assert.Equal(2, alone.MeetingMatches);                               // on its own: every mention of Monday
        Assert.Equal(1, followUp.MeetingMatches);                            // with context: only the audit line
        Assert.Contains("audit", followUp.Sources[0].Snippet);
        Assert.Equal("and what about Monday?", followUp.Question);           // the question shown is the one asked
        Assert.Contains("\"and what about Monday?\"", followUp.Answer);
        Assert.Equal(alone.MeetingMatches, ask.Ask("and what about Monday?", previousQuestion: "  ").MeetingMatches);
    }
}
