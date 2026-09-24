using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gil.Llm;

/// <summary>Connection and retry settings for an OpenAI-compatible chat-completions endpoint.</summary>
public sealed record OpenAICompatibleOptions
{
    /// <summary>The server root, without <c>/v1</c>.</summary>
    public required Uri BaseUrl { get; init; }

    public required string ApiKey { get; init; }
    public required string Model { get; init; }

    /// <summary>
    /// Attempts for failures the server did not process: refused connections, 429 and 5xx. A request that timed out
    /// may already be queued on a busy shared server, so it is retried only <see cref="ReadTimeoutRetries"/> times —
    /// resending eagerly would queue the same work twice and deepen the backlog.
    /// </summary>
    public int MaxAttempts { get; init; } = 4;

    public int ReadTimeoutRetries { get; init; } = 1;
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Longer than a busy shared server's queueing time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Chat completions over the OpenAI-compatible HTTP API (OpenAI, and self-hosted servers such as llama.cpp and vLLM),
/// reading what judgments and cost accounting need: first-token log-probabilities, cached prompt tokens and, when the
/// server reports them, its processing times.
/// </summary>
/// <remarks>
/// A thin transport of its own because general-purpose client abstractions (including IronHive's) do not yet expose
/// token log-probabilities or server timings. When they do, an adapter over that abstraction should replace this.
/// </remarks>
// TODO(upstream): replace with an adapter over IronHive once its abstractions expose token log-probabilities and
// server timings; remove this transport then.
public sealed class OpenAICompatibleChatModel : IChatModel
{
    private readonly OpenAICompatibleHttp _http;

    public OpenAICompatibleChatModel(HttpClient http, OpenAICompatibleOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = new OpenAICompatibleHttp(http, options, delay);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (text, latencyMs) = await _http.PostAsync("v1/chat/completions", Body(request), cancellationToken).ConfigureAwait(false);
        return Parse(text, latencyMs);
    }

    private string Body(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = _http.Options.Model,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => new JsonObject { ["role"] = m.Role, ["content"] = m.Content })]),
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
        };
        if (request.TopLogprobs is int top)
        {
            body["logprobs"] = true;
            body["top_logprobs"] = top;
        }

        foreach (var (key, value) in request.ExtraBody ?? [])
        {
            body[key] = value?.DeepClone();
        }

        return body.ToJsonString();
    }

    private ChatResult Parse(string text, double latencyMs)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var choice = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 ? choices[0] : default;
        var usage = root.TryGetProperty("usage", out var u) ? u : default;
        var timings = root.TryGetProperty("timings", out var t) ? t : default;

        string? firstToken = null;
        var top = new List<TokenLogprob>();
        if (choice.ValueKind == JsonValueKind.Object
            && choice.TryGetProperty("logprobs", out var logprobs) && logprobs.ValueKind == JsonValueKind.Object
            && logprobs.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            && content.GetArrayLength() > 0)
        {
            var head = content[0];
            firstToken = head.GetProperty("token").GetString();
            if (head.TryGetProperty("top_logprobs", out var alternatives))
            {
                top.AddRange(alternatives.EnumerateArray().Select(a => new TokenLogprob(a.GetProperty("token").GetString()!, a.GetProperty("logprob").GetDouble())));
            }
        }

        return new ChatResult
        {
            Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? _http.Options.Model : _http.Options.Model,
            Content = choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var body) && body.ValueKind == JsonValueKind.String
                ? body.GetString()!
                : "",
            PromptTokens = Int(usage, "prompt_tokens"),
            CachedTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens_details", out var details)
                ? Int(details, "cached_tokens")
                : 0,
            CompletionTokens = Int(usage, "completion_tokens"),
            LatencyMs = latencyMs,
            FirstToken = firstToken,
            TopLogprobs = top,
            GpuPromptMs = Number(timings, "prompt_ms"),
            GpuPredictedMs = Number(timings, "predicted_ms"),
            RawResponse = text,
        };
    }

    private static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
