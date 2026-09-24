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
        store.FindTrace("t1").Should().Be(new TraceSummary("task", "입력 문장", "partial", "배송 조회 안내", new Recall("t0", 0.71, 0.9, Hit: false)));
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
    public void Writes_the_compatibility_fixture()
    {
        // Other implementations read this file to check they understand the layout. Set GIL_COMPAT_FIXTURE to
        // a path to (re)generate it there; otherwise it is written to a temporary directory.
        var target = Environment.GetEnvironmentVariable("GIL_COMPAT_FIXTURE") ?? Path.Combine(_directory, "fixture.sqlite");
        CompatibilityFixture.Write(target);

        using var read = Open(target);
        Row(read, "SELECT COUNT(*) AS n FROM traces")["n"].Should().Be(1L);
        Row(read, "SELECT COUNT(*) AS n FROM calls")["n"].Should().Be(1L);
    }

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
