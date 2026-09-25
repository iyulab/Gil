using AwesomeAssertions;
using Gil.Llm;
using Gil.Telemetry;

namespace Gil.Tests.Llm;

public sealed class EnergyModelTests
{
    [Fact]
    public void Fitting_recovers_the_per_call_and_per_token_costs_of_recorded_calls()
    {
        var truth = new EnergyModel(Fixed: 125, PerFreshPromptToken: 0.74, PerCachedToken: 0.05, PerOutputToken: 15);
        var samples = new[] { (320, 0, 1), (900, 64, 1), (150, 0, 40), (2000, 512, 3), (600, 128, 120), (75, 0, 250) }
            .Select(c => new CallCostSample(c.Item1, c.Item2, c.Item3, truth.Of(c.Item1, c.Item2, c.Item3)))
            .ToList();

        var fitted = EnergyModel.Fit(samples)!;

        fitted.Fixed.Should().BeApproximately(125, 1e-6);
        fitted.PerFreshPromptToken.Should().BeApproximately(0.74, 1e-9);
        fitted.PerCachedToken.Should().BeApproximately(0.05, 1e-9);
        fitted.PerOutputToken.Should().BeApproximately(15, 1e-9);
    }

    [Fact]
    public void A_term_the_calls_never_exercise_gets_zero()
    {
        var samples = new[] { (100, 1, 140.0), (400, 1, 215.0), (100, 20, 425.0), (700, 5, 350.0) }
            .Select(c => new CallCostSample(c.Item1, 0, c.Item2, c.Item3))
            .ToList();

        var fitted = EnergyModel.Fit(samples)!;

        fitted.PerCachedToken.Should().Be(0, "no call reported cached tokens");
        fitted.Fixed.Should().BeApproximately(100, 1e-6);
        fitted.PerFreshPromptToken.Should().BeApproximately(0.25, 1e-9);
        fitted.PerOutputToken.Should().BeApproximately(15, 1e-9);
    }

    [Fact]
    public void Too_few_or_indistinguishable_calls_give_no_model()
    {
        EnergyModel.Fit([new CallCostSample(100, 0, 1, 200), new CallCostSample(200, 0, 2, 300)]).Should().BeNull("three terms, two calls");
        EnergyModel.Fit([.. Enumerable.Repeat(new CallCostSample(100, 0, 1, 200), 5)]).Should().BeNull("every call the same size");
    }

    [Fact]
    public void The_store_hands_over_chat_calls_with_server_times_for_one_model()
    {
        var directory = Directory.CreateTempSubdirectory("gil-fit-").FullName;
        using (var store = new SqliteTelemetryStore(Path.Combine(directory, "fit.sqlite")))
        {
            store.OpenTrace("t", "task", "s");
            store.RecordCall(Call("a", "judge", "local", 320, 1, 41.5, 12.0));
            store.RecordCall(Call("b", "fallback", "local", 150, 40, 20.0, null));
            store.RecordCall(Call("c", "judge", "other", 320, 1, 99.0, 1.0));
            store.RecordCall(Call("d", "embed", "local", 8, 0, null, null));
            store.RecordCall(Call("e", "judge", "local", 320, 1, null, null));

            store.ServerTimeSamples("local").Should().Equal(new CallCostSample(320, 0, 1, 53.5), new CallCostSample(150, 0, 40, 20.0));
            store.ServerTimeSamples().Should().HaveCount(3);
        }

        Directory.Delete(directory, recursive: true);
    }

    private static CallRecord Call(string id, string role, string model, int prompt, int completion, double? gpuPrompt, double? gpuPredicted) => new()
    {
        CallId = id,
        TraceId = "t",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Role = role,
        Model = model,
        PromptTokens = prompt,
        CachedTokens = 0,
        CompletionTokens = completion,
        LatencyMs = 1,
        GpuPromptMs = gpuPrompt,
        GpuPredictedMs = gpuPredicted,
        Energy = 0,
    };
}
