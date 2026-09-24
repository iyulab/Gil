using System.Text.Json;

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

/// <summary>
/// Replays judgments another implementation recorded, per node: <c>{node: {probs, confidence, trusted, choice}}</c>.
/// An untrusted judgment has no choice, whatever was recorded.
/// </summary>
internal sealed class RecordedJudge(JsonElement byNode) : IJudge
{
    public Task<Judgment> JudgeAsync(string state, IReadOnlyList<Candidate> candidates, string traceId, string? nodeId = null, int? layer = null, CancellationToken cancellationToken = default)
    {
        var r = byNode.GetProperty(nodeId!);
        var probs = r.GetProperty("probs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var trusted = r.GetProperty("trusted").GetBoolean();
        return Task.FromResult(new Judgment
        {
            Probs = probs,
            Choice = trusted ? r.GetProperty("choice").GetString() : null,
            Confidence = r.GetProperty("confidence").GetDouble(),
            NoneProb = 1 - probs.Values.Sum(),
            LabelMass = 0.99,
            Trusted = trusted,
            Call = new CallRecord
            {
                CallId = Guid.NewGuid().ToString("N"),
                TraceId = traceId,
                CreatedAt = DateTimeOffset.UnixEpoch,
                Role = "judge",
                Model = "replay",
                NodeId = nodeId,
                Layer = layer,
                PromptTokens = 0,
                CachedTokens = 0,
                CompletionTokens = 0,
                LatencyMs = 0,
                Energy = 1,
            },
        });
    }
}

/// <summary>Returns the given outputs in order and fails if asked for more — a replay must use exactly what was recorded.</summary>
internal sealed class RecordedChatModel(IReadOnlyList<string> outputs) : IChatModel
{
    public int Used { get; private set; }

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (Used >= outputs.Count)
        {
            throw new InvalidOperationException($"asked for output {Used + 1} of {outputs.Count} recorded");
        }

        return Task.FromResult(new ChatResult
        {
            Model = "replay",
            Content = outputs[Used++],
            PromptTokens = 1,
            CachedTokens = 0,
            CompletionTokens = 1,
            LatencyMs = 1,
            RawResponse = "{}",
        });
    }
}
