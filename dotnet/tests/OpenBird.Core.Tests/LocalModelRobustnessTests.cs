using System.Net;
using System.Text;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

/// <summary>Behaviour found on a real Foundry Local server: aliases, rejected JSON option, reasoning text, long meetings.</summary>
public class LocalModelRobustnessTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };
    private static string Chat(string content) => System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
    private const string Catalog = """{"data":[{"id":"qwen2.5-1.5b-instruct-generic-gpu:4"},{"id":"qwen2.5-1.5b-instruct-qnn-npu:3"},{"id":"qwen2.5-1.5b-instruct-generic-cpu:4"},{"id":"phi-4"}]}""";

    [Fact]
    public async Task A_short_alias_the_server_rejects_resolves_to_the_npu_build()
    {
        var handler = new FakeHandler((req, body) =>
            req.RequestUri!.AbsolutePath.EndsWith("/v1/models") ? Json(Catalog)
            : body.Contains("\"model\":\"qwen2.5-1.5b-instruct-qnn-npu:3\"") ? Json(Chat("hello"))
            : Status(HttpStatusCode.InternalServerError, "unknown model"));
        var model = new LocalChatModel(new HttpClient(handler), "http://127.0.0.1:39839", "qwen2.5-1.5b");

        Assert.Equal("hello", await model.CompleteAsync("hi", false, default));
        Assert.Contains("qnn-npu:3", model.Name);                       // remembered, so later calls go straight to it
        var before = handler.Calls.Count;
        await model.CompleteAsync("again", false, default);
        Assert.Equal(before + 1, handler.Calls.Count);
    }

    [Fact]
    public async Task A_rejected_json_option_is_retried_without_it()
    {
        var handler = new FakeHandler((_, body) => body.Contains("response_format") ? Status(HttpStatusCode.BadRequest, "no") : Json(Chat("{\"terms\":[]}")));
        var model = new LocalChatModel(new HttpClient(handler), "http://localhost:1234", "m");

        Assert.Equal("{\"terms\":[]}", await model.CompleteAsync("x", true, default));
        Assert.Equal(2, handler.Calls.Count);
        Assert.DoesNotContain("response_format", handler.Calls[1].Body);
    }

    [Theory]
    [InlineData("<think>let me think</think>The answer.", "The answer.")]
    [InlineData("<think>a\nb\nc</think>\n\n{\"x\":1}", "{\"x\":1}")]
    [InlineData("No tags here", "No tags here")]
    public void Reasoning_text_is_removed(string raw, string expected) => Assert.Equal(expected, LocalChatModel.StripReasoning(raw));

    [Fact]
    public void Matching_ids_prefer_the_npu_build()
    {
        var ids = new[] { "qwen2.5-1.5b-instruct-generic-gpu:4", "qwen2.5-1.5b-instruct-qnn-npu:3", "qwen2.5-1.5b-instruct-generic-cpu:4", "phi-4" };
        Assert.Equal(new[] { "qwen2.5-1.5b-instruct-qnn-npu:3", "qwen2.5-1.5b-instruct-generic-cpu:4", "qwen2.5-1.5b-instruct-generic-gpu:4" },
            LocalChatModel.MatchIds(ids, "qwen2.5-1.5b"));
    }

    [Fact]
    public async Task The_connection_test_sends_a_real_message_and_names_the_fix_when_it_fails()
    {
        var failing = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/v1/models") ? Json(Catalog) : Status(HttpStatusCode.InternalServerError, "not loaded"));
        var bad = await new LocalChatModel(new HttpClient(failing), "http://127.0.0.1:1", "qwen2.5-1.5b").TestAsync(default);
        Assert.False(bad.Ok);
        Assert.Contains("did not answer", bad.Message);
        Assert.Contains("qwen2.5-1.5b-instruct-qnn-npu:3", bad.Message);

        var working = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/v1/models") ? Json(Catalog) : Json(Chat("OK")));
        var good = await new LocalChatModel(new HttpClient(working), "http://127.0.0.1:1", "qwen2.5-1.5b-instruct-qnn-npu:3").TestAsync(default);
        Assert.True(good.Ok);
        Assert.StartsWith("Working.", good.Message);
        Assert.Contains(working.Calls, c => c.Path.EndsWith("/v1/chat/completions"));
    }

    [Fact]
    public async Task Ask_says_so_when_the_model_does_not_answer()
    {
        using var tmp = new TempDir();
        var db = new Database(tmp.File("o.db"), tmp.File("notes"));
        var d = new List<DialogueTurn> { new(1, "00:01", "Tester", 0, "The audit is moved to Friday.") };
        db.SaveMeeting("m1", "Audit call", 30, null, LocalSummarizer.Summarize("Audit call", d, null, "executive", "Tester"), d, null, "x");
        var model = new LocalChatModel(new HttpClient(new FakeHandler((_, _) => Status(HttpStatusCode.InternalServerError, "boom"))), "http://localhost:1", "m");

        var result = await new AskEngine(db, "Tester").AskAsync("audit Friday", new IChatModel[] { model });

        Assert.NotNull(result.Notice);
        Assert.Contains("did not answer", result.Notice);
        Assert.Contains("audit", result.Answer, StringComparison.OrdinalIgnoreCase);      // the excerpts are still shown
    }

    private sealed class CountingModel(Func<string, string> reply) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string Name => "counting";
        public Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(reply(prompt));
        }
    }

    private static List<DialogueTurn> LongTranscript(int turns, int wordsPerTurn) =>
        Enumerable.Range(0, turns).Select(i => new DialogueTurn(i, $"{i / 60:00}:{i % 60:00}", i % 2 == 0 ? "Anna" : "Ben", i % 2,
            string.Join(' ', Enumerable.Repeat($"point{i}", wordsPerTurn)))).ToList();

    [Fact]
    public void Short_transcripts_stay_in_one_piece_and_long_ones_split_at_turn_boundaries()
    {
        Assert.Single(CloudSummaryProvider.Chunk(LongTranscript(10, 50), 3500, 2500));
        var parts = CloudSummaryProvider.Chunk(LongTranscript(100, 100), 3500, 2500);       // 10,000 words
        Assert.Equal(4, parts.Count);
        Assert.Equal(100, parts.Sum(p => p.Count));                                          // no turn lost or duplicated
        Assert.All(parts, p => Assert.InRange(p.Sum(t => t.Text.Split(' ').Length), 1, 2500));
    }

    [Fact]
    public async Task A_long_meeting_is_summarised_in_parts_then_merged()
    {
        var model = new CountingModel(p => p.Contains("PART NOTES")
            ? """{"tldr":"merged","summary":"whole meeting","action_items":[{"owner":"Anna","task":"Send budget"}]}"""
            : """{"tldr":"part","summary":"s","action_items":[{"owner":"Anna","task":"Send budget"}]}""");
        var summary = await new CloudSummaryProvider(new IChatModel[] { model })
            .SummarizeAsync("Big meeting", LongTranscript(100, 100), Array.Empty<ScreenFrame>(), "executive", "Tester", default);

        Assert.Equal("merged", summary!.Tldr);
        Assert.Equal(5, model.Prompts.Count);                                                // 4 parts + 1 merge
        Assert.Contains("part 1 of 4", model.Prompts[0]);
        Assert.Equal("pending", Assert.Single(summary.ActionItems).Status);
    }

    [Fact]
    public async Task If_the_merge_fails_the_part_notes_are_combined_without_duplicates()
    {
        var model = new CountingModel(p => p.Contains("PART NOTES") ? "not json" : """{"tldr":"t","summary":"s","action_items":[{"owner":"Anna","task":"Send budget"}],"decisions":["Use vendor B"]}""");
        var summary = await new CloudSummaryProvider(new IChatModel[] { model })
            .SummarizeAsync("Big meeting", LongTranscript(100, 100), Array.Empty<ScreenFrame>(), "executive", "Tester", default);

        Assert.Equal("t", summary!.Tldr);
        Assert.Single(summary.ActionItems);
        Assert.Single(summary.Decisions);
    }
}
