using System.Net;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

public class DigestAndExportTests
{
    private static Database Seed(TempDir tmp)
    {
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var d = new List<DialogueTurn>
        {
            new(1, "00:01", "Tester", 0, "I will send the escalation matrix to Anna tomorrow."),
            new(9, "00:09", "Remote Participants", 1, "We need to review the staffing | roster before Friday."),
        };
        var summary = LocalSummarizer.Summarize("Ops sync", d, null, "executive", "Tester");
        db.SaveMeeting("m1", "Ops sync", 754, null, summary, d, null, MarkdownRenderer.Render(summary, d, null, "Tester"), "Client-Acme");
        return db;
    }

    [Fact]
    public void Digest_covers_the_local_day_and_fills_every_section()
    {
        using var tmp = new TempDir();
        var db = Seed(tmp);
        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd");
        foreach (var title in new[] { "Audit.xlsx - Excel", "Audit.xlsx - Excel", "Inbox - Outlook" })
            db.AddScreenFrame(new ScreenFrame(1, $"{day} 09:00:00", title, null, "x"));
        db.AddScreenFrame(new ScreenFrame(2, "2001-01-01 09:00:00", "Old day", null, "x"));
        var done = db.GetActions().First(a => a.Owner == "Remote Participants");
        db.SetActionCompleted(done.Id, true);

        var result = new DigestGenerator(db, "Tester").Generate(now: now);

        Assert.Equal((1, 2, 1, 1, 3), (result.Meetings, result.Actions, result.Pending, result.Completed, result.ScreenFrames));
        var md = File.ReadAllText(result.MarkdownPath);
        Assert.Contains($"# OpenBird Daily Digest: {day}", md);
        Assert.Contains("- **Meetings**: 1 (12.6 minutes)", md);
        Assert.Contains("| **Ops sync** | Client-Acme | 12.6 min | Discussion covering", md);   // Focus column is filled
        Assert.Contains("- [ ] **Tester**: I will send the escalation matrix to Anna tomorrow. *(from Ops sync)*", md);
        Assert.Contains("- [x] **Remote Participants**: We need to review the staffing | roster before Friday.", md);
        Assert.Contains("- **Audit.xlsx**: 2 captures (66.7%)", md);
        Assert.Contains("- **Inbox**: 1 capture (33.3%)", md);
        Assert.DoesNotContain("Old day", md);

        // An empty day still produces a valid digest.
        var empty = new DigestGenerator(db, "Tester").Generate(new DateOnly(2001, 2, 3), now, writeDocx: false);
        Assert.Equal(0, empty.Meetings + empty.Actions + empty.ScreenFrames);
        Assert.Contains("*No meetings recorded on this day.*", empty.Markdown);
    }

    [Fact]
    public void Word_export_is_a_valid_document_with_the_digest_content()
    {
        using var tmp = new TempDir();
        var db = Seed(tmp);
        var result = new DigestGenerator(db, "Tester").Generate();

        using var doc = WordprocessingDocument.Open(result.DocxPath!, false);
        var errors = new OpenXmlValidator().Validate(doc).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var body = doc.MainDocumentPart!.Document!.Body!;
        var text = string.Join("\n", body.Descendants<Paragraph>().Select(p => p.InnerText));
        Assert.Contains("OpenBird Daily Digest", text);
        Assert.Contains("☐ Tester: I will send the escalation matrix", text);      // check item, bold markers removed
        Assert.DoesNotContain("**", text);
        var table = body.Descendants<Table>().Single();
        Assert.Equal(2, table.Elements<TableRow>().Count());                          // header + one meeting, rule row dropped
        Assert.Equal(5, table.Elements<TableRow>().First().Elements<TableCell>().Count());
    }

    [Fact]
    public void Word_export_keeps_escaped_pipes_inside_a_table_cell()
    {
        using var tmp = new TempDir();
        var path = WordExporter.Export("| A | B |\n| :--- | :--- |\n| left \\| still left | right |\n", tmp.File("t.docx"));
        using var doc = WordprocessingDocument.Open(path, false);
        var cells = doc.MainDocumentPart!.Document!.Body!.Descendants<TableRow>().Last().Elements<TableCell>().Select(c => c.InnerText).ToList();
        Assert.Equal(new[] { "left | still left", "right" }, cells);
    }

    [Fact]
    public void Tasks_file_lists_open_items()
    {
        using var tmp = new TempDir();
        var db = Seed(tmp);
        var path = TasksExporter.Export(db.GetActions(pendingOnly: true), tmp.File("notes", "tasks.md"), "Tester");
        var text = File.ReadAllText(path);
        Assert.Contains("- [ ] **I will send the escalation matrix to Anna tomorrow.** (Owner: `Tester` • From: *Ops sync*", text);
        Assert.Contains("*No open action items.*", File.ReadAllText(TasksExporter.Export(Array.Empty<ActionItem>(), tmp.File("empty.md"), "Tester")));
    }
}

