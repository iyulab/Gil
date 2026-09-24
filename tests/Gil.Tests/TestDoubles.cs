namespace Gil.Tests;

/// <summary>An in-memory telemetry sink for tests.</summary>
internal sealed class ListSink : ITelemetrySink
{
    public List<CallRecord> Calls { get; } = [];

    public Dictionary<string, (string Task, string State, TraceOutcome? Outcome, string? Verdict, string? Correction)> Traces { get; } = [];

    public void OpenTrace(string traceId, string task, string state, string? label = null) =>
        Traces.TryAdd(traceId, (task, state, null, null, null));

    public void CloseTrace(string traceId, TraceOutcome outcome) =>
        Traces[traceId] = Traces[traceId] with { Outcome = outcome };

    public void RecordCall(CallRecord record) => Calls.Add(record);

    public string RecordRunConfig(string task, string configJson) => configJson;

    public void RecordFeedback(string traceId, string verdict, string? correction) =>
        Traces[traceId] = Traces[traceId] with { Verdict = verdict, Correction = correction };

    public TraceSummary? FindTrace(string traceId) =>
        Traces.TryGetValue(traceId, out var t) && t.Outcome is not null
            ? new TraceSummary(t.Task, t.State, t.Outcome.Mode, t.Outcome.Output, t.Outcome.Recall)
            : null;
}
