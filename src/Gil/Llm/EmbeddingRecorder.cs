namespace Gil.Llm;

/// <summary>
/// Sends embedding requests and records each as a call with role <c>embed</c>. It takes its own energy model: an
/// embedding model's cost per token is not the chat model's, and pricing both alike would overstate lookups.
/// </summary>
public sealed class EmbeddingRecorder(IEmbeddingModel model, EnergyModel energy, ITelemetrySink? sink = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<(IReadOnlyList<float[]> Vectors, CallRecord Call)> EmbedAsync(IReadOnlyList<string> texts, string traceId, CancellationToken cancellationToken = default)
    {
        var createdAt = _clock.GetUtcNow();
        var result = await model.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
        var call = new CallRecord
        {
            CallId = Guid.NewGuid().ToString("N"),
            TraceId = traceId,
            CreatedAt = createdAt,
            Role = "embed",
            Model = result.Model,
            PromptTokens = result.PromptTokens,
            CachedTokens = 0,
            CompletionTokens = 0,
            LatencyMs = result.LatencyMs,
            Energy = energy.Of(result.PromptTokens, 0, 0),
            RawResponse = result.RawResponse,
        };
        sink?.RecordCall(call);
        return (result.Vectors, call);
    }
}
