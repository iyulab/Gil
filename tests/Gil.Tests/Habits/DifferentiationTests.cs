using System.Text.Json;
using AwesomeAssertions;
using Gil.Fallback;
using Gil.Habits;
using Gil.Ontology;

namespace Gil.Tests.Habits;

public sealed class DifferentiationTests
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
            options:
              - {id: book, kind: answer, label: book, description: book a flight, text: book_flight}
        """).Root;

    // The contract knows answers the tree has no habits for yet.
    private static readonly TreeAnswerContract Contract = new(OntologyYaml.Parse("""
        id: root
        children:
          - id: bank
            label: bank
            description: banking
            options:
              - {id: balance, kind: answer, label: balance, description: how much is in my account, text: balance}
              - {id: lost, kind: answer, label: lost, description: card lost, text: card_lost}
          - id: travel
            label: travel
            description: trips
            options:
              - {id: book, kind: answer, label: book, description: book a flight, text: book_flight}
        """).Root);

    [Fact]
    public void A_node_is_flagged_when_its_candidates_reach_the_scheme_capacity_less_the_headroom()
    {
        var bank = Tree.Children[0] with
        {
            Habits = [.. Enumerable.Range(0, 7).Select(i => new Habit { Id = $"h{i}", Kind = HabitKind.Answer, Label = $"h{i}", Description = $"d{i}", Text = $"h{i}" })],
        };
        var tree = Tree with { Children = [bank, Tree.Children[1]] };

        Differentiation.Capacity(tree).Should().BeEmpty();
        Differentiation.Capacity(tree, headroom: 2).Should().Equal(new DifferentiationSignal(DifferentiationKind.Capacity, "bank", null, null, 7, []));
    }

    [Fact]
    public void Repeated_answers_at_an_anchor_with_children_are_told_apart_as_missed_reanchor_or_orphan()
    {
        var candidates = new[]
        {
            Correct("t1", "root", "card_lost"),
            Correct("t2", "root", "book_flight"),
            Correct("t3", "root", "card_lost"),
            Correct("t4", "root", "weather"),
            Correct("t5", "root", "book_flight"),
            Correct("t6", "root", "weather"),
            Correct("t7", "root", "card_lost"),
            Correct("t8", "root", "해당 없음"),
            Correct("t9", "root", "해당 없음"),
            Correct("t10", "bank", "card_lost"), // an anchor without children is promotion's job
            Correct("t11", "bank", "card_lost"),
            new PromotionCandidate("t12", "x", "root", "weather", "wrong", null, 1),
        };

        var signals = Differentiation.Anchored(Tree, candidates, Contract, minSupport: 2, never: new HashSet<string> { "해당 없음" });

        signals.Should().BeEquivalentTo(
            new[]
            {
                new DifferentiationSignal(DifferentiationKind.Reanchor, "root", "card_lost", "bank", 3, ["t1", "t3", "t7"]),
                new DifferentiationSignal(DifferentiationKind.Missed, "root", "book_flight", "travel", 2, ["t2", "t5"]),
                new DifferentiationSignal(DifferentiationKind.Orphan, "root", "weather", null, 2, ["t4", "t6"]),
            },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void Without_a_narrowable_contract_no_child_can_take_an_answer()
    {
        var signals = Differentiation.Anchored(Tree, [Correct("t1", "root", "card_lost"), Correct("t2", "root", "card_lost")], new TextContract(), minSupport: 2);

        signals.Single().Kind.Should().Be(DifferentiationKind.Orphan);
    }

    [Fact]
    public void Signals_what_the_reference_implementation_signalled_on_the_same_runs()
    {
        // Runs' final trees, promotion evidence and contracts, with the signals another implementation raised from them —
        // the committed synthetic fixture, or a file GIL_COMPAT_DIFFERENTIATION points at.
        var path = Conformance.Fixture("GIL_COMPAT_DIFFERENTIATION", "differentiation.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_DIFFERENTIATION is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var runs = 0;
        foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
        {
            var task = run.GetProperty("task").GetString();
            var tree = OntologyYaml.Parse(run.GetProperty("tree").GetString()!).Root;
            var contract = new TreeAnswerContract(OntologyYaml.Parse(run.GetProperty("contract_tree").GetString()!).Root);
            var candidates = run.GetProperty("candidates").EnumerateArray().Select(c => new PromotionCandidate(
                c.GetProperty("trace_id").GetString()!, c.GetProperty("state").GetString()!, c.GetProperty("anchor").GetString()!,
                Text(c, "output"), Text(c, "verdict"), Text(c, "correction"), c.GetProperty("fallback_energy").GetDouble())).ToList();
            var never = run.GetProperty("never").EnumerateArray().Select(n => n.GetString()!).ToHashSet();
            var expected = run.GetProperty("expected");

            Same(Differentiation.Capacity(tree), expected.GetProperty("capacity_0"), $"{task} capacity");
            Same(Differentiation.Capacity(tree, headroom: 2), expected.GetProperty("capacity_2"), $"{task} capacity with headroom");
            Same(
                Differentiation.Anchored(tree, candidates, contract, run.GetProperty("n_min").GetInt32(), never),
                expected.GetProperty("anchored"),
                $"{task} anchored");
            runs++;
        }

        runs.Should().BePositive();
    }

    private static void Same(IReadOnlyList<DifferentiationSignal> actual, JsonElement expected, string what)
    {
        var want = expected.EnumerateArray().Select(s => new DifferentiationSignal(
            Enum.Parse<DifferentiationKind>(s.GetProperty("kind").GetString()!, ignoreCase: true),
            s.GetProperty("node").GetString()!,
            Text(s, "output"),
            Text(s, "target"),
            s.GetProperty("support").GetInt32(),
            [.. s.GetProperty("sources").EnumerateArray().Select(x => x.GetString()!)])).ToList();
        actual.Should().BeEquivalentTo(want, options => options.WithStrictOrdering(), what);
    }

    private static string? Text(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static PromotionCandidate Correct(string traceId, string anchor, string output) =>
        new(traceId, $"input of {traceId}", anchor, output, "correct", null, 1);
}
