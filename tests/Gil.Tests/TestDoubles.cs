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
            ? new TraceSummary(t.Task, t.State, t.Outcome.Mode, t.Outcome.Output, t.Outcome.Recall) { Path = t.Outcome.Path }
            : null;
}

/// <summary>Records what the resolver writes to the habit statistics, in order.</summary>
internal sealed class RecordingStatistics : IHabitStatistics
{
    public List<(string Scope, IReadOnlyList<PathStep> Path)> Paths { get; } = [];

    public List<(string Scope, string Item, HabitCounts Delta)> Outcomes { get; } = [];

    public void RecordPath(string scope, IReadOnlyList<PathStep> path) => Paths.Add((scope, path));

    public void RecordOutcome(string scope, string itemId, HabitCounts delta) => Outcomes.Add((scope, itemId, delta));

    public NodeVisits Visits(string scope, string nodeId) => throw new NotSupportedException();

    public IReadOnlyDictionary<string, int> Choices(string scope, string nodeId) => throw new NotSupportedException();

    public HabitCounts Reliability(string scope, string itemId) => throw new NotSupportedException();
}
