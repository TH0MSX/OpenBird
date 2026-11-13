namespace OpenBird.Core;

public sealed record ChatTurn(string Role, string Text)
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";
}

/// <summary>
/// A plain conversation with the allowed model. Unlike Ask it does not search the user's
/// meetings or screen history: what is typed here is all the model sees. The whole
/// conversation travels as one prompt, so it works with every provider.
/// </summary>
public sealed class ChatSession(string userName)
{
    /// <summary>Older turns are dropped from the prompt beyond this many characters, so small models keep up.</summary>
    public const int MaxHistoryChars = 6000;

    private readonly List<ChatTurn> _turns = new();
    public IReadOnlyList<ChatTurn> Turns => _turns;

    public void Clear() => _turns.Clear();

    public static string BuildPrompt(string userName, IReadOnlyList<ChatTurn> history, string message, DateTime now)
    {
        var kept = new List<string>();
        var used = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var line = $"{(history[i].Role == ChatTurn.UserRole ? "User" : "Assistant")}: {history[i].Text}";
            if (used + line.Length > MaxHistoryChars && kept.Count > 0) break;
            kept.Add(line);
            used += line.Length;
        }
        kept.Reverse();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are OpenBird, a helpful assistant running on {userName}'s own PC. Today is {now:dddd, d MMMM yyyy}.");
        sb.AppendLine("Answer clearly and concisely. If you do not know something, say so instead of guessing.");
        if (kept.Count > 0) sb.AppendLine().AppendLine("Conversation so far:").AppendLine(string.Join("\n", kept));
        sb.AppendLine().AppendLine($"User: {message}").Append("Assistant:");
        return sb.ToString();
    }

    /// <summary>
    /// Sends a message and returns the reply. Both turns are kept only when a model answered,
    /// so a failure can be retried without duplicating the question. Throws the last model error.
    /// </summary>
    public async Task<string> SendAsync(IReadOnlyList<IChatModel> models, string message, CancellationToken ct, Func<DateTime>? clock = null)
    {
        message = message.Trim();
        if (message.Length == 0) throw new ArgumentException("Type a message first.");
        if (models.Count == 0) throw new InvalidOperationException("AI is off. Choose this PC or cloud in Settings to chat.");

        var prompt = BuildPrompt(userName, _turns, message, (clock ?? (() => DateTime.Now))());
        Exception? last = null;
        foreach (var model in models)
        {
            try
            {
                var reply = (await model.CompleteAsync(prompt, jsonOutput: false, ct)).Trim();
                if (reply.Length == 0) { last = new HttpRequestException($"{model.Name} returned an empty reply."); continue; }
                _turns.Add(new ChatTurn(ChatTurn.UserRole, message));
                _turns.Add(new ChatTurn(ChatTurn.AssistantRole, reply));
                return reply;
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                last = new HttpRequestException($"{model.Name}: {e.Message}", e);
            }
        }
        throw last!;
    }
}
