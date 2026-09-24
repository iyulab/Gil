using Gil.Telemetry;

namespace Gil.Tests.Telemetry;

/// <summary>A small, fully synthetic store that other implementations open to check they read the same layout.</summary>
internal static class CompatibilityFixture
{
    public static readonly TraceOutcome Outcome = new()
    {
        Mode = "partial",
        Output = "배송 조회 안내",
        Confidence = 0.62,
        Energy = 512.5,
        Path =
        [
            new PathStep("root", 1, "cat-배송", 0.93, "accept", new Dictionary<string, double> { ["cat-배송"] = 0.93, ["cat-결제"] = 0.05 }, 210.0),
            new PathStep("cat-배송", 2, null, 0.62, "exit", new Dictionary<string, double> { ["opt-1"] = 0.62 }, 150.0),
        ],
        Recall = new Recall("t0", 0.71, 0.9, Hit: false),
    };

    public static readonly CallRecord Judgment = new()
    {
        CallId = "c1",
        TraceId = "fixture-0001",
        CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Role = "judge",
        Model = "fixture-model",
        NodeId = "root",
        Layer = 1,
        PromptTokens = 200,
        CachedTokens = 0,
        CompletionTokens = 1,
        LatencyMs = 40.0,
        GpuPromptMs = 12.5,
        GpuPredictedMs = 3.0,
        Content = "B",
        FirstToken = "B",
        TopLogprobs = [new TokenLogprob("B", -0.07), new TokenLogprob("A", -3.2)],
        Distribution = new Dictionary<string, double> { ["cat-배송"] = 0.93, ["cat-결제"] = 0.05 },
        LabelMass = 0.99,
        Confidence = 0.93,
        Outcome = "accept",
        Energy = 210.0,
        RawResponse = "{\"model\":\"fixture-model\"}",
        Candidates =
        [
            new ShownCandidate(null, "A", "해당 없음", null, null),
            new ShownCandidate("cat-배송", "B", "배송", "주문한 물건의 도착·지연", null),
            new ShownCandidate("cat-결제", "C", "결제", "결제 수단·영수증", null),
        ],
    };

    public static void Write(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }

        using var store = new SqliteTelemetryStore(path, clock: new FixedClock());
        store.RecordRunConfig("fixture", "{\"tree\":\"fixture\",\"tau\":[0.9,0.75]}");
        store.OpenTrace("fixture-0001", "fixture", "택배가 아직 안 왔어요", label: "배송 조회 안내");
        store.RecordCall(Judgment);
        store.CloseTrace("fixture-0001", Outcome);
        store.RecordPath("fixture", Outcome.Path);
        store.RecordPath("fixture", [new PathStep("root", 1, "cat-결제", 0.9, "accept", new Dictionary<string, double>(), 0)]);
        store.RecordOutcome("fixture", "cat-배송", new HabitCounts(Reinforced: 3, Penalized: 1));
        store.RecordOutcome("fixture", "opt-1", new HabitCounts(Missed: 2, Explored: 1, Disputed: 1));
    }
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 2, 3, 4, 5, 123, 456, TimeSpan.Zero);
}
