using AwesomeAssertions;
using Gil.Llm;
using Gil.Telemetry;

namespace Gil.Tests.Telemetry;

public sealed class TaskStatsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gil-stats-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Accuracy_is_observed_per_mode_with_a_wilson_interval_and_rates_count_every_closed_request()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "s.sqlite"));
        var n = 0;
        void Request(string mode, string? verdict)
        {
            var id = $"t{n++:D3}";
            store.OpenTrace(id, "support", "s");
            store.CloseTrace(id, new TraceOutcome { Mode = mode, Energy = 1 });
            if (verdict is not null)
            {
                store.RecordFeedback(id, verdict, null);
            }
        }

        for (var i = 0; i < 10; i++)
        {
            Request("habit/answer", i < 8 ? "correct" : "wrong");
        }

        Request("habit/template", null);
        Request("memory", "correct");
        Request("memory", null);
        Request("fallback", "wrong");
        Request("fallback", "wrong");
        Request("fallback", "wrong");
        store.OpenTrace("down", "support", "s");
        store.CloseTrace("down", new TraceOutcome { Mode = "partial", Energy = 1, Recall = Recall.Failed(0.9, new TimeoutException("slow")) });
        store.OpenTrace("open", "support", "not closed yet");
        store.OpenTrace("elsewhere", "other", "s");
        store.CloseTrace("elsewhere", new TraceOutcome { Mode = "memory", Energy = 1 });

        var stats = store.Stats("support");

        (stats.Requests, stats.Judged, stats.MemoryFailures).Should().Be((17, 14, 1));
        stats.HabitRate.Should().BeApproximately(11 / 17.0, 1e-12);
        stats.MemoryRate.Should().BeApproximately(2 / 17.0, 1e-12);
        stats.Modes.Select(m => m.Mode).Should().Equal("habit/answer", "fallback", "memory", "habit/template", "partial");
        var habit = stats.Modes[0];
        (habit.Requests, habit.Judged, habit.Correct, habit.Accuracy).Should().Be((10, 10, 8, 0.8));
        habit.Low!.Value.Should().BeApproximately(0.490162, 1e-6);
        habit.High!.Value.Should().BeApproximately(0.943318, 1e-6);
        var fallback = stats.Modes[1];
        fallback.Accuracy.Should().Be(0);
        fallback.Low!.Value.Should().BeApproximately(0, 1e-12);
        fallback.High!.Value.Should().BeApproximately(0.561497, 1e-6);
        stats.Modes[3].Should().Be(new ModeStats("habit/template", 1, 0, 0, null, null, null));
        store.Stats("other").MemoryFailures.Should().Be(0);
    }

    [Fact]
    public void Cost_per_request_is_a_trend_over_windows_and_can_be_priced_again_with_fitted_coefficients()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "c.sqlite"));
        for (var i = 0; i < 5; i++)
        {
            var id = $"t{i}";
            store.OpenTrace(id, "support", "s");
            store.RecordCall(Call(id, $"{id}-embed", "embed", 10, 0, energy: 0.5));
            store.RecordCall(Call(id, $"{id}-judge", "judge", 100 * (i + 1), 1, energy: 999));
            store.CloseTrace(id, new TraceOutcome { Mode = "partial", Energy = 10 * (i + 1) });
        }

        var recorded = store.Stats("support", window: 2);
        var repriced = store.Stats("support", window: 2, pricing: new EnergyModel(Fixed: 100, PerFreshPromptToken: 1, PerCachedToken: 0, PerOutputToken: 10));

        recorded.Cost.Should().Equal(new CostWindow(0, 2, 15), new CostWindow(2, 2, 35), new CostWindow(4, 1, 50));
        // Judge calls priced again (100 + tokens + 10), embedding calls as recorded (0.5).
        repriced.Cost.Select(w => w.MeanCost).Should().Equal(260.5, 460.5, 610.5);
    }

    private static CallRecord Call(string trace, string id, string role, int prompt, int completion, double energy) => new()
    {
        CallId = id,
        TraceId = trace,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Role = role,
        Model = "m",
        PromptTokens = prompt,
        CachedTokens = 0,
        CompletionTokens = completion,
        LatencyMs = 1,
        Energy = energy,
    };
}
