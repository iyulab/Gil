using System.Text.Json;
using AwesomeAssertions;
using Gil.Habits;
using Gil.Ontology;
using Gil.Telemetry;
using Microsoft.Data.Sqlite;

namespace Gil.Tests.Habits;

public sealed class DeactivationTests : IDisposable
{
    private static readonly Node Tree = OntologyYaml.Parse("""
        id: root
        children:
          - id: bank
            label: bank
            description: banking
            options:
              - {id: wrong, kind: answer, label: wrong, description: often wrong, text: wrong}
              - {id: argued, kind: answer, label: argued, description: often disputed, text: argued}
              - {id: old, kind: answer, label: old, description: long unused, text: old}
              - {id: fresh, kind: answer, label: fresh, description: just used, text: fresh}
        """).Root;

    private static readonly DeactivationPolicy Policy = new(HalfLife: 10, StaleBelow: 0.25, ReliabilityBelow: 0.4, MinVerdicts: 3, DisputeRate: 0.5, MinExplored: 4);

    private static readonly ReliabilityWeights Weights = new(Reinforce: 1, Penalty: 3, PriorStrength: 2);

    private readonly string _directory = Directory.CreateTempSubdirectory("gil-deactivation-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Each_habit_gets_its_heaviest_reason_only_and_the_order_is_unreliable_disputed_stale()
    {
        var statistics = new FixedStatistics(new Dictionary<string, HabitCounts>
        {
            ["wrong"] = new(Reinforced: 1, Penalized: 3, Explored: 4, Disputed: 4), // unreliable wins over disputed
            ["argued"] = new(Reinforced: 5, Explored: 4, Disputed: 2),
            ["fresh"] = new(Reinforced: 2),
        });
        var usage = new HabitUsage(new Dictionary<string, int> { ["wrong"] = 99, ["argued"] = 99, ["fresh"] = 98 }, Total: 100);

        var proposals = Deactivation.Propose(Tree, statistics, "task", usage, Policy, Weights);

        proposals.Select(p => (p.HabitId, p.Reason)).Should().Equal(
            ("wrong", DeactivationReason.Unreliable),
            ("argued", DeactivationReason.Disputed),
            ("old", DeactivationReason.Stale));
        proposals[0].Score.Should().BeApproximately((1 + 1.0) / (2 + 1 + 9), 1e-12);
        proposals[2].Recency.Should().BeApproximately(Math.Pow(0.5, 100 / 10.0), 1e-12, "a habit never used is measured from the task's start");
        proposals.Should().OnlyContain(p => p.Anchor == "bank");
    }

    [Fact]
    public void Thin_evidence_proposes_nothing_and_a_zero_half_life_never_ages()
    {
        var statistics = new FixedStatistics(new Dictionary<string, HabitCounts>
        {
            ["wrong"] = new(Penalized: 2), // below the minimum verdicts
            ["argued"] = new(Explored: 3, Disputed: 3), // below the minimum cross-checks
        });

        Deactivation.Propose(Tree, statistics, "task", new HabitUsage(new Dictionary<string, int>(), 1_000), Policy with { HalfLife = 0 }, Weights)
            .Should().BeEmpty();
    }

    [Fact]
    public void Usage_counts_answered_requests_and_remembers_where_each_habit_last_answered()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "t.sqlite"));
        Close(store, "r1", "habit/answer", Step("bank", "old", "accept"));
        Close(store, "r2", "fallback", Step("bank", null, "exit"));
        Close(store, "r3", "habit/answer", Step("bank", "fresh", "accept"));
        Close(store, "r4", "memory", null);
        store.OpenTrace("r5", "support", "still open");
        Close(store, "r6", "habit/answer", Step("bank", "old", "accept"));

        var usage = store.Usage("support");

        usage.Total.Should().Be(5, "every closed request counts, whichever path answered it");
        usage.LastIndex.Should().Equal(new Dictionary<string, int> { ["old"] = 4, ["fresh"] = 2 });
    }

    [Fact]
    public void Proposes_what_the_reference_implementation_proposed_from_the_same_run()
    {
        // A run's final tree, habit counts and requests, with the proposals another implementation made under a few
        // policies — the committed synthetic fixture, or a file GIL_COMPAT_DEACTIVATION points at. The requests are
        // written to a store so usage is read for real.
        var path = Conformance.Fixture("GIL_COMPAT_DEACTIVATION", "deactivation.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_DEACTIVATION is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fixture = document.RootElement;
        var task = fixture.GetProperty("task").GetString()!;
        var tree = OntologyYaml.Parse(fixture.GetProperty("tree").GetString()!).Root;
        var w = fixture.GetProperty("weights");
        var weights = new ReliabilityWeights(w.GetProperty("reinforce").GetDouble(), w.GetProperty("penalty").GetDouble(), w.GetProperty("prior_strength").GetDouble());
        var statistics = new FixedStatistics(fixture.GetProperty("counts").EnumerateObject().ToDictionary(
            c => c.Name,
            c => new HabitCounts(
                c.Value.GetProperty("reinforced").GetInt32(), c.Value.GetProperty("penalized").GetInt32(), c.Value.GetProperty("missed").GetInt32(),
                c.Value.GetProperty("explored").GetInt32(), c.Value.GetProperty("disputed").GetInt32())));

        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "replay.sqlite"));
        var number = 0;
        foreach (var request in fixture.GetProperty("requests").EnumerateArray())
        {
            var last = request.GetProperty("last");
            PathStep? step = last.ValueKind == JsonValueKind.Null
                ? null
                : Step(last.GetProperty("node").GetString()!, last.GetProperty("chosen").GetString(), last.GetProperty("outcome").GetString()!);
            Close(store, $"r{number++:D6}", request.GetProperty("mode").GetString()!, step, task);
        }

        var usage = store.Usage(task);
        var expectedUsage = fixture.GetProperty("usage");
        usage.Total.Should().Be(expectedUsage.GetProperty("total").GetInt32());
        usage.LastIndex.Should().BeEquivalentTo(expectedUsage.GetProperty("last_index").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));

        var cases = 0;
        foreach (var @case in fixture.GetProperty("cases").EnumerateArray())
        {
            var p = @case.GetProperty("policy");
            var policy = new DeactivationPolicy(
                p.GetProperty("half_life").GetDouble(), p.GetProperty("stale_below").GetDouble(), p.GetProperty("reliability_below").GetDouble(),
                p.GetProperty("min_verdicts").GetInt32(), p.GetProperty("dispute_rate").GetDouble(), p.GetProperty("min_explored").GetInt32());
            var name = @case.GetProperty("name").GetString();

            var proposals = Deactivation.Propose(tree, statistics, task, usage, policy, weights);

            var expected = @case.GetProperty("expected").EnumerateArray().ToList();
            proposals.Select(x => (x.HabitId, x.Anchor, x.Reason.ToString().ToLowerInvariant())).Should().Equal(
                expected.Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("anchor").GetString()!, e.GetProperty("reason").GetString()!)),
                $"policy {name}");
            foreach (var (proposal, want) in proposals.Zip(expected))
            {
                proposal.Recency.Should().BeApproximately(want.GetProperty("recency").GetDouble(), 1e-12, proposal.HabitId);
                proposal.Score.Should().BeApproximately(want.GetProperty("score").GetDouble(), 1e-12, proposal.HabitId);
            }

            cases++;
        }

        cases.Should().BePositive();
    }

    private static PathStep Step(string node, string? chosen, string outcome) =>
        new(node, 1, chosen, 0.9, outcome, new Dictionary<string, double>(), 1);

    private static void Close(SqliteTelemetryStore store, string traceId, string mode, PathStep? last, string task = "support")
    {
        store.OpenTrace(traceId, task, $"state {traceId}");
        store.CloseTrace(traceId, new TraceOutcome { Output = "x", Mode = mode, Path = last is null ? [] : [last], Energy = 1 });
    }

    private sealed class FixedStatistics(IReadOnlyDictionary<string, HabitCounts> counts) : IHabitStatistics
    {
        public void RecordPath(string scope, IReadOnlyList<PathStep> path) => throw new NotSupportedException();

        public void RecordOutcome(string scope, string itemId, HabitCounts delta) => throw new NotSupportedException();

        public NodeVisits Visits(string scope, string nodeId) => throw new NotSupportedException();

        public IReadOnlyDictionary<string, int> Choices(string scope, string nodeId) => throw new NotSupportedException();

        public HabitCounts Reliability(string scope, string itemId) => counts.GetValueOrDefault(itemId) ?? new HabitCounts();
    }
}
