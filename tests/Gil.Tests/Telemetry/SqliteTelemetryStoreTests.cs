using System.Text.Json;
using AwesomeAssertions;
using Gil.Telemetry;
using Microsoft.Data.Sqlite;

namespace Gil.Tests.Telemetry;

public sealed class SqliteTelemetryStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("gil-telemetry-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Review_decisions_are_kept_in_order_with_their_notes()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "r.sqlite"), clock: new FixedClock());
        store.RecordReview("support", ReviewKind.Promotion, "promoted-abc", ReviewDecision.Accepted);
        store.RecordReview("support", ReviewKind.Deactivation, "refund", ReviewDecision.Rejected, "still right, just rare");
        store.RecordReview("other", ReviewKind.Differentiation, "billing", ReviewDecision.Accepted);

        var reviews = store.Reviews("support");

        reviews.Select(r => (r.Kind, r.ItemId, r.Decision, r.Note)).Should().Equal(
            (ReviewKind.Promotion, "promoted-abc", ReviewDecision.Accepted, (string?)null),
            (ReviewKind.Deactivation, "refund", ReviewDecision.Rejected, "still right, just rare"));
        reviews[0].At.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, 456, TimeSpan.Zero));
    }

    [Fact]
    public void Disposing_a_store_releases_its_file_and_leaves_other_stores_working()
    {
        var first = Path.Combine(_directory, "first.sqlite");
        using var other = new SqliteTelemetryStore(Path.Combine(_directory, "other.sqlite"));

        using (var store = new SqliteTelemetryStore(first))
        {
            store.OpenTrace("t1", "task", "input");
        }

        File.Delete(first);
        File.Exists(first).Should().BeFalse();
        other.OpenTrace("t2", "task", "input");
        other.Usage("task").Should().NotBeNull();
    }

    [Fact]
    public void A_request_is_opened_then_closed_with_its_path_and_recall()
    {
        var path = Path.Combine(_directory, "t.sqlite");
        using (var store = new SqliteTelemetryStore(path, clock: new FixedClock()))
        {
            store.OpenTrace("t1", "task", "입력 문장", label: "정답");
            store.OpenTrace("t1", "other", "ignored"); // opening again keeps the first row
            store.CloseTrace("t1", CompatibilityFixture.Outcome);
        }

        using var read = Open(path);
        var row = Row(read, "SELECT task, state, label, mode, output, confidence, energy, path, recall, created_at FROM traces");
        row["task"].Should().Be("task");
        row["state"].Should().Be("입력 문장");
        row["label"].Should().Be("정답");
        row["mode"].Should().Be("partial");
        row["created_at"].Should().Be("2026-01-02T03:04:05.123456+00:00");
        ((string)row["path"]!).Should().Contain("\"chosen\":\"cat-배송\"").And.Contain("\"outcome\":\"accept\"");
        using var recall = JsonDocument.Parse((string)row["recall"]!);
        recall.RootElement.GetProperty("source").GetString().Should().Be("t0");
        recall.RootElement.GetProperty("hit").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void A_judgment_call_keeps_the_candidates_as_shown_and_its_distribution()
    {
        var path = Path.Combine(_directory, "c.sqlite");
        using (var store = new SqliteTelemetryStore(path, clock: new FixedClock()))
        {
            store.RecordCall(CompatibilityFixture.Judgment);
        }

        using var read = Open(path);
        var row = Row(read, "SELECT role, layer, candidates, distribution, top_logprobs, outcome, gpu_prompt_ms FROM calls");
        row["role"].Should().Be("judge");
        row["layer"].Should().Be(1L);
        row["outcome"].Should().Be("accept");
        row["gpu_prompt_ms"].Should().Be(12.5);
        using var shown = JsonDocument.Parse((string)row["candidates"]!);
        shown.RootElement[0].GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        shown.RootElement[1].GetProperty("shown_as").GetString().Should().Be("B");
        ((string)row["top_logprobs"]!).Should().Contain("\"token\":\"B\"");
        ((string)row["distribution"]!).Should().Contain("\"cat-배송\"");
    }

    [Fact]
    public void A_run_config_is_written_once_and_the_first_one_wins()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "r.sqlite"));

        store.RecordRunConfig("run", "{\"tau\":0.9}").Should().Be("{\"tau\":0.9}");
        store.RecordRunConfig("run", "{\"tau\":0.5}").Should().Be("{\"tau\":0.9}");
    }

    [Fact]
    public void Feedback_is_recorded_and_a_closed_request_is_found_with_its_recall()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "f.sqlite"));
        store.OpenTrace("open", "task", "not closed yet");
        store.OpenTrace("t1", "task", "입력 문장");
        store.CloseTrace("t1", CompatibilityFixture.Outcome);

        store.RecordFeedback("t1", "wrong", "정정된 답");

        store.FindTrace("open").Should().BeNull();
        store.FindTrace("t1").Should().BeEquivalentTo(new TraceSummary("task", "입력 문장", "partial", "배송 조회 안내", new Recall("t0", 0.71, 0.9, Hit: false)) { Path = CompatibilityFixture.Outcome.Path });
    }

    [Fact]
    public void A_failed_lookup_is_stored_with_its_error_and_without_a_neighbour()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "r.sqlite"));
        store.OpenTrace("t1", "task", "입력");
        var failed = Recall.Failed(0.9, new HttpRequestException("refused"));
        store.CloseTrace("t1", CompatibilityFixture.Outcome with { Recall = failed });

        store.FindTrace("t1")!.Recall.Should().Be(failed);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_directory, "r.sqlite")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT recall FROM traces WHERE trace_id = 't1'";
        command.ExecuteScalar().Should().Be("""{"threshold":0.9,"hit":false,"error":"HttpRequestException: refused"}""");
    }

    [Fact]
    public void The_restricted_mark_survives_reopening_without_it()
    {
        var path = Path.Combine(_directory, "x.sqlite");
        using (var store = new SqliteTelemetryStore(path, restricted: true))
        {
            store.IsRestricted.Should().BeTrue();
        }

        using var reopened = new SqliteTelemetryStore(path);
        reopened.IsRestricted.Should().BeTrue();
    }

    [Fact]
    public void A_path_counts_visits_choices_and_treats_a_deferral_as_an_exit()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "t.sqlite"), clock: new FixedClock());
        store.RecordPath("task", [Step("root", "bank", "accept"), Step("bank", "o-balance", "accept")]);
        store.RecordPath("task", [Step("root", "bank", "accept"), Step("bank", "shadow-1", "defer")]);
        store.RecordPath("task", [Step("root", null, "exit")]);
        store.RecordPath("task", [Step("root", "work", "accept"), Step("work", null, "skip")]);

        store.Visits("task", "root").Should().Be(new NodeVisits(4, 3, 1, new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, 456, TimeSpan.Zero)));
        store.Visits("task", "bank").Should().BeEquivalentTo(new { Hits = 2, Accepts = 1, Exits = 1 });
        store.Visits("task", "work").Should().BeEquivalentTo(new { Hits = 1, Accepts = 0, Exits = 0 }); // a skip is a hit only
        store.Choices("task", "root").Should().BeEquivalentTo(new Dictionary<string, int> { ["bank"] = 2, ["work"] = 1 });
        store.Choices("task", "bank").Should().BeEquivalentTo(new Dictionary<string, int> { ["o-balance"] = 1 }); // not the shadow
        store.Visits("other", "root").Should().Be(new NodeVisits(0, 0, 0, null));
    }

    [Fact]
    public void Outcomes_accumulate_raw_counts_per_scope_and_weights_are_applied_on_read()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "t.sqlite"));
        store.RecordOutcome("task", "o-balance", new HabitCounts(Reinforced: 1));
        store.RecordOutcome("task", "o-balance", new HabitCounts(Reinforced: 1, Penalized: 1));
        store.RecordOutcome("task", "o-balance", new HabitCounts(Missed: 1, Explored: 1, Disputed: 1));
        store.RecordOutcome("other", "o-balance", new HabitCounts(Penalized: 5));

        var counts = store.Reliability("task", "o-balance");
        counts.Should().Be(new HabitCounts(2, 1, 1, 1, 1));
        counts.Score(new ReliabilityWeights(Reinforce: 1, Penalty: 3, PriorStrength: 2)).Should().BeApproximately((1 + 2.0) / (2 + 2 + 3), 1e-12);
        store.Reliability("task", "unknown").Score(new ReliabilityWeights(1, 3, 0)).Should().Be(0.5);
    }

    [Fact]
    public void A_promotion_round_is_recorded_once_and_read_back_in_round_order()
    {
        using var store = new SqliteTelemetryStore(Path.Combine(_directory, "p.sqlite"));
        store.PromotionRound("task", 50).Should().BeNull("the round never ran");

        store.RecordPromotionRound("task", 100, [CompatibilityFixture.Proposal]);
        store.RecordPromotionRound("task", 50, []);

        store.PromotionRound("task", 50).Should().BeEmpty("a round that took nothing is still a round");
        var loaded = store.PromotionRound("task", 100).Should().ContainSingle().Subject;
        loaded.Should().BeEquivalentTo(CompatibilityFixture.Proposal with { Warnings = [] });
        loaded.Habit.Slots.Should().Equal(CompatibilityFixture.Proposal.Habit.Slots);
        store.PromotionHistory("task").Should().ContainSingle().Which.AtIndex.Should().Be(100);
        store.PromotionHistory("other").Should().BeEmpty();
        store.Invoking(s => s.RecordPromotionRound("task", 100, [])).Should().Throw<SqliteException>();
        store.PromotionRound("task", 100).Should().HaveCount(1, "a refused round leaves the recorded one as it was");
    }

    [Fact]
    public void A_promoted_habit_is_stored_with_every_field_by_name_and_the_kind_as_a_word()
    {
        var json = SqliteTelemetryStore.OptionJson(CompatibilityFixture.Proposal.Habit);

        using var document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "kind", "label", "description", "text", "template", "slots", "steps", "origin");
        document.RootElement.GetProperty("kind").GetString().Should().Be("template");
        document.RootElement.GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("slots")[0].EnumerateObject().Select(p => p.Name).Should().Equal("name", "instruction", "fixed");
        json.Should().Contain("접수");
        SqliteTelemetryStore.HabitFromOptionJson("""{"id": "a", "kind": "answer", "label": "l", "description": "d", "text": "t"}""")
            .Should().BeEquivalentTo(new Habit { Id = "a", Kind = HabitKind.Answer, Label = "l", Description = "d", Text = "t" });
    }

    [Fact]
    public void A_request_without_output_keeps_why_and_an_older_file_gains_the_column()
    {
        var path = Path.Combine(_directory, "old.sqlite");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var command = old.CreateCommand();
            command.CommandText = "CREATE TABLE traces (trace_id TEXT PRIMARY KEY, created_at TEXT, task TEXT, state TEXT, mode TEXT, "
                + "path TEXT, output TEXT, confidence REAL, energy REAL, feedback_verdict TEXT, feedback_correction TEXT, "
                + "implicit_signal TEXT, label TEXT, explored_output TEXT, recall TEXT)";
            command.ExecuteNonQuery();
        }

        using (var store = new SqliteTelemetryStore(path))
        {
            store.OpenTrace("t1", "task", "입력");
            store.CloseTrace("t1", CompatibilityFixture.Outcome with { Output = null, Failure = "not one of the listed answers" });
        }

        using var read = Open(path);
        Row(read, "SELECT output, failure FROM traces")["failure"].Should().Be("not one of the listed answers");
    }

    [Fact]
    public void Writes_the_compatibility_fixture()
    {
        // Other implementations read this file to check they understand the layout. Set GIL_COMPAT_FIXTURE to
        // a path to (re)generate it there; otherwise it is written to a temporary directory.
        var target = Environment.GetEnvironmentVariable("GIL_COMPAT_FIXTURE") ?? Path.Combine(_directory, "fixture.sqlite");
        CompatibilityFixture.Write(target);

        using var read = Open(target);
        Row(read, "SELECT COUNT(*) AS n FROM traces")["n"].Should().Be(2L);
        Row(read, "SELECT failure FROM traces WHERE trace_id = 'unmet-0001'")["failure"].Should().Be(CompatibilityFixture.Unmet);
        Row(read, "SELECT COUNT(*) AS n FROM promotions")["n"].Should().Be(1L);
        Row(read, "SELECT COUNT(*) AS n FROM calls")["n"].Should().Be(1L);
        Row(read, "SELECT COUNT(*) AS n FROM habit_reliability")["n"].Should().Be(2L);
    }

    private static PathStep Step(string node, string? chosen, string outcome) =>
        new(node, 1, chosen, 0.9, outcome, new Dictionary<string, double>(), 0);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static Dictionary<string, object?> Row(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i));
    }
}
