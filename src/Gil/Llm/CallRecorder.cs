namespace Gil.Llm;

/// <summary>
/// The single path every model call takes: send, price, attribute to its request, record. Keeping it single is what
/// guarantees no call escapes the telemetry — a component that talks to the model directly would leave costs
/// and distributions out of the record.
/// </summary>
public sealed class CallRecorder(IChatModel model, EnergyModel energy, ITelemetrySink? sink = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<CallRecord> CompleteAsync(
        ChatRequest request,
        string role,
        string traceId,
        string? nodeId = null,
        int? layer = null,
        Func<CallRecord, CallRecord>? annotate = null,
        CancellationToken cancellationToken = default)
    {
        var createdAt = _clock.GetUtcNow();
        using var activity = GilDiagnostics.StartCall(role, traceId, nodeId, layer);
        ChatResult result;
        try
        {
            result = await model.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            GilDiagnostics.Failed(activity, error);
            throw;
        }

        var record = new CallRecord
        {
            CallId = Guid.NewGuid().ToString("N"),
            TraceId = traceId,
            CreatedAt = createdAt,
            Role = role,
            Model = result.Model,
            NodeId = nodeId,
            Layer = layer,
            PromptTokens = result.PromptTokens,
            CachedTokens = result.CachedTokens,
            CompletionTokens = result.CompletionTokens,
            LatencyMs = result.LatencyMs,
            GpuPromptMs = result.GpuPromptMs,
            GpuPredictedMs = result.GpuPredictedMs,
            Content = result.Content,
            FirstToken = result.FirstToken,
            TopLogprobs = result.TopLogprobs,
            Energy = energy.Of(result.PromptTokens, result.CachedTokens, result.CompletionTokens),
            RawResponse = result.RawResponse,
        };
        // A judge adds what it read from the response (distribution, candidates as shown) before the row is written:
        // one row per call, never updated afterwards.
        if (annotate is not null)
        {
            record = annotate(record);
        }

        sink?.RecordCall(record);
        GilDiagnostics.Called(activity, record);
        return record;
    }
}
