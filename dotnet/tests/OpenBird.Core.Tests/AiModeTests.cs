using System.Net;
using System.Text;
using System.Text.Json;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

public class AiModeTests
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

    private sealed class ScriptedModel(params string[] replies) : IChatModel
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Prompts { get; } = new();
        public string Name => "scripted";
        public Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : "{}");
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Chat(string text) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } });

    [Fact]
    public void Mode_falls_back_to_the_older_cloud_flag_and_keeps_it_in_step()
    {
        var s = new AppSettings { GroqApiKey = "k" };
        Assert.Equal("off", s.EffectiveAiMode);
        s.CloudAiEnabled = true;                       // a settings file from before ai_mode existed
        Assert.Equal("cloud", s.EffectiveAiMode);
        s.SetAiMode("local");
        Assert.Equal(("local", false, ""), (s.EffectiveAiMode, s.CloudAiEnabled, s.EffectiveGroqKey));   // keys unusable outside cloud mode
        s.SetAiMode("nonsense");
        Assert.Equal("off", s.EffectiveAiMode);
    }

    [Fact]
    public void Each_mode_creates_only_its_own_models()
    {
        var http = new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no call expected")));
        var s = new AppSettings { GeminiApiKey = "g", GroqApiKey = "q" };
        Assert.Empty(AiModels.Create(s, http));
        s.SetAiMode("local");
        Assert.IsType<LocalChatModel>(Assert.Single(AiModels.Create(s, http)));
        Assert.Empty(CloudModels.Create(s, http));
        s.SetAiMode("cloud");
        Assert.Equal(2, AiModels.Create(s, http).Count);
        Assert.DoesNotContain(AiModels.Create(s, http), m => m is LocalChatModel);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:1234/", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://192.168.1.20:11434", false)]       // another machine on the network
    [InlineData("https://api.example.com", false)]
    [InlineData("localhost:11434", false)]                 // not a full address
    [InlineData("", false)]
    public void Local_mode_only_accepts_addresses_on_this_pc(string url, bool accepted)
    {
        var s = new AppSettings { LocalAiUrl = url };
        s.SetAiMode("local");
        Assert.Equal(accepted ? 1 : 0, AiModels.Create(s, new HttpClient()).Count);
        if (!accepted) Assert.Throws<ArgumentException>(() => LocalChatModel.ParseLoopback(url));
    }

    [Fact]
    public async Task Local_model_picks_the_first_installed_model_and_talks_to_localhost_only()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/v1/models")
            ? Json("""{"data": [{"id": "llama3.2:3b"}, {"id": "phi4-mini"}]}""")
            : Json(Chat("""{"tldr": "Local summary", "summary": "ok"}""")));
        var s = new AppSettings { UserName = "Tester" };
        s.SetAiMode("local");
        var provider = new CloudSummaryProvider(AiModels.Create(s, new HttpClient(handler)));

        var summary = await provider.SummarizeAsync("m", new[] { new DialogueTurn(1, "00:01", "Tester", 0, "Hello there.") }, Array.Empty<ScreenFrame>(), "executive", "Tester", default);

        Assert.Equal("Local summary", summary!.Tldr);
        Assert.All(handler.Calls, c => Assert.True(c.Request.RequestUri!.IsLoopback));
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Calls[1].Request.RequestUri!.ToString());
        Assert.Contains("\"model\":\"llama3.2:3b\"", handler.Calls[1].Body);
        Assert.Contains("\"stream\":false", handler.Calls[1].Body);
    }

    [Fact]
    public async Task A_stopped_local_server_falls_back_to_the_built_in_summary()
    {
        using var tmp = new TempDir();
        var s = new SettingsStore(tmp.File("appdata"), tmp.File("docs")).Load();
        s.SetAiMode("local");
        var down = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("connection refused")));
        var db = new Database(s.DbPath, s.StorageDir);
        var result = await new MeetingPipeline(db, s, provider: new CloudSummaryProvider(AiModels.Create(s, down)))
            .ProcessAsync(new FinishedRecording("m1", "No server", "", 0, 5, null));
        Assert.Contains("The AI summary failed", result!.Warning);
        Assert.NotNull(db.GetMeeting("m1"));
    }

    private static Database SeedMeeting(TempDir tmp)
    {
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var d = new List<DialogueTurn>
        {
            new(1, "00:01", "Tester", 0, "I will send the invoice summary to Anna on Friday."),
            new(9, "00:09", "Remote", 1, "The supplier wants written confirmation of the new delivery window."),
        };
        db.SaveMeeting("m1", "Supplier call", 30, null, LocalSummarizer.Summarize("Supplier call", d, null, "executive", "Tester"), d, null, "x");
        return db;
    }

    [Fact]
    public async Task Related_terms_from_a_model_find_notes_that_use_other_words()
    {
        using var tmp = new TempDir();
        var db = SeedMeeting(tmp);
        var ask = new AskEngine(db, "Tester");

        var plain = ask.Ask("billing statement");
        Assert.Equal(0, plain.MeetingMatches);                                               // the note says "invoice", not "billing"

        var model = new ScriptedModel("""{"terms": ["invoice", "receipt", "billing statement"]}""", "The invoice summary goes to Anna on Friday [1].");
        var smart = await ask.AskAsync("billing statement", new IChatModel[] { model });
        Assert.Equal(1, smart.MeetingMatches);
        Assert.Contains("invoice", smart.Sources[0].Snippet);
        Assert.Equal("The invoice summary goes to Anna on Friday [1].", smart.Answer);
        Assert.Equal(2, model.Prompts.Count);                                                // one call for terms, one for the answer

        // A model that answers with rubbish must not break search.
        var broken = await ask.AskAsync("invoice", new IChatModel[] { new ScriptedModel("not json", "Answer.") });
        Assert.Equal(1, broken.MeetingMatches);
    }

    [Fact]
    public async Task Action_cards_are_created_deduplicated_accepted_and_dismissed()
    {
        using var tmp = new TempDir();
        var db = SeedMeeting(tmp);
        var model = new ScriptedModel("""
            {"cards": [
              {"title": "Confirm the delivery window in writing", "detail": "The supplier asked for written confirmation."},
              {"title": "send the invoice summary to Anna on Friday", "detail": "Already an action item."},
              {"title": "x"},
              {"detail": "no title"},
              {"title": "Check the Friday deadline with Anna", "detail": ""},
              {"title": "A fourth card that is over the limit"}
            ]}
            """);

        var cards = await ActionCardGenerator.GenerateAsync(db, new IChatModel[] { model }, "m1", "Tester", default);

        // Only the first three entries are considered; of those, one duplicates an action item and one is too short.
        Assert.Equal(new[] { "Confirm the delivery window in writing" }, cards.Select(c => c.Title));
        Assert.Equal("Supplier call", cards[0].MeetingTitle);
        Assert.Contains("already in the action items", model.Prompts[0]);
        Assert.Contains("- Tester: I will send the invoice summary to Anna on Friday.", model.Prompts[0]);

        db.AcceptCard(cards[0].Id, "Tester");
        Assert.Empty(db.GetCards());
        Assert.Contains(db.GetActions(pendingOnly: true), a => a.Task == "Confirm the delivery window in writing: The supplier asked for written confirmation." && a.MeetingTitle == "Supplier call");
        db.AcceptCard(cards[0].Id, "Tester");                                                // accepting twice adds nothing
        Assert.Equal(2, db.GetActions().Count);

        var id = db.AddCard("m1", "Another idea", "");
        db.DismissCard(id);
        Assert.Empty(db.GetCards());
        Assert.Single(db.GetCards("dismissed"));

        // No model selected, or a meeting without a transcript: nothing happens and nothing is called.
        Assert.Empty(await ActionCardGenerator.GenerateAsync(db, Array.Empty<IChatModel>(), "m1", "Tester", default));
        var silent = new ScriptedModel();
        db.SaveMeeting("m2", "Silent", 5, null, new MeetingSummary(), Array.Empty<DialogueTurn>(), null, "x");
        Assert.Empty(await ActionCardGenerator.GenerateAsync(db, new IChatModel[] { silent }, "m2", "Tester", default));
        Assert.Empty(silent.Prompts);
    }
}

