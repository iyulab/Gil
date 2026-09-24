namespace Gil;

/// <summary>Where requests and model calls are recorded. The stored layout is a contract shared with analysis tooling.</summary>
public interface ITelemetrySink
{
    /// <summary>Opens a request. Opening an existing request leaves it untouched, so a known label can be written first.</summary>
    void OpenTrace(string traceId, string task, string state, string? label = null);

    void CloseTrace(string traceId, TraceOutcome outcome);

    void RecordCall(CallRecord record);

    /// <summary>Stores a run's configuration once and returns the stored one — a resumed run is defined by what it started with.</summary>
    string RecordRunConfig(string task, string configJson);

    /// <summary>Records a verdict on a request's output; <paramref name="correction"/> is the right output when it was wrong.</summary>
    void RecordFeedback(string traceId, string verdict, string? correction);

    /// <summary>A closed request as recorded, or null when it is unknown or still open.</summary>
    TraceSummary? FindTrace(string traceId);
}

/// <summary>What feedback needs to know about a recorded request.</summary>
public sealed record TraceSummary(string Task, string State, string? Mode, string? Output, Recall? Recall)
{
    /// <summary>The traversal path; empty when the request never reached the tree.</summary>
    public IReadOnlyList<PathStep> Path { get; init; } = [];
}
