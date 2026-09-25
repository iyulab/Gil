using System.Text.Json;
using AwesomeAssertions;
using Gil.Fallback;
using Gil.Llm;
using Gil.Ontology;
using Gil.Telemetry;
using Gil.Traverse;
using Microsoft.Data.Sqlite;

namespace Gil.Tests;

public sealed class ResolverReplayTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gil-resolver-replay-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Replays_a_recorded_stream_to_the_same_modes_outputs_paths_and_statistics()
    {
        // A recorded stream (the committed synthetic fixture, or a file GIL_COMPAT_RESOLVER points at): per request the
        // judgments per node, the model's outputs (fallback and slot filling) in the order they were produced and the
        // verdict given; per request what another implementation answered; and the habit statistics it ended with. Only
        // the model is replayed — the walk, the narrowing, the escape to the full fallback, contract checking, slot
        // filling and its fallback when the blanks stay empty, feedback attribution and counting all run for real.
        var path = Conformance.Fixture("GIL_COMPAT_RESOLVER", "resolver.json");
        if (path is null)
        {
            Assert.Skip("GIL_COMPAT_RESOLVER is not set and the committed fixture is missing");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "replay.sqlite"));
        var runs = 0;
        foreach (var run in document.RootElement.GetProperty("runs").EnumerateArray())
        {
            await ReplayAsync(run, store);
            runs++;
        }

        runs.Should().BePositive();
    }

    private async Task ReplayAsync(JsonElement fixture, SqliteTelemetryStore store)
    {
        var name = fixture.GetProperty("task").GetString()!;
        var thresholds = new Thresholds([.. fixture.GetProperty("per_layer").EnumerateArray().Select(v => v.GetDouble())], fixture.GetProperty("leaf").GetDouble());
        var task = new TaskDefinition(
            name,
            new TreeAnswerContract(OntologyYaml.Parse(fixture.GetProperty("contract_tree").GetString()!).Root),
            OntologyYaml.Parse(fixture.GetProperty("tree").GetString()!).Root,
            new TaskPolicy
            {
                Thresholds = thresholds,
                FallbackScope = fixture.GetProperty("fallback_scope").GetString() == "path" ? FallbackScope.Path : FallbackScope.Full,
            });
        var attempts = fixture.GetProperty("fallback_max_attempts").GetInt32();
        var slotAttempts = fixture.TryGetProperty("slot_max_attempts", out var slots) ? slots.GetInt32() : 2;

        var replayed = 0;
        foreach (var @case in fixture.GetProperty("cases").EnumerateArray())
        {
            var traceId = @case.GetProperty("trace_id").GetString()!;
            var model = new RecordedChatModel([.. @case.GetProperty("fallback_outputs").EnumerateArray().Select(o => o.GetString()!)]);
            var recorder = new CallRecorder(model, new EnergyModel(1, 0, 0, 0), store);
            var resolver = new Resolver(
                new GreedyTraverser(new RecordedJudge(@case.GetProperty("judgments"))),
                new FallbackGenerator(recorder, attempts),
                new SlotFiller(recorder, slotAttempts),
                store,
                statistics: store);

            // A request that was interrupted and tried again had its dead attempt's visits counted too.
            store.RecordPath(name, [.. @case.GetProperty("retired_steps").EnumerateArray().Select(Step)]);
            var result = await resolver.ResolveAsync(task, @case.GetProperty("state").GetString()!, traceId, TestContext.Current.CancellationToken);
            await resolver.FeedbackAsync(
                task, traceId, @case.GetProperty("verdict").GetString() == "correct", @case.GetProperty("correction").GetString(), TestContext.Current.CancellationToken);

            (result.Mode, result.Output).Should().Be((@case.GetProperty("mode").GetString()!, @case.GetProperty("output").GetString()), traceId);
            result.Path.Select(s => (s.Node, s.Chosen, s.Outcome)).Should().Equal(
                @case.GetProperty("steps").EnumerateArray().Select(s => { var step = Step(s); return (step.Node, step.Chosen, step.Outcome); }), traceId);
            model.Used.Should().Be(@case.GetProperty("fallback_outputs").GetArrayLength(), traceId);
            replayed++;
        }

        replayed.Should().BePositive();
        var stats = fixture.GetProperty("stats");
        foreach (var row in stats.GetProperty("node_stats").EnumerateArray())
        {
            var node = row.GetProperty("node_id").GetString()!;
            store.Visits(name, node).Should().BeEquivalentTo(
                new { Hits = row.GetProperty("hits").GetInt32(), Accepts = row.GetProperty("accepts").GetInt32(), Exits = row.GetProperty("exits").GetInt32() }, node);
        }

        foreach (var node in stats.GetProperty("node_choices").EnumerateArray().GroupBy(r => r.GetProperty("node_id").GetString()!))
        {
            store.Choices(name, node.Key).Should().BeEquivalentTo(
                node.ToDictionary(r => r.GetProperty("chosen_id").GetString()!, r => r.GetProperty("accepts").GetInt32()), node.Key);
        }

        foreach (var row in stats.GetProperty("habit_reliability").EnumerateArray())
        {
            var item = row.GetProperty("item_id").GetString()!;
            store.Reliability(name, item).Should().Be(
                new HabitCounts(
                    row.GetProperty("reinforced").GetInt32(),
                    row.GetProperty("penalized").GetInt32(),
                    row.GetProperty("missed").GetInt32(),
                    row.GetProperty("explored").GetInt32(),
                    row.GetProperty("disputed").GetInt32()),
                item);
        }

        // And nothing beyond what the other implementation counted.
        using var read = new SqliteConnection($"Data Source={Path.Combine(_directory, "replay.sqlite")}");
        read.Open();
        foreach (var table in new[] { "node_stats", "node_choices", "habit_reliability" })
        {
            using var count = read.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE scope = $scope";
            count.Parameters.AddWithValue("$scope", name);
            ((long)count.ExecuteScalar()!).Should().Be(stats.GetProperty(table).GetArrayLength(), table);
        }
    }

    private static PathStep Step(JsonElement s) =>
        new(s.GetProperty("node").GetString()!, 0, s.GetProperty("chosen").GetString(), 0, s.GetProperty("outcome").GetString()!, new Dictionary<string, double>(), 0);
}
