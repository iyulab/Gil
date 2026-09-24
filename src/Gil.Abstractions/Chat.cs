using System.Text.Json.Nodes;

namespace Gil;

/// <summary>One chat message.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>A completion request, provider-neutral.</summary>
public sealed record ChatRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public required int MaxTokens { get; init; }
    public double Temperature { get; init; }

    /// <summary>
    /// How many alternatives to return at the first generated token; null asks for none. Single-token judgments
    /// need them — the answer is the distribution, not the token.
    /// </summary>
    public int? TopLogprobs { get; init; }

    /// <summary>Provider-specific request fields merged into the body (e.g. a server's chat-template switches).</summary>
    public JsonObject? ExtraBody { get; init; }
}

/// <summary>What a provider returned, before it is attributed to a request and priced.</summary>
public sealed record ChatResult
{
    public required string Model { get; init; }
    public required string Content { get; init; }
    public required int PromptTokens { get; init; }

    /// <summary>Prompt tokens served from the provider's cache; 0 when the provider does not report it.</summary>
    public required int CachedTokens { get; init; }

    public required int CompletionTokens { get; init; }
    public required double LatencyMs { get; init; }
    public string? FirstToken { get; init; }
    public IReadOnlyList<TokenLogprob> TopLogprobs { get; init; } = [];

    /// <summary>Self-hosted servers only: reported processing time of the prompt and of generation.</summary>
    public double? GpuPromptMs { get; init; }

    public double? GpuPredictedMs { get; init; }

    /// <summary>The response body as received.</summary>
    public required string RawResponse { get; init; }
}

/// <summary>A chat-completion backend.</summary>
public interface IChatModel
{
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