/// <summary>
/// Talks to a real model server on this PC. Needs OPENBIRD_TEST_LOCAL_AI_URL (for example
/// http://localhost:11434 with Ollama running); passes without checking anything otherwise.
/// </summary>
public class RealLocalModelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Summary_related_terms_and_cards_work_against_a_real_local_model()
    {
        var url = Environment.GetEnvironmentVariable("OPENBIRD_TEST_LOCAL_AI_URL");
        if (string.IsNullOrEmpty(url)) { output.WriteLine("SKIPPED: OPENBIRD_TEST_LOCAL_AI_URL is not set."); return; }

        using var tmp = new TempDir();
        var s = new SettingsStore(tmp.File("appdata"), tmp.File("docs")).Load();
        s.UserName = "Alex";
        s.LocalAiUrl = url;
        s.SetAiMode("local");
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var models = AiModels.Create(s, http);
        var listed = await ((LocalChatModel)models[0]).ListModelsAsync(default);
        output.WriteLine("models: " + string.Join(", ", listed));
        Assert.NotEmpty(listed);

        var dialogue = new List<DialogueTurn>
        {
            new(2, "00:02", "Alex", 0, "Thanks for joining. The release rate dropped to ninety one percent last week, mostly because of address corrections."),
            new(14, "00:14", "Remote Participants", 1, "We can add a second validation step in the dispatch tool. Engineering needs two weeks for that."),
            new(27, "00:27", "Alex", 0, "Okay, let's do that. I will send the audit findings to Anna by Friday so she can brief the team leads."),
            new(39, "00:39", "Remote Participants", 1, "Agreed. We also decided to move the weekly quality review from Monday to Wednesday."),
        };
        var db = new Database(s.DbPath, s.StorageDir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await new MeetingPipeline(db, s, provider: new CloudSummaryProvider(models))
            .ProcessAsync(new FinishedRecording("m1", "Quality sync", "", 0, 45, null), ct: default);
        // No wav, so the pipeline has no transcript; summarize the dialogue directly as well.
        var summary = await new CloudSummaryProvider(models).SummarizeAsync("Quality sync", dialogue, Array.Empty<ScreenFrame>(), "executive", "Alex", default);
        output.WriteLine($"summary in {clock.Elapsed.TotalSeconds:0.0}s: tldr={summary!.Tldr}");
        foreach (var a in summary.ActionItems) output.WriteLine($"  action: {a.Owner}: {a.Task}");
        foreach (var d in summary.Decisions) output.WriteLine($"  decision: {d}");
        Assert.False(string.IsNullOrWhiteSpace(summary.Tldr));

        db.SaveMeeting("m2", "Quality sync", 45, null, summary, dialogue, null, MarkdownRenderer.Render(summary, dialogue, null, "Alex"));
        clock.Restart();
        var terms = await AskEngine.RelatedTermsAsync("what happened with deliveries getting stuck", models, default);
        output.WriteLine($"related terms in {clock.Elapsed.TotalSeconds:0.0}s: {string.Join(", ", terms)}");

        clock.Restart();
        var cards = await ActionCardGenerator.GenerateAsync(db, models, "m2", "Alex", default);
        output.WriteLine($"cards in {clock.Elapsed.TotalSeconds:0.0}s:");
        foreach (var c in cards) output.WriteLine($"  card: {c.Title} | {c.Detail}");

        clock.Restart();
        var answer = await new AskEngine(db, "Alex").AskAsync("when is the quality review now?", models);
        output.WriteLine($"answer in {clock.Elapsed.TotalSeconds:0.0}s: {answer.Answer}");
        Assert.True(answer.MeetingMatches >= 1);
    }
}
