using System.Net;
using OpenBird.Core;
using Xunit;

namespace OpenBird.Core.Tests;

public class ChatSessionTests
{
    private sealed class Model(Func<string, string> reply) : IChatModel
    {
        public List<string> Prompts { get; } = new();
        public string Name => "scripted";
        public Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(reply(prompt));
        }
    }

    private sealed class Broken : IChatModel
    {
        public string Name => "broken";
        public Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct) => throw new HttpRequestException("down", null, HttpStatusCode.InternalServerError);
    }

    private static readonly Func<DateTime> Clock = () => new DateTime(2026, 11, 13);

    [Fact]
    public async Task The_conversation_travels_with_each_message_in_order()
    {
        var model = new Model(p => p.Contains("Second") ? "Answer two" : "Answer one");
        var chat = new ChatSession("Alex");

        Assert.Equal("Answer one", await chat.SendAsync(new[] { model }, "First question", default, Clock));
        Assert.Equal("Answer two", await chat.SendAsync(new[] { model }, "Second question", default, Clock));

        var second = model.Prompts[1];
        Assert.True(second.IndexOf("User: First question") < second.IndexOf("Assistant: Answer one"));
        Assert.True(second.IndexOf("Assistant: Answer one") < second.IndexOf("User: Second question"));
        Assert.Contains("Friday, 13 November 2026", second);
        Assert.Contains("Alex", second);
        Assert.Equal(4, chat.Turns.Count);
        Assert.False(model.Prompts[0].Contains("Conversation so far"));
    }

    [Fact]
    public async Task A_failed_message_is_not_kept_so_it_can_be_sent_again()
    {
        var chat = new ChatSession("T");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => chat.SendAsync(new IChatModel[] { new Broken() }, "hello", default, Clock));
        Assert.Contains("broken", error.Message);
        Assert.Empty(chat.Turns);
    }

    [Fact]
    public async Task The_next_model_is_tried_when_the_first_fails()
    {
        var chat = new ChatSession("T");
        var reply = await chat.SendAsync(new IChatModel[] { new Broken(), new Model(_ => "from the second") }, "hello", default, Clock);
        Assert.Equal("from the second", reply);
    }

    [Fact]
    public async Task Old_turns_are_dropped_to_keep_the_prompt_small_but_the_newest_stays()
    {
        var model = new Model(_ => new string('x', 1500));
        var chat = new ChatSession("T");
        for (var i = 0; i < 8; i++) await chat.SendAsync(new[] { model }, $"question {i}", default, Clock);

        var last = model.Prompts[^1];
        Assert.InRange(last.Length, 1, ChatSession.MaxHistoryChars + 1500);
        Assert.DoesNotContain("question 0", last);
        Assert.Contains("question 7", last);
    }

    [Fact]
    public async Task Empty_input_and_no_model_are_explained_and_new_chat_clears()
    {
        var chat = new ChatSession("T");
        await Assert.ThrowsAsync<ArgumentException>(() => chat.SendAsync(new[] { new Model(_ => "x") }, "   ", default, Clock));
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendAsync(Array.Empty<IChatModel>(), "hi", default, Clock));
        Assert.Contains("Settings", off.Message);

        await chat.SendAsync(new[] { new Model(_ => "ok") }, "hi", default, Clock);
        chat.Clear();
        Assert.Empty(chat.Turns);
    }
}
