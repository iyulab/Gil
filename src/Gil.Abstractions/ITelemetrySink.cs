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
}
