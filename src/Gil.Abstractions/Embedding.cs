namespace Gil;

/// <summary>Vectors for a batch of texts, in input order, and what the provider reported.</summary>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, string Model, int PromptTokens, double LatencyMs, string RawResponse);

/// <summary>A text-embedding backend.</summary>
public interface IEmbeddingModel
{
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
