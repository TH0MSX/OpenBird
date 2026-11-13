using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OpenBird.Core;

/// <summary>A text model reachable over the network. Only created when the user has switched cloud AI on.</summary>
public interface IChatModel
{
    string Name { get; }
    Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct);
}

public sealed class GeminiChatModel(HttpClient http, string apiKey, string model) : IChatModel
{
    public string Name => $"Gemini ({model})";

    public async Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
    {
        var config = new Dictionary<string, object> { ["temperature"] = 0.2 };
        if (jsonOutput) config["response_mime_type"] = "application/json";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
        // The key travels in a header, never in the URL (URLs end up in logs and error messages).
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = JsonContent.Create(new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = config,
        });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return CloudJson.Read(() => doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
    }
}

public sealed class GroqChatModel(HttpClient http, string apiKey, string model) : IChatModel
{
    public string Name => $"Groq ({model})";

    public async Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["temperature"] = 0.2,
        };
        if (jsonOutput) body["response_format"] = new { type = "json_object" };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return CloudJson.Read(() => doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }
}

internal static class CloudJson
{
    /// <summary>Any unexpected response shape becomes a JsonException, which callers treat as "this provider failed".</summary>
    public static string Read(Func<string?> read)
    {
        try { return read() ?? throw new JsonException("The model returned no text."); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new JsonException("The model response had an unexpected shape.", e);
        }
    }

    public static IReadOnlyList<string> ReadList(Func<List<string>> read)
    {
        try { return read(); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException)
        {
            throw new JsonException("The model list had an unexpected shape.", e);
        }
    }

    /// <summary>Models sometimes wrap JSON in a Markdown code fence.</summary>
    public static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstBreak = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstBreak >= 0 && lastFence > firstBreak ? t[(firstBreak + 1)..lastFence].Trim() : t;
    }
}

/// <summary>
/// A model server running on this PC that speaks the OpenAI chat API: Ollama,
/// LM Studio or a llama.cpp server. Only loopback addresses are accepted, so
/// "local" can never send text to another machine.
/// </summary>
public sealed record LocalTestResult(bool Ok, string Message);

public sealed class LocalChatModel : IChatModel
{
    /// <summary>How long one request may take before it counts as "no answer". Models on a NPU can hang rather than fail.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(180);

    private readonly HttpClient _http;
    private readonly Uri _base;
    private string _model;

    public LocalChatModel(HttpClient http, string baseUrl, string model)
    {
        _http = http;
        _base = ParseLoopback(baseUrl);
        _model = model.Trim();
    }

    public string Name => $"Local ({(_model.Length > 0 ? _model : "first available model")})";

    /// <summary>Throws when the address is not on this PC.</summary>
    public static Uri ParseLoopback(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{baseUrl}' is not a valid address. Example: http://localhost:11434");
        if (!uri.IsLoopback)
            throw new ArgumentException($"'{uri.Host}' is not this PC. Local mode only talks to localhost; use Cloud mode for other machines.");
        return uri;
    }

