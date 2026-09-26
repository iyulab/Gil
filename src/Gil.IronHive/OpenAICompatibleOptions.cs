using System.Text.Json.Nodes;

namespace Gil.Llm;

/// <summary>Connection and retry settings for an OpenAI-compatible endpoint (chat completions or embeddings).</summary>
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

    /// <summary>Per attempt; longer than a busy shared server's queueing time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long to wait for a connection to open.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Provider-specific fields merged into every request body sent to this endpoint — for example a server's
    /// chat-template switches. Set once here, every caller of the model sends them; a request's own
    /// <see cref="ChatRequest.ExtraBody"/> replaces a field of the same name. Fields a server does not know may be
    /// rejected, so set only what this endpoint accepts.
    /// </summary>
    public JsonObject? ExtraBody { get; init; }
}
