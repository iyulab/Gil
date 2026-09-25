using System.Text.Json;
using AwesomeAssertions;
using Gil.Judge;
using Gil.Llm;

namespace Gil.Tests.Judge;

public sealed class SingleTokenJudgeTests
{
    private static readonly Candidate[] Candidates =
    [
        new("quality", "Quality", "defects"),
        new("equipment", "Equipment", "machine failure"),
        new("delivery", "Delivery", "due dates", Answer: "shipping"),
    ];

    [Fact]
    public void Label_schemes_keep_the_first_symbol_for_none_of_these_and_refuse_overflow()
    {
        LabelScheme.NoneLabel(LabelScheme.Digits).Should().Be("0");
        LabelScheme.Labels(LabelScheme.Letters, 3).Should().Equal("B", "C", "D");
        LabelScheme.Capacity(LabelScheme.Digits).Should().Be(9);
        var overflow = () => LabelScheme.Labels(LabelScheme.Digits, 10);
        overflow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_distribution_keeps_trimmed_label_tokens_renormalizes_and_trusts_only_enough_label_mass()
    {
        var logprobs = new[] { new TokenLogprob(" B", Math.Log(0.5)), new TokenLogprob("B", Math.Log(0.2)), new TokenLogprob("A", Math.Log(0.2)), new TokenLogprob("hello", Math.Log(0.1)) };

        var trusted = LabelDistribution.From(logprobs, ["A", "B", "C"], minLabelMass: 0.85);
        var untrusted = LabelDistribution.From(logprobs, ["A", "B", "C"], minLabelMass: 0.95);

        trusted.LabelMass.Should().BeApproximately(0.9, 1e-9);
        trusted.Probs["B"].Should().BeApproximately(0.7 / 0.9, 1e-9);
        (trusted.Choice, trusted.Trusted).Should().Be(("B", true));
        untrusted.Trusted.Should().BeFalse();
    }

    [Fact]
    public async Task A_confident_label_maps_back_to_its_candidate_and_the_call_records_what_was_shown()
    {
        var (judge, model, sink) = Judge("C", new() { OrderSeed = null });

        var judgment = await judge.JudgeAsync("the press stopped", Candidates, "t1", "root", 1, TestContext.Current.CancellationToken);

        judgment.Choice.Should().Be("equipment"); // no shuffling: B quality, C equipment, D delivery
        judgment.Trusted.Should().BeTrue();
        var prompt = model.Requests.Single().Messages[^1].Content;
        prompt.Should().Contain("A. 해당 없음").And.Contain("C. Equipment — machine failure").And.StartWith("<입력>\nthe press stopped");
        model.Requests.Single().MaxTokens.Should().Be(1);
        var call = sink.Calls.Single();
        call.Outcome.Should().Be("accept");
        call.Candidates!.Select(c => (c.Id, c.ShownAs)).Should().Equal((null, "A"), ("quality", "B"), ("equipment", "C"), ("delivery", "D"));
        call.Candidates![3].Answer.Should().Be("shipping");
        call.Distribution!.Keys.Should().BeEquivalentTo(["equipment"]);
    }

    [Fact]
    public async Task Choosing_none_of_these_exits_without_a_choice()
    {
        var (judge, _, sink) = Judge("A", new() { OrderSeed = null });

        var judgment = await judge.JudgeAsync("unrelated", Candidates, "t1", cancellationToken: TestContext.Current.CancellationToken);

        (judgment.Choice, judgment.NoneProb).Should().Be((null, 1.0));
        sink.Calls.Single().Outcome.Should().Be("exit");
    }

    [Fact]
    public async Task The_shown_order_depends_on_the_request_only()
    {
        var options = new SingleTokenJudgeOptions { OrderSeed = 7 };
        var (first, firstModel, _) = Judge("B", options);
        var (second, secondModel, _) = Judge("B", options);

        await first.JudgeAsync("an earlier request", Candidates, "p", "root", cancellationToken: TestContext.Current.CancellationToken);
        await first.JudgeAsync("same request", Candidates, "t", "root", cancellationToken: TestContext.Current.CancellationToken);
        await second.JudgeAsync("same request", Candidates.Reverse().ToArray(), "t-other-run", "root", cancellationToken: TestContext.Current.CancellationToken);

        firstModel.Requests[^1].Messages[^1].Content.Should().Be(secondModel.Requests[^1].Messages[^1].Content);
        var orders = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            await first.JudgeAsync($"request {i}", Candidates, "x", "root", cancellationToken: TestContext.Current.CancellationToken);
            orders.Add(string.Join(",", firstModel.Requests[^1].Messages[^1].Content.Split('\n').Where(l => l.Contains(" — ", StringComparison.Ordinal))));
        }

        orders.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void Recorded_judgments_from_another_implementation_give_the_same_distribution_and_outcome()
    {
        // Recorded judge calls (shown candidates, first-token log-probabilities, and the distribution and outcome another
        // implementation derived) — the committed synthetic fixture, or a file GIL_COMPAT_JUDGE points at — to check this
        // one derives the same.
        var path = Conformance.Fixture("GIL_COMPAT_JUDGE", "judge.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_JUDGE is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var checkedCount = 0;
        foreach (var recorded in document.RootElement.EnumerateArray())
        {
            var shown = recorded.GetProperty("shown").EnumerateArray().ToList();
            var none = shown[0].GetProperty("shown_as").GetString()!;
            var byLabel = shown.Skip(1).ToDictionary(s => s.GetProperty("shown_as").GetString()!, s => s.GetProperty("id").GetString()!);
            var logprobs = recorded.GetProperty("top_logprobs").EnumerateArray()
                .Select(t => new TokenLogprob(t.GetProperty("token").GetString()!, t.GetProperty("logprob").GetDouble()));

            var distribution = LabelDistribution.From(logprobs, [none, .. byLabel.Keys], minLabelMass: 0.9);

            var expected = recorded.GetProperty("distribution").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
            var actual = distribution.Probs.Where(p => byLabel.ContainsKey(p.Key)).ToDictionary(p => byLabel[p.Key], p => p.Value);
            actual.Keys.Should().BeEquivalentTo(expected.Keys, recorded.GetProperty("trace_id").GetString());
            foreach (var (id, p) in expected)
            {
                actual[id].Should().BeApproximately(p, 1e-9);
            }

            distribution.LabelMass.Should().BeApproximately(recorded.GetProperty("label_mass").GetDouble(), 1e-9);
            var outcome = distribution.Trusted && distribution.Choice != none ? "accept" : "exit";
            outcome.Should().Be(recorded.GetProperty("outcome").GetString());
            checkedCount++;
        }

        checkedCount.Should().BePositive();
    }

    private static (SingleTokenJudge Judge, ScriptedModel Model, ListSink Sink) Judge(string answer, SingleTokenJudgeOptions options)
    {
        var model = new ScriptedModel(answer);
        var sink = new ListSink();
        return (new SingleTokenJudge(new CallRecorder(model, new EnergyModel(0, 1, 0, 1), sink), options), model, sink);
    }

    private sealed class ScriptedModel(string answer) : IChatModel
    {
        public List<ChatRequest> Requests { get; } = [];

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ChatResult
            {
                Model = "m",
                Content = answer,
                PromptTokens = 10,
                CachedTokens = 0,
                CompletionTokens = 1,
                LatencyMs = 1,
                FirstToken = answer,
                TopLogprobs = [new TokenLogprob(answer, 0)],
                RawResponse = "{}",
            });
        }
    }
}