public class CloudTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string GeminiReply(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    private static string GroqReply(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } });

    private static readonly List<DialogueTurn> Dialogue = new() { new(1, "00:01", "Tester", 0, "We agreed to move the audit to Monday.") };

    [Fact]
    public void Local_only_mode_creates_no_models_even_with_keys_present()
    {
        var settings = new AppSettings { CloudAiEnabled = false, GeminiApiKey = "g-key", GroqApiKey = "q-key" };
        Assert.Empty(CloudModels.Create(settings, new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no network in local-only mode")))));
        settings.CloudAiEnabled = true;
        Assert.Equal(2, CloudModels.Create(settings, new HttpClient()).Count);
        settings.GroqApiKey = " ";
        Assert.Single(CloudModels.Create(settings, new HttpClient()));
    }

    [Fact]
    public async Task Gemini_summary_is_parsed_and_the_key_is_sent_in_a_header_not_the_url()
    {
        var handler = new FakeHandler((_, _) => Json(GeminiReply("""
            ```json
            {"tldr": "Audit moves to Monday.", "summary": "Short sync.", "for_you": ["Tell Anna"],
             "action_items": [{"owner": "Tester", "task": "Rebook the audit"}], "decisions": ["Audit on Monday"], "topics": []}
            ```
            """)));
        var settings = new AppSettings { CloudAiEnabled = true, GeminiApiKey = "g-key" };
        var provider = new CloudSummaryProvider(CloudModels.Create(settings, new HttpClient(handler)));

        var summary = await provider.SummarizeAsync("Audit sync", Dialogue, Array.Empty<ScreenFrame>(), "executive", "Tester", default);

        Assert.Equal("Audit moves to Monday.", summary!.Tldr);
        Assert.Equal("Audit sync", summary.Title);
        Assert.Equal("pending", summary.ActionItems.Single().Status);
        var (request, body) = handler.Calls.Single();
        Assert.DoesNotContain("g-key", request.RequestUri!.ToString());
        Assert.Equal("g-key", request.Headers.GetValues("x-goog-api-key").Single());
        Assert.Contains("gemini-2.5-flash:generateContent", request.RequestUri.ToString());
        Assert.Contains("We agreed to move the audit to Monday.", body);
        Assert.Contains("response_mime_type", body);
    }

    [Fact]
    public async Task Falls_through_to_the_next_model_and_the_pipeline_falls_back_offline_when_all_fail()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.Host.Contains("googleapis")
            ? Json("{\"error\":\"down\"}", HttpStatusCode.InternalServerError)
            : Json(GroqReply("{\"tldr\": \"From Groq\", \"summary\": \"ok\"}")));
        var settings = new AppSettings { CloudAiEnabled = true, GeminiApiKey = "g", GroqApiKey = "q", UserName = "Tester" };
        var provider = new CloudSummaryProvider(CloudModels.Create(settings, new HttpClient(handler)));
        var summary = await provider.SummarizeAsync("m", Dialogue, Array.Empty<ScreenFrame>(), "executive", "Tester", default);
        Assert.Equal("From Groq", summary!.Tldr);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("Bearer q", handler.Calls[1].Request.Headers.GetValues("Authorization").Single());

        // Every provider failing must not lose the meeting.
        using var tmp = new TempDir();
        settings.DbPath = tmp.File("o.db");
        settings.StorageDir = tmp.File("notes");
        var broken = new CloudSummaryProvider(CloudModels.Create(settings, new HttpClient(new FakeHandler((_, _) => Json("not json at all")))));
        var db = new Database(settings.DbPath, settings.StorageDir);
        var result = await new MeetingPipeline(db, settings, provider: broken).ProcessAsync(new FinishedRecording("m9", "Fallback", "", 0, 5, null));
        Assert.Contains("The AI summary failed", result!.Warning);
        Assert.StartsWith("Brief audio recording", result.Summary.Tldr);
    }

    [Fact]
    public async Task Ask_uses_a_model_only_when_allowed_and_keeps_the_sources()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var summary = LocalSummarizer.Summarize("Audit sync", Dialogue, null, "executive", "Tester");
        db.SaveMeeting("m1", "Audit sync", 20, null, summary, Dialogue, null, "x");
        var ask = new AskEngine(db, "Tester");

        var handler = new FakeHandler((_, _) => Json(GroqReply("The audit moved to Monday [1].")));
        var models = CloudModels.Create(new AppSettings { CloudAiEnabled = true, GroqApiKey = "q" }, new HttpClient(handler));
        var cloud = await ask.AskAsync("audit monday", models);
        Assert.Equal("The audit moved to Monday [1].", cloud.Answer);
        Assert.NotEmpty(cloud.Sources);
        Assert.Equal(2, handler.Calls.Count);                       // one call for related search words, one for the answer
        Assert.Contains("[1] meeting | Audit sync", handler.Calls[1].Body.Replace("\\u0022", "\"").Replace("\\n", "\n"));

        var local = await ask.AskAsync("audit monday", Array.Empty<IChatModel>());
        Assert.StartsWith("### Results for", local.Answer);

        var nothing = await ask.AskAsync("zeppelin", models);       // nothing found: no answer is requested
        Assert.Contains("No matches", nothing.Answer);
        Assert.Equal(3, handler.Calls.Count);                       // only the related-words call was added
    }
}
