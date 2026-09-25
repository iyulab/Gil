using System.Text.Json;
using AwesomeAssertions;
using Gil.Habits;
using Gil.Ontology;
using Gil.Telemetry;
using Microsoft.Data.Sqlite;

namespace Gil.Tests.Habits;

public sealed class PromotionTests : IDisposable
{
    private static readonly Node Tree = OntologyYaml.Parse("""
        id: root
        children:
          - id: bank
            label: bank
            description: banking
            label_scheme: digits
            options:
              - {id: balance, kind: answer, label: balance, description: how much is in my account, text: balance}
          - id: travel
            label: travel
            description: trips
          - id: work
            label: work
            description: office
            children:
              - id: pto
                label: pto
                description: days off
        """).Root;

    private static readonly PromotionPolicy Policy = new(MinSupport: 2) { NeverPromote = new HashSet<string> { "none" } };

    private readonly string _directory = Directory.CreateTempSubdirectory("gil-promotion-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Exact_grouping_keeps_the_order_outputs_first_appear_in()
    {
        var groups = RepeatedOutputProposer.ExactGrouping(["b", "a", "b", "c", "a"]);

        groups.Select(g => (g.Output, string.Join(",", g.Positions))).Should().Equal(("b", "0,2"), ("a", "1,4"), ("c", "3"));
    }

    [Fact]
    public void A_repeated_confirmed_output_is_proposed_where_it_pays_and_nothing_already_answered_is()
    {
        var proposer = new RepeatedOutputProposer(Policy, new Dictionary<string, double> { ["bank"] = 40, ["root"] = 80 });
        var candidates = new[]
        {
            Candidate("t1", "bank", "card_lost", "correct", energy: 300, state: "I lost my card"),
            Candidate("t2", "bank", "card_lost", "correct", energy: 500),
            Candidate("t3", "bank", "balance", "correct"), // already a habit
            Candidate("t4", "bank", "balance", "correct"),
            Candidate("t5", "bank", "none", "correct"), // never promoted
            Candidate("t6", "bank", "none", "correct"),
            Candidate("t7", "bank", "once", "correct"), // below the minimum support
            Candidate("t8", "bank", "unchecked", null), // no feedback
            Candidate("t9", "bank", "unchecked", null),
            Candidate("t10", "work", "leave", "correct"), // a node with children takes no habits
            Candidate("t11", "work", "leave", "correct"),
            Candidate("t12", "travel", "book", "correct", energy: 100),
            Candidate("t13", "travel", "book", "correct", energy: 100),
        };

        var proposals = proposer.Propose(Tree, candidates);

        proposals.Select(p => (p.Anchor, p.Habit.Text)).Should().Equal(("bank", "card_lost"), ("travel", "book"));
        var lost = proposals[0];
        lost.Habit.Should().BeEquivalentTo(new Habit
        {
            Id = DerivedHabits.Id(DerivedHabits.PromotedPrefix, "bank", "card_lost"),
            Kind = HabitKind.Answer,
            Label = "card_lost",
            Description = "I lost my card",
            Text = "card_lost",
            Origin = "promoted",
        });
        lost.Sources.Should().Equal("t1", "t2");
        (lost.Support, lost.AnchorVolume).Should().Be((2, 9), "every fallback at the anchor counts in the denominator");
        lost.ExpectedSaving.Should().BeApproximately(2.0 / 9 * 400, 1e-9);
        lost.AddedCost.Should().Be(40, "one more candidate beside the one habit costs the anchor's judgment divided by its habits");
        proposals[1].AddedCost.Should().Be(60, "a first habit makes a new judgment, priced at the mean judgment");
        proposer.Rejected.Should().Equal(new Dictionary<string, int> { ["min_support"] = 1 });
    }

    [Fact]
    public void A_habit_that_costs_more_than_it_saves_is_not_proposed()
    {
        var proposer = new RepeatedOutputProposer(Policy, new Dictionary<string, double> { ["bank"] = 10_000 });

        var proposals = proposer.Propose(Tree, [Candidate("t1", "bank", "card_lost", "correct"), Candidate("t2", "bank", "card_lost", "correct")]);

        proposals.Should().BeEmpty();
        proposer.Rejected.Should().Equal(new Dictionary<string, int> { ["energy"] = 1 });
    }

    [Fact]
    public void Corrections_count_only_when_the_policy_says_so_and_the_cost_model_prices_a_habit_by_its_size()
    {
        var candidates = new[]
        {
            Candidate("t1", "travel", "wrong answer", "wrong", correction: " visa ", energy: 500),
            Candidate("t2", "travel", "another", "wrong", correction: "visa", energy: 500),
        };
        var model = new JudgeCostModel(Fixed: 100, PerChar: 0.5, MeanStateChars: 20, NoneChars: 5);

        new RepeatedOutputProposer(Policy, new Dictionary<string, double>(), model).Propose(Tree, candidates).Should().BeEmpty();
        var proposal = new RepeatedOutputProposer(Policy with { FromCorrections = true }, new Dictionary<string, double>(), model)
            .Propose(Tree, candidates).Single();

        proposal.Habit.Text.Should().Be("visa");
        proposal.AddedCost.Should().Be(model.NewCall("visa".Length + "input of t1".Length), "the label plus the first input as description");
    }

    [Fact]
    public void The_cost_model_is_a_least_squares_line_and_needs_two_sizes()
    {
        JudgeCostModel.Fit([new JudgeCostSample(10, 5, 4, 3), new JudgeCostSample(10, 7, 4, 3)]).Should().BeNull();

        var model = JudgeCostModel.Fit([new JudgeCostSample(10, 25, 4, 3), new JudgeCostSample(20, 45, 6, 5), new JudgeCostSample(30, 65, 8, 4)])!;

        (model.Fixed, model.PerChar, model.MeanStateChars, model.NoneChars).Should().Be((5.0, 2.0, 6.0, 5));
    }

    [Fact]
    public void Applying_stops_at_a_full_label_scheme_and_the_review_carries_both_trees()
    {
        var full = Tree with
        {
            Children = [Tree.Children[0] with { Habits = [.. Enumerable.Range(0, 9).Select(i => new Habit { Id = $"h{i}", Kind = HabitKind.Answer, Label = $"h{i}", Description = $"d{i}", Text = $"h{i}" })] }],
        };
        var proposal = new PromotionProposal("bank", new Habit { Id = "promoted-x", Kind = HabitKind.Answer, Label = "x", Description = "x", Text = "x", Origin = "promoted" }, ["t1"], 3, 3, 10, 1, []);

        var (after, notApplied) = Promotion.Apply(full, [proposal]);
        after.Find("bank")!.Habits.Should().HaveCount(9);
        notApplied.Should().ContainSingle().Which.Should().Contain("promoted-x");

        var review = Promotion.Review(Tree, [proposal]);
        review.Before.Should().NotContain("promoted-x");
        review.After.Should().Contain("promoted-x");
        OntologyYaml.Parse(review.After).Root.Find("bank")!.Habits.Select(h => h.Id).Should().Equal("balance", "promoted-x");
    }

    [Fact]
    public void The_store_supplies_fallback_answers_judgment_energy_and_cost_samples()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "t.sqlite"));
        Close(store, "t1", "fallback", "card_lost", [Step("root", "bank"), Step("bank", null)]);
        store.RecordFeedback("t1", "correct", null);
        Close(store, "t2", "habit/answer", "balance", [Step("root", "bank")]);
        Close(store, "t3", "partial", "visa", [Step("root", "travel"), Step("travel", null)]);
        store.RecordCall(Call("c1", "t1", "fallback", null, 30, null));
        store.RecordCall(Call("c2", "t1", "fallback", null, 20, null));
        store.RecordCall(Call("c3", "t1", "judge", "root", 10, [new ShownCandidate(null, "A", "해당 없음", null, null), new ShownCandidate("bank", "B", "bank", "돈 😀", null)]));
        store.RecordCall(Call("c4", "t2", "judge", "root", 30, [new ShownCandidate(null, "A", "none", null, null)]));

        store.PromotionCandidates("support").Should().Equal(
            new PromotionCandidate("t1", "state t1", "bank", "card_lost", "correct", null, 50),
            new PromotionCandidate("t3", "state t3", "travel", "visa", null, null, 0));
        store.JudgeEnergyByNode("support").Should().Equal(new Dictionary<string, double> { ["root"] = 20 });
        store.JudgeCostSamples("support").Should().BeEquivalentTo(new[]
        {
            new JudgeCostSample(8 + 5 + 4 + 3, 10, 8, 5), // code points: "state t1", "해당 없음", "bank", "돈 😀" (the emoji is one, not two UTF-16 units)
            new JudgeCostSample(8 + 4, 30, 8, 4),
        });
    }

    [Fact]
    public void Proposes_what_the_reference_implementation_proposed_at_each_recorded_round()
    {
        // Promotion rounds rebuilt from a recorded run — the committed synthetic fixture, or a file GIL_COMPAT_PROMOTION
        // points at: per round the tree, the evidence as it stood, the judgment energy and cost model, and what the other
        // implementation proposed.
        var path = Conformance.Fixture("GIL_COMPAT_PROMOTION", "promotion.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_PROMOTION is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fixture = document.RootElement;
        var policy = new PromotionPolicy(fixture.GetProperty("n_min").GetInt32())
        {
            NeverPromote = fixture.GetProperty("never_promote").EnumerateArray().Select(e => e.GetString()!).ToHashSet(),
        };
        var rounds = 0;
        foreach (var round in fixture.GetProperty("rounds").EnumerateArray())
        {
            var at = round.GetProperty("at_index").GetInt32();
            var tree = OntologyYaml.Parse(round.GetProperty("tree").GetString()!).Root;
            var candidates = round.GetProperty("candidates").EnumerateArray().Select(c => new PromotionCandidate(
                c.GetProperty("trace_id").GetString()!, c.GetProperty("state").GetString()!, c.GetProperty("anchor").GetString()!,
                Text(c, "output"), Text(c, "verdict"), Text(c, "correction"), c.GetProperty("fallback_energy").GetDouble())).ToList();
            var energy = round.GetProperty("judge_energy").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
            var model = round.GetProperty("cost_model") is { ValueKind: JsonValueKind.Object } m
                ? new JudgeCostModel(m.GetProperty("fixed").GetDouble(), m.GetProperty("per_char").GetDouble(), m.GetProperty("mean_state_chars").GetDouble(), m.GetProperty("none_chars").GetInt32())
                : null;

            if (round.GetProperty("cost_samples") is { ValueKind: JsonValueKind.Array } samples)
            {
                var fitted = JudgeCostModel.Fit([.. samples.EnumerateArray().Select(s => new JudgeCostSample(
                    s.GetProperty("chars").GetInt32(), s.GetProperty("energy").GetDouble(), s.GetProperty("state_chars").GetInt32(), s.GetProperty("none_chars").GetInt32()))])!;
                Close(fitted.Fixed, model!.Fixed, $"fixed at {at}");
                Close(fitted.PerChar, model.PerChar, $"per char at {at}");
                Close(fitted.MeanStateChars, model.MeanStateChars, $"mean state at {at}");
                fitted.NoneChars.Should().Be(model.NoneChars);
            }

            var proposer = new RepeatedOutputProposer(policy, energy, model);
            var proposals = proposer.Propose(tree, candidates);

            var expected = round.GetProperty("expected").EnumerateArray().ToList();
            proposals.Select(p => p.Habit.Id).Should().Equal(expected.Select(e => e.GetProperty("id").GetString()), $"round {at}");
            foreach (var (proposal, want) in proposals.Zip(expected))
            {
                (proposal.Anchor, proposal.Habit.Label, proposal.Habit.Description, proposal.Habit.Text).Should().Be(
                    (want.GetProperty("anchor").GetString()!, want.GetProperty("label").GetString()!, want.GetProperty("description").GetString()!, want.GetProperty("text").GetString()));
                proposal.Sources.Should().Equal(want.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
                (proposal.Support, proposal.AnchorVolume).Should().Be((want.GetProperty("support").GetInt32(), want.GetProperty("anchor_volume").GetInt32()));
                Close(proposal.ExpectedSaving, want.GetProperty("expected_saving").GetDouble(), $"saving of {proposal.Habit.Id}");
                Close(proposal.AddedCost, want.GetProperty("added_cost").GetDouble(), $"cost of {proposal.Habit.Id}");
                (proposal.Warnings.Count > 0).Should().Be(want.GetProperty("warned").GetBoolean(), $"warnings of {proposal.Habit.Id}");
            }

            var rejected = round.GetProperty("rejected").EnumerateObject().ToDictionary(p => p.Name == "n_min" ? "min_support" : p.Name, p => p.Value.GetInt32());
            proposer.Rejected.Should().Equal(rejected, $"round {at}");
            rounds++;
        }

        rounds.Should().BePositive();
    }

    // The reference sums floating point with compensation; plain summation agrees to far better than this.
    private static void Close(double actual, double expected, string what) =>
        actual.Should().BeApproximately(expected, Math.Max(1e-9, Math.Abs(expected) * 1e-9), what);

    private static string? Text(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static PromotionCandidate Candidate(string traceId, string anchor, string output, string? verdict, string? correction = null, double energy = 100, string? state = null) =>
        new(traceId, state ?? $"input of {traceId}", anchor, output, verdict, correction, energy);

    private static PathStep Step(string node, string? chosen) =>
        new(node, 1, chosen, 0.9, chosen is null ? "exit" : "accept", new Dictionary<string, double>(), 1);

    private static void Close(SqliteTelemetryStore store, string traceId, string mode, string output, IReadOnlyList<PathStep> path)
    {
        store.OpenTrace(traceId, "support", $"state {traceId}");
        store.CloseTrace(traceId, new TraceOutcome { Output = output, Mode = mode, Path = path, Energy = 1 });
    }

    private static CallRecord Call(string id, string traceId, string role, string? node, double energy, IReadOnlyList<ShownCandidate>? shown) => new()
    {
        CallId = id,
        TraceId = traceId,
        CreatedAt = DateTimeOffset.UnixEpoch,
        Role = role,
        Model = "m",
        NodeId = node,
        PromptTokens = 1,
        CachedTokens = 0,
        CompletionTokens = 1,
        LatencyMs = 1,
        Energy = energy,
        Candidates = shown,
    };
}