    /// <summary>Names of the models the local server offers. Throws HttpRequestException when it is not running.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(_base, "v1/models"), ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return CloudJson.ReadList(() => doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "").Where(id => id.Length > 0).ToList());
    }

    /// <summary>Removes the "thinking" text some models put in front of the answer.</summary>
    public static string StripReasoning(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"<think>.*?</think>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim();
    }

    /// <summary>
    /// Servers such as Foundry Local list one model under several ids ("qwen2.5-1.5b" is really
    /// "qwen2.5-1.5b-instruct-qnn-npu:3"). Ids that contain the typed name, NPU builds first.
    /// </summary>
    public static IReadOnlyList<string> MatchIds(IEnumerable<string> ids, string typed)
    {
        if (typed.Length == 0) return Array.Empty<string>();
        static int Rank(string id) => id.Contains("npu", StringComparison.OrdinalIgnoreCase) ? 0
            : id.Contains("cpu", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        return ids.Where(id => !id.Equals(typed, StringComparison.OrdinalIgnoreCase) && id.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Rank).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> CompleteAsync(string prompt, bool jsonOutput, CancellationToken ct)
    {
        if (_model.Length == 0)
        {
            var models = await ListModelsAsync(ct);
            if (models.Count == 0) throw new HttpRequestException("The local model server is running but has no models installed.");
            _model = models[0];
        }

        HttpRequestException? rejected = null;
        try { return await PostAsync(_model, prompt, jsonOutput, ct); }
        catch (HttpRequestException e) when (e.StatusCode is not null && !ct.IsCancellationRequested) { rejected = e; }

        // The server answered but refused this model name. Try the other ids that contain it.
        IReadOnlyList<string> alternatives = Array.Empty<string>();
        try { alternatives = MatchIds(await ListModelsAsync(ct), _model).Take(3).ToList(); }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
        foreach (var id in alternatives)
        {
            try
            {
                var text = await PostAsync(id, prompt, jsonOutput, ct);
                _model = id;
                return text;
            }
            catch (HttpRequestException e) when (e.StatusCode is not null && !ct.IsCancellationRequested) { rejected = e; }
        }
        throw rejected!;
    }

    /// <summary>
    /// A real chat round trip, which is what proves the model works: listing models only proves
    /// the server is running. Never throws; the message says what to do next.
    /// </summary>
    public async Task<LocalTestResult> TestAsync(CancellationToken ct)
    {
        IReadOnlyList<string> ids = Array.Empty<string>();
        try { ids = await ListModelsAsync(ct); }
        catch (HttpRequestException e) when (e.StatusCode is null)
        {
            return new LocalTestResult(false, "No model server answered at that address. Start it (for Foundry Local: foundry server start) and test again.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException) { /* servers without a model list can still chat */ }

        var started = DateTime.UtcNow;
        try
        {
            var reply = await CompleteAsync("Reply with the single word OK.", jsonOutput: false, ct);
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            return new LocalTestResult(reply.Length > 0,
                reply.Length > 0
                    ? $"Working. {_model} answered in {seconds:0.0}s."
                    : "The server answered with an empty reply. Try another model.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            var hint = ids.Count > 0 && _model.Length > 0
                ? MatchIds(ids, _model).Take(3).ToList() is { Count: > 0 } m ? $" Names on the server that match: {string.Join(", ", m)}." : ""
                : "";
            return new LocalTestResult(false, $"The server is running but the model '{_model}' did not answer: {e.Message}{hint} Make sure the model is loaded (Foundry: foundry model load <name>).");
        }
    }

    private async Task<string> PostAsync(string model, string prompt, bool jsonOutput, CancellationToken ct)
    {
        try
        {
            if (jsonOutput)
            {
                // Some servers reject the JSON option; the prompt already asks for JSON, so retry without it.
                try { return await SendOnceAsync(model, prompt, jsonMode: true, ct); }
                catch (HttpRequestException e) when (e.StatusCode is not null && !ct.IsCancellationRequested) { }
            }
            return await SendOnceAsync(model, prompt, jsonMode: false, ct);
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConnectionError && !ct.IsCancellationRequested)
        {
            // Connection refused: the server may still be starting (it launches at sign-in). Wait once and retry.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return await SendOnceAsync(model, prompt, jsonMode: false, ct);
        }
    }

    private async Task<string> SendOnceAsync(string model, string prompt, bool jsonMode, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["temperature"] = 0.2,
            ["stream"] = false,
        };
        if (jsonMode) body["response_format"] = new { type = "json_object" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "v1/chat/completions")) { Content = JsonContent.Create(body) };
            using var response = await _http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = text.Length > 200 ? text[..200] : text;
                throw new HttpRequestException($"the server answered {(int)response.StatusCode}{(detail.Length > 0 ? $": {detail.Trim()}" : "")}", null, response.StatusCode);
            }
            using var doc = JsonDocument.Parse(text);
            return StripReasoning(CloudJson.Read(() => doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"no answer within {(int)RequestTimeout.TotalSeconds} seconds");
        }
    }
}

/// <summary>Chooses the models for the mode the user set: none, a local server, or cloud providers.</summary>
public static class AiModels
{
    public static IReadOnlyList<IChatModel> Create(AppSettings settings, HttpClient http)
    {
        var models = new List<IChatModel>();
        switch (settings.EffectiveAiMode)
        {
            case "local":
                try { models.Add(new LocalChatModel(http, settings.LocalAiUrl, settings.LocalAiModel)); }
                catch (ArgumentException) { }       // a bad or non-local address means no model, never a remote call
                break;
            case "cloud":
                if (!string.IsNullOrWhiteSpace(settings.EffectiveGeminiKey)) models.Add(new GeminiChatModel(http, settings.EffectiveGeminiKey, settings.GeminiModel));
                if (!string.IsNullOrWhiteSpace(settings.EffectiveGroqKey)) models.Add(new GroqChatModel(http, settings.EffectiveGroqKey, settings.GroqModel));
                break;
        }
        return models;
    }
}

public static class CloudModels
{
    /// <summary>Cloud providers only. Empty unless the mode is "cloud" and a key is set.</summary>
    public static IReadOnlyList<IChatModel> Create(AppSettings settings, HttpClient http) =>
        settings.EffectiveAiMode == "cloud" ? AiModels.Create(settings, http) : Array.Empty<IChatModel>();
}

/// <summary>Meeting notes written by a language model (local or cloud), trying each allowed model in order.</summary>
public sealed class CloudSummaryProvider(IReadOnlyList<IChatModel> models) : ISummaryProvider
{
    /// <summary>Transcripts longer than this many words are summarised in parts, then merged. Small local models lose the middle of long prompts.</summary>
    public const int ChunkTriggerWords = 3500;
    public const int ChunkWords = 2500;

    public async Task<MeetingSummary?> SummarizeAsync(string meetingName, IReadOnlyList<DialogueTurn> dialogue,
        IReadOnlyList<ScreenFrame> screenContext, string template, string userName, CancellationToken ct)
    {
        if (models.Count == 0) return null;
        var chunks = Chunk(dialogue, ChunkTriggerWords, ChunkWords);
        if (chunks.Count <= 1)
            return Finish(await RunAsync(BuildPrompt(meetingName, dialogue, screenContext, template, userName), ct), meetingName, template);

        var noScreens = Array.Empty<ScreenFrame>();
        var parts = new List<MeetingSummary>();
        for (var i = 0; i < chunks.Count; i++)
            parts.Add(await RunAsync(BuildPrompt($"{meetingName} (part {i + 1} of {chunks.Count})", chunks[i], noScreens, template, userName), ct));

        try { return Finish(await RunAsync(BuildMergePrompt(meetingName, parts, template, userName), ct), meetingName, template); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Finish(Combine(parts), meetingName, template);     // the merge is a nicety; the parts already hold the facts
        }
    }

    /// <summary>Splits a transcript at turn boundaries into parts of about <paramref name="size"/> words; short transcripts stay whole.</summary>
    public static List<IReadOnlyList<DialogueTurn>> Chunk(IReadOnlyList<DialogueTurn> dialogue, int triggerWords, int size)
    {
        static int Words(DialogueTurn d) => d.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var result = new List<IReadOnlyList<DialogueTurn>>();
        if (dialogue.Sum(Words) <= triggerWords) { result.Add(dialogue); return result; }
        var current = new List<DialogueTurn>();
        var count = 0;
        foreach (var turn in dialogue)
        {
            var w = Words(turn);
            if (count > 0 && count + w > size) { result.Add(current); current = new(); count = 0; }
            current.Add(turn);
            count += w;
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private async Task<MeetingSummary> RunAsync(string prompt, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var model in models)
        {
            try
            {
                var json = CloudJson.StripFence(await model.CompleteAsync(prompt, jsonOutput: true, ct));
                return JsonSerializer.Deserialize<MeetingSummary>(json) ?? throw new JsonException("Empty summary.");
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                last = e;
            }
        }
        throw last!;
    }

    private static MeetingSummary Finish(MeetingSummary summary, string meetingName, string template)
    {
        summary.Title = meetingName;
        summary.Template = template;
        summary.ForYou ??= new();
        summary.ActionItems ??= new();
        summary.Decisions ??= new();
        summary.Topics ??= new();
        foreach (var a in summary.ActionItems) if (string.IsNullOrWhiteSpace(a.Status)) a.Status = "pending";
        return summary;
    }

    private static MeetingSummary Combine(IReadOnlyList<MeetingSummary> parts) => new()
    {
        Tldr = parts.Select(p => p.Tldr).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "",
        Summary = string.Join(" ", parts.Select(p => p.Summary).Where(t => !string.IsNullOrWhiteSpace(t))),
        ForYou = parts.SelectMany(p => p.ForYou ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        ActionItems = parts.SelectMany(p => p.ActionItems ?? new()).GroupBy(a => a.Task, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList(),
        Decisions = parts.SelectMany(p => p.Decisions ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Topics = parts.SelectMany(p => p.Topics ?? new()).ToList(),
    };

    public static string BuildMergePrompt(string meetingName, IReadOnlyList<MeetingSummary> parts, string template, string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are OpenBird, a meeting-notes assistant. Below are notes for consecutive parts of one meeting for {userName}. Merge them into one set of notes for the whole meeting.");
        sb.AppendLine("Keep every action item and decision. Remove duplicates. Do not invent anything.");
        sb.AppendLine().AppendLine($"Meeting title: {meetingName}").AppendLine($"Template: {template}").AppendLine().AppendLine("PART NOTES:");
        for (var i = 0; i < parts.Count; i++)
            sb.AppendLine($"--- Part {i + 1} ---").AppendLine(JsonSerializer.Serialize(parts[i]));
        sb.AppendLine().AppendLine("Return ONLY valid JSON with exactly these fields:");
        sb.AppendLine("""
            {
              "tldr": "1-2 sentence punchline",
              "summary": "Concise paragraph of what happened",
              "for_you": ["Items relevant to USER or assigned to them"],
              "action_items": [{"owner": "Person name", "task": "Specific action", "status": "pending"}],
              "decisions": ["Explicit decisions that were agreed"],
              "topics": [{"title": "Topic name", "summary": "Key points"}]
            }
            """.Replace("USER", userName));
        return sb.ToString();
    }

    public static string BuildPrompt(string meetingName, IReadOnlyList<DialogueTurn> dialogue,
        IReadOnlyList<ScreenFrame> screenContext, string template, string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are OpenBird, a meeting-notes assistant. Analyse this meeting transcript and on-screen context for {userName}.");
        sb.AppendLine(template switch
        {
            "actions_only" => "Focus strictly on actionable commitments, task owners, due dates and concrete decisions.",
            "technical" => "Focus on technical architecture, systems, code, data and engineering specifications.",
            _ => "Provide a balanced, high-signal executive brief.",
        });
        sb.AppendLine().AppendLine($"Meeting title: {meetingName}").AppendLine($"Template: {template}").AppendLine().AppendLine("TRANSCRIPT:");
        foreach (var d in dialogue) sb.AppendLine($"[{d.Timestamp}] {d.Speaker}: {d.Text}");

        var screens = screenContext.Where(s => !string.IsNullOrEmpty(s.OcrText)).ToList();
        if (screens.Count > 0)
        {
            sb.AppendLine().AppendLine("SCREEN CONTEXT CAPTURED DURING THE CALL:");
            foreach (var s in screens)
                sb.AppendLine($"[{s.TimeStr}] App: {s.WindowTitle}").AppendLine($"Text: {(s.OcrText.Length > 300 ? s.OcrText[..300] : s.OcrText)}").AppendLine("---");
        }

        sb.AppendLine().AppendLine("Return ONLY valid JSON with exactly these fields:");
        sb.AppendLine("""
            {
              "tldr": "1-2 sentence punchline",
              "summary": "Concise paragraph of what happened",
              "for_you": ["Items relevant to USER or assigned to them"],
              "action_items": [{"owner": "Person name", "task": "Specific action", "status": "pending"}],
              "decisions": ["Explicit decisions that were agreed"],
              "topics": [{"title": "Topic name", "summary": "Key points"}]
            }
            """.Replace("USER", userName));
        sb.AppendLine("Do not invent facts that are not in the transcript or screen context.");
        return sb.ToString();
    }
}
