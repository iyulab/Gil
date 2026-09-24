using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gil.Telemetry;

/// <summary>
/// Telemetry in a single SQLite file. Table and column names and meanings are the contract: tools written in
/// any language read these files directly, so columns may be added but never renamed or repurposed.
/// Timestamps are UTC ISO-8601 strings; JSON columns keep non-ASCII text unescaped.
/// </summary>
public sealed class SqliteTelemetryStore : ITelemetrySink, IDisposable
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS traces (
            trace_id            TEXT PRIMARY KEY,
            created_at          TEXT,
            task                TEXT,
            state               TEXT,
            mode                TEXT,
            path                TEXT,
            output              TEXT,
            confidence          REAL,
            energy              REAL,
            feedback_verdict    TEXT,
            feedback_correction TEXT,
            implicit_signal     TEXT,
            label               TEXT,
            explored_output     TEXT,
            recall              TEXT
        );

        CREATE TABLE IF NOT EXISTS calls (
            call_id           TEXT PRIMARY KEY,
            trace_id          TEXT NOT NULL,
            created_at        TEXT NOT NULL,
            role              TEXT NOT NULL,
            model             TEXT NOT NULL,
            node_id           TEXT,
            layer             INTEGER,
            prompt_tokens     INTEGER NOT NULL,
            cached_tokens     INTEGER NOT NULL,
            completion_tokens INTEGER NOT NULL,
            latency_ms        REAL NOT NULL,
            gpu_prompt_ms     REAL,
            gpu_predicted_ms  REAL,
            content           TEXT,
            first_token       TEXT,
            top_logprobs      TEXT,
            distribution      TEXT,
            label_mass        REAL,
            confidence        REAL,
            outcome           TEXT,
            energy            REAL NOT NULL,
            raw_response      TEXT NOT NULL,
            candidates        TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_calls_trace ON calls (trace_id);

        CREATE TABLE IF NOT EXISTS run_configs (
            task       TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            config     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS store_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;

    /// <param name="path">The database file; created with the schema if it does not exist.</param>
    /// <param name="restricted">
    /// Marks the file as holding data that must not leave its environment. The mark is permanent: reopening
    /// without it keeps it, and export tools refuse marked files.
    /// </param>
    /// <param name="clock">Source of timestamps; the system clock by default.</param>
    public SqliteTelemetryStore(string path, bool restricted = false, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var source = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30 };
        _connection = new SqliteConnection(source.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute(Schema);
        if (restricted)
        {
            Execute("INSERT OR REPLACE INTO store_meta (key, value) VALUES ('restricted', '1')");
        }
    }

    /// <summary>True when the file carries the restricted mark.</summary>
    public bool IsRestricted =>
        Scalar("SELECT value FROM store_meta WHERE key = 'restricted'") is string value && value == "1";

    public void OpenTrace(string traceId, string task, string state, string? label = null) =>
        Execute(
            "INSERT OR IGNORE INTO traces (trace_id, created_at, task, state, label) VALUES ($id, $at, $task, $state, $label)",
            ("$id", traceId), ("$at", Now()), ("$task", task), ("$state", state), ("$label", label));

    public void CloseTrace(string traceId, TraceOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Execute(
            "UPDATE traces SET mode = $mode, output = $output, confidence = $confidence, energy = $energy, "
                + "path = $path, recall = $recall WHERE trace_id = $id",
            ("$mode", outcome.Mode),
            ("$output", outcome.Output),
            ("$confidence", outcome.Confidence),
            ("$energy", outcome.Energy),
            ("$path", JsonSerializer.Serialize(outcome.Path, Json)),
            ("$recall", outcome.Recall is null ? null : JsonSerializer.Serialize(outcome.Recall, Json)),
            ("$id", traceId));
    }

    public void RecordCall(CallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Execute(
            """
            INSERT INTO calls (
                call_id, trace_id, created_at, role, model, node_id, layer,
                prompt_tokens, cached_tokens, completion_tokens, latency_ms, gpu_prompt_ms, gpu_predicted_ms,
                content, first_token, top_logprobs, distribution, label_mass, confidence, outcome, energy,
                raw_response, candidates)
            VALUES ($call, $trace, $at, $role, $model, $node, $layer, $pt, $ct, $dt, $lat, $gp, $gd,
                $content, $first, $top, $dist, $mass, $conf, $outcome, $energy, $raw, $cands)
            """,
            ("$call", record.CallId), ("$trace", record.TraceId), ("$at", Format(record.CreatedAt)), ("$role", record.Role),
            ("$model", record.Model), ("$node", record.NodeId), ("$layer", record.Layer), ("$pt", record.PromptTokens),
            ("$ct", record.CachedTokens), ("$dt", record.CompletionTokens), ("$lat", record.LatencyMs),
            ("$gp", record.GpuPromptMs), ("$gd", record.GpuPredictedMs), ("$content", record.Content),
            ("$first", record.FirstToken), ("$top", JsonSerializer.Serialize(record.TopLogprobs, Json)),
            ("$dist", record.Distribution is null ? null : JsonSerializer.Serialize(record.Distribution, Json)),
            ("$mass", record.LabelMass), ("$conf", record.Confidence), ("$outcome", record.Outcome),
            ("$energy", record.Energy), ("$raw", record.RawResponse),
            ("$cands", record.Candidates is null ? null : JsonSerializer.Serialize(record.Candidates, Json)));
    }

    public string RecordRunConfig(string task, string configJson)
    {
        Execute(
            "INSERT OR IGNORE INTO run_configs (task, created_at, config) VALUES ($task, $at, $config)",
            ("$task", task), ("$at", Now()), ("$config", configJson));
        return (string)Scalar("SELECT config FROM run_configs WHERE task = $task", ("$task", task))!;
    }

    public void RecordFeedback(string traceId, string verdict, string? correction) =>
        Execute(
            "UPDATE traces SET feedback_verdict = $verdict, feedback_correction = $correction WHERE trace_id = $id",
            ("$verdict", verdict), ("$correction", correction), ("$id", traceId));

    public TraceSummary? FindTrace(string traceId)
    {
        using var command = Command("SELECT task, state, mode, output, recall FROM traces WHERE trace_id = $id", [("$id", traceId)]);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2))
        {
            return null;
        }

        var recall = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Recall>(reader.GetString(4), Json);
        return new TraceSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), recall);
    }

    /// <summary>The task's requests that received feedback, oldest first — what a memory index is rebuilt from.</summary>
    public IReadOnlyList<Gil.Memory.FeedbackEntry> FeedbackHistory(string task)
    {
        using var command = Command(
            "SELECT trace_id, state, mode, output, recall, feedback_verdict, feedback_correction FROM traces "
                + "WHERE task = $task AND feedback_verdict IS NOT NULL ORDER BY created_at, rowid",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var entries = new List<Gil.Memory.FeedbackEntry>();
        while (reader.Read())
        {
            entries.Add(new Gil.Memory.FeedbackEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Recall>(reader.GetString(4), Json),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    internal static string Format(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);

    private string Now() => Format(_clock.GetUtcNow());

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
