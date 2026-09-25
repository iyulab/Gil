using System.Text.Json;
using AwesomeAssertions;
using Gil.Ontology;
using Gil.Traverse;

namespace Gil.Tests.Traverse;

public sealed class GreedyTraverserTests
{
    private const string Tree = """
        id: root
        children:
          - id: quality
            label: Quality
            description: defects
            children:
              - id: dimension
                label: Dimension
                description: out of tolerance
                options:
                  - {id: dim-01, kind: answer, label: Ask data, description: send data, text: Please send the data.}
          - id: work
            label: Work
            description: no habits yet
        """;

    private static readonly Node Root = OntologyYaml.Parse(Tree).Root;
    private static readonly Thresholds Loose = new([0.5, 0.5], 0.5);

    [Fact]
    public async Task Descends_to_the_leaf_and_accepts_a_habit()
    {
        var judge = new Scripted(("quality", 0.9), ("dimension", 0.9), ("dim-01", 0.9));

        var result = await new GreedyTraverser(judge).TraverseAsync("parts are too long", Root, Loose, "t", cancellationToken: TestContext.Current.CancellationToken);

        result.Habit!.Id.Should().Be("dim-01");
        result.Path.Select(s => (s.Node, s.Outcome)).Should().Equal(("root", "accept"), ("quality", "accept"), ("dimension", "accept"));
        result.Confirmed.Should().Equal("quality", "dimension", "dim-01");
        result.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_node_without_habits_is_skipped_without_a_call()
    {
        var judge = new Scripted(("work", 0.95));

        var result = await new GreedyTraverser(judge).TraverseAsync("x", Root, Loose, "t", cancellationToken: TestContext.Current.CancellationToken);

        result.ExitReason.Should().Be("skip");
        result.Path[^1].Should().Match<PathStep>(s => s.Node == "work" && s.Outcome == "skip" && s.Energy == 0);
        result.Calls.Should().Be(1);
        result.Confirmed.Should().Equal("work");
    }

    [Theory]
    [InlineData(0.4, true, "quality", "low_confidence")]
    [InlineData(0.9, false, "quality", "untrusted")]
    [InlineData(0.9, true, null, "none_selected")]
    public async Task Stops_with_the_reason(double confidence, bool trusted, string? choice, string reason)
    {
        var judge = new Scripted((choice, confidence)) { Trusted = trusted };

        var result = await new GreedyTraverser(judge).TraverseAsync("x", Root, Loose, "t", cancellationToken: TestContext.Current.CancellationToken);

        result.ExitReason.Should().Be(reason);
        result.Path.Single().Should().Match<PathStep>(s => s.Outcome == "exit" && s.Chosen == null);
        result.Path.Single().NoneProb!.Value.Should().BeApproximately(choice is null ? 1 : 1 - confidence, 1e-12, "the step says whether 'none of these' won");
        result.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_leaf_uses_its_own_threshold()
    {
        var judge = new Scripted(("quality", 0.95), ("dimension", 0.95), ("dim-01", 0.6));

        var result = await new GreedyTraverser(judge).TraverseAsync("x", Root, new Thresholds([0.9], Leaf: 0.7), "t", cancellationToken: TestContext.Current.CancellationToken);

        result.ExitReason.Should().Be("low_confidence");
        result.Path[^1].Node.Should().Be("dimension");
    }

    [Fact]
    public async Task Choosing_a_shadow_defers_instead_of_answering()
    {
        var judge = new Scripted(("quality", 0.9), ("dimension", 0.9), ("shadow-1", 0.9));
        var shadows = new Dictionary<string, IReadOnlyList<Candidate>> { ["dimension"] = [new Candidate("shadow-1", "known", "a known answer", "Known.")] };

        var result = await new GreedyTraverser(judge).TraverseAsync("x", Root, Loose, "t", shadows, TestContext.Current.CancellationToken);

        result.ExitReason.Should().Be("shadow");
        result.Path[^1].Should().Match<PathStep>(s => s.Outcome == "defer" && s.Chosen == "shadow-1");
        judge.Shown[^1].Select(c => c.Id).Should().Equal("dim-01", "shadow-1");
    }

    [Fact]
    public async Task Recorded_judgments_give_the_same_paths_as_another_implementation()
    {
        // Recorded judgments per request and node, with the paths another implementation's traverser produced from them
        // under several thresholds — the committed synthetic fixture, or a file GIL_COMPAT_TRAVERSE points at.
        var path = Conformance.Fixture("GIL_COMPAT_TRAVERSE", "traverse.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_TRAVERSE is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = OntologyYaml.Parse(document.RootElement.GetProperty("tree").GetString()!).Root;
        var compared = 0;
        foreach (var @case in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var judge = new RecordedJudge(@case.GetProperty("judgments"));
            foreach (var expected in @case.GetProperty("expected").EnumerateArray())
            {
                var thresholds = new Thresholds([.. expected.GetProperty("per_layer").EnumerateArray().Select(v => v.GetDouble())], expected.GetProperty("leaf").GetDouble());
                var result = await new GreedyTraverser(judge).TraverseAsync("", root, thresholds, @case.GetProperty("trace_id").GetString()!, cancellationToken: TestContext.Current.CancellationToken);

                var because = $"{@case.GetProperty("trace_id").GetString()} at {thresholds.Leaf}";
                result.Path.Select(s => (s.Node, s.Chosen, s.Outcome)).Should().Equal(
                    expected.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("node").GetString()!, s.GetProperty("chosen").GetString(), s.GetProperty("outcome").GetString()!)),
                    because);
                result.Habit?.Id.Should().Be(expected.GetProperty("habit").GetString(), because);
                result.ExitReason.Should().Be(expected.GetProperty("exit_reason").GetString(), because);
                compared++;
            }
        }

        compared.Should().BePositive();
    }

    private sealed class Scripted(params (string? Choice, double Confidence)[] script) : IJudge
    {
        private int _next;

        public bool Trusted { get; init; } = true;

        public List<IReadOnlyList<Candidate>> Shown { get; } = [];

        public Task<Judgment> JudgeAsync(string state, IReadOnlyList<Candidate> candidates, string traceId, string? nodeId = null, int? layer = null, CancellationToken cancellationToken = default)
        {
            Shown.Add(candidates);
            var (choice, confidence) = script[_next++];
            return Task.FromResult(Make(choice, confidence, Trusted, choice is null ? [] : new Dictionary<string, double> { [choice] = confidence }));
        }
    }

    private static Judgment Make(string? choice, double confidence, bool trusted, Dictionary<string, double> probs) => new()
    {
        Probs = probs,
        Choice = trusted ? choice : null,
        Confidence = confidence,
        NoneProb = 1 - probs.Values.Sum(),
        LabelMass = 0.99,
        Trusted = trusted,
        Call = new CallRecord
        {
            CallId = "c",
            TraceId = "t",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Role = "judge",
            Model = "m",
            PromptTokens = 0,
            CachedTokens = 0,
            CompletionTokens = 0,
            LatencyMs = 0,
            Energy = 1,
        },
    };
}
