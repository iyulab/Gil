using System.Text.Json;
using AwesomeAssertions;
using Gil.Habits;
using Gil.Ontology;
using Gil.Telemetry;
using Microsoft.Data.Sqlite;

namespace Gil.Tests.Habits;

public sealed class ShadowIndexTests : IDisposable
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
              - {id: transfer, kind: answer, label: transfer, description: move money, text: transfer}
          - id: empty
            label: empty
            description: nothing yet
        """).Root;

    private readonly string _directory = Directory.CreateTempSubdirectory("gil-shadows-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("partial", " card_lost ", "correct", null, null, "card_lost")]
    [InlineData("fallback", "card_lost", "correct", null, null, "card_lost")]
    [InlineData("habit/answer", "balance", "correct", null, null, null)]
    [InlineData("habit/answer", "balance", "wrong", " card_lost ", null, "card_lost")]
    [InlineData("habit/answer", "balance", "wrong", null, null, null)]
    [InlineData("habit/answer", "balance", null, null, "card_lost", "card_lost")]
    [InlineData("habit/answer", "balance", null, null, " balance ", null)]
    [InlineData("habit/answer", "balance", "correct", null, "card_lost", null)]
    public void A_known_answer_is_a_correction_a_confirmed_generated_answer_or_a_disagreeing_exploration(
        string mode, string output, string? verdict, string? correction, string? explored, string? expected) =>
        ShadowIndex.KnownAnswer(mode, output, verdict, correction, explored).Should().Be(expected);

    [Fact]
    public void Shadows_go_to_nodes_with_habits_skip_taken_and_excluded_answers_and_keep_the_best_supported_that_fit()
    {
        var evidence = new List<ShadowEvidence>
        {
            Correct("bank", "card_lost", "I lost my card"),
            Correct("bank", "balance", "already a habit"),
            Correct("bank", "not applicable", "no answer"),
            Correct("empty", "weather", "no habits here"),
        };

        // Ten digits less "none of these" hold nine candidates; two habits leave room for seven shadows.
        foreach (var answer in new[] { "a", "b", "c", "d", "e", "f", "g" })
        {
            evidence.Add(Correct("bank", answer, $"first {answer}"));
            evidence.Add(Correct("bank", answer, $"second {answer}"));
        }

        evidence.Add(Correct("bank", "card_lost", "lost it again"));
        evidence.Add(Correct("bank", "card_lost", "and again"));

        var index = ShadowIndex.Build(evidence, Tree, new HashSet<string> { "not applicable" });

        index.Keys.Should().Equal("bank");
        var shadows = index["bank"];
        shadows.Select(s => s.Answer).Should().Equal("card_lost", "a", "b", "c", "d", "e", "f"); // equal support keeps first-seen order
        shadows[0].Should().Be(new Candidate(ShadowIndex.Id("bank", "card_lost"), "card_lost", "I lost my card", "card_lost"));
        shadows.Should().OnlyContain(s => ShadowIndex.IsShadow(s.Id));
    }

    [Fact]
    public void A_long_answer_is_labelled_by_its_first_forty_characters_without_splitting_one()
    {
        var answer = new string('가', 39) + "😀" + "tail";

        var shadow = ShadowIndex.Build([Correct("bank", answer, "x")], Tree)["bank"].Single();

        shadow.Label.Should().Be(new string('가', 39) + "😀");
        shadow.Answer.Should().Be(answer);
    }

    [Fact]
    public void The_store_returns_evidence_in_arrival_order_with_the_last_node_of_the_path_as_anchor()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "t.sqlite"));
        Close(store, "t1", "support", "fallback", "card_lost", [Step("root", "bank"), Step("bank", null)]);
        store.RecordFeedback("t1", "correct", null);
        Close(store, "t2", "support", "fallback", "unreviewed", [Step("root", "bank")]);
        Close(store, "t3", "other", "fallback", "elsewhere", [Step("root", "bank")]);
        store.RecordFeedback("t3", "correct", null);
        Close(store, "t4", "support", "memory", "remembered", []);
        store.RecordFeedback("t4", "correct", null);

        var evidence = store.ShadowEvidence("support");

        evidence.Should().Equal(new ShadowEvidence("state t1", "bank", "fallback", "card_lost", "correct", null, null));
    }

    [Fact]
    public void Builds_the_same_index_as_the_reference_implementation_from_the_same_log()
    {
        // Point GIL_COMPAT_SHADOWS at recorded evidence with the index another implementation built after the first N rows.
        var path = Environment.GetEnvironmentVariable("GIL_COMPAT_SHADOWS");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_SHADOWS is not set");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var fixture = document.RootElement;
        var tree = OntologyYaml.Parse(fixture.GetProperty("tree").GetString()!).Root;
        var exclude = fixture.GetProperty("exclude").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        var evidence = fixture.GetProperty("evidence").EnumerateArray().Select(Evidence).OfType<ShadowEvidence>().ToList();
        var checkpoints = 0;
        foreach (var checkpoint in fixture.GetProperty("checkpoints").EnumerateArray())
        {
            var rows = checkpoint.GetProperty("rows").GetInt32();
            var built = ShadowIndex.Build(evidence.Take(rows), tree, exclude);
            var expected = checkpoint.GetProperty("index").EnumerateObject().ToDictionary(
                a => a.Name,
                a => a.Value.EnumerateArray().Select(c => new Candidate(
                    c.GetProperty("id").GetString()!,
                    c.GetProperty("label").GetString()!,
                    c.GetProperty("description").GetString()!,
                    c.GetProperty("answer").GetString())).ToList());

            built.Keys.Should().BeEquivalentTo(expected.Keys, $"after {rows} rows");
            foreach (var (anchor, shadows) in expected)
            {
                built[anchor].Should().Equal(shadows, $"anchor {anchor} after {rows} rows");
            }

            checkpoints++;
        }

        checkpoints.Should().BePositive();
    }

    private static ShadowEvidence? Evidence(JsonElement row)
    {
        using var steps = JsonDocument.Parse(row.GetProperty("path").GetString()!);
        var count = steps.RootElement.GetArrayLength();
        return count == 0
            ? null
            : new ShadowEvidence(
                row.GetProperty("state").GetString()!,
                steps.RootElement[count - 1].GetProperty("node").GetString()!,
                Text(row, "mode"),
                Text(row, "output"),
                Text(row, "feedback_verdict"),
                Text(row, "feedback_correction"),
                Text(row, "explored_output"));

        static string? Text(JsonElement row, string name) =>
            row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();
    }

    private static ShadowEvidence Correct(string anchor, string answer, string state) => new(state, anchor, "fallback", answer, "correct", null, null);

    private static PathStep Step(string node, string? chosen) =>
        new(node, 1, chosen, 0.9, chosen is null ? "exit" : "accept", new Dictionary<string, double>(), 1);

    private static void Close(SqliteTelemetryStore store, string traceId, string task, string mode, string output, IReadOnlyList<PathStep> path)
    {
        store.OpenTrace(traceId, task, $"state {traceId}");
        store.CloseTrace(traceId, new TraceOutcome { Output = output, Mode = mode, Path = path, Energy = 1 });
    }
}
