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
public sealed class SqliteTelemetryStore : ITelemetrySink, IHabitStatistics, IShadowEvidenceSource, IDisposable
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

        CREATE TABLE IF NOT EXISTS node_stats (
            scope        TEXT NOT NULL DEFAULT '',
            node_id      TEXT NOT NULL,
            hits         INTEGER NOT NULL DEFAULT 0,
            accepts      INTEGER NOT NULL DEFAULT 0,
            exits        INTEGER NOT NULL DEFAULT 0,
            last_used_at TEXT,
            PRIMARY KEY (scope, node_id)
        );

        CREATE TABLE IF NOT EXISTS node_choices (
            scope     TEXT NOT NULL DEFAULT '',
            node_id   TEXT NOT NULL,
            chosen_id TEXT NOT NULL,
            accepts   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (scope, node_id, chosen_id)
        );

        CREATE TABLE IF NOT EXISTS habit_reliability (
            scope      TEXT NOT NULL DEFAULT '',
            item_id    TEXT NOT NULL,
            reinforced INTEGER NOT NULL DEFAULT 0,
            penalized  INTEGER NOT NULL DEFAULT 0,
            missed     INTEGER NOT NULL DEFAULT 0,
            explored   INTEGER NOT NULL DEFAULT 0,
            disputed   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (scope, item_id)
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
    private SqliteTransaction? _transaction;

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
                + "path = $path, recall = $recall, explored_output = $explored WHERE trace_id = $id",
            ("$mode", outcome.Mode),
            ("$output", outcome.Output),
            ("$confidence", outcome.Confidence),
            ("$energy", outcome.Energy),
            ("$path", JsonSerializer.Serialize(outcome.Path, Json)),
            ("$recall", outcome.Recall is null ? null : JsonSerializer.Serialize(outcome.Recall, Json)),
            ("$explored", outcome.ExploredOutput),
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
        using var command = Command("SELECT task, state, mode, output, recall, path FROM traces WHERE trace_id = $id", [("$id", traceId)]);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2))
        {
            return null;
        }

        var recall = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Recall>(reader.GetString(4), Json);
        var path = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<List<PathStep>>(reader.GetString(5), Json);
        return new TraceSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), recall)
        {
            Path = path ?? [],
        };
    }

    public IReadOnlyList<ShadowEvidence> ShadowEvidence(string task)
    {
        using var command = Command(
            "SELECT state, path, mode, output, feedback_verdict, feedback_correction, explored_output FROM traces "
            + "WHERE task = $task AND path IS NOT NULL AND (feedback_verdict IS NOT NULL OR explored_output IS NOT NULL) "
            + "ORDER BY created_at, trace_id",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var evidence = new List<ShadowEvidence>();
        while (reader.Read())
        {
            // Only the anchor matters, and a path recorded by another implementation may carry more fields per step.
            using var path = JsonDocument.Parse(reader.GetString(1));
            if (path.RootElement.GetArrayLength() == 0)
            {
                continue;
            }

            evidence.Add(new ShadowEvidence(
                reader.GetString(0),
                path.RootElement[path.RootElement.GetArrayLength() - 1].GetProperty("node").GetString()!,
                Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6)));
        }

        return evidence;

        static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public void RecordPath(string scope, IReadOnlyList<PathStep> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var at = Now();
        InTransaction(() =>
        {
            foreach (var step in path)
            {
                var accept = step.Outcome == "accept";
                var exit = step.Outcome is "exit" or "defer";
                Execute(
                    "INSERT INTO node_stats (scope, node_id, hits, accepts, exits, last_used_at) VALUES ($scope, $node, 1, $accept, $exit, $at) "
                        + "ON CONFLICT (scope, node_id) DO UPDATE SET hits = hits + 1, accepts = accepts + excluded.accepts, "
                        + "exits = exits + excluded.exits, last_used_at = excluded.last_used_at",
                    ("$scope", scope), ("$node", step.Node), ("$accept", accept ? 1 : 0), ("$exit", exit ? 1 : 0), ("$at", at));
                if (accept && step.Chosen is not null)
                {
                    Execute(
                        "INSERT INTO node_choices (scope, node_id, chosen_id, accepts) VALUES ($scope, $node, $chosen, 1) "
                            + "ON CONFLICT (scope, node_id, chosen_id) DO UPDATE SET accepts = accepts + 1",
                        ("$scope", scope), ("$node", step.Node), ("$chosen", step.Chosen));
                }
            }
        });
    }

    public void RecordOutcome(string scope, string itemId, HabitCounts delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Execute(
            "INSERT INTO habit_reliability (scope, item_id, reinforced, penalized, missed, explored, disputed) "
                + "VALUES ($scope, $item, $r, $p, $m, $e, $d) ON CONFLICT (scope, item_id) DO UPDATE SET "
                + "reinforced = reinforced + excluded.reinforced, penalized = penalized + excluded.penalized, "
                + "missed = missed + excluded.missed, explored = explored + excluded.explored, disputed = disputed + excluded.disputed",
            ("$scope", scope), ("$item", itemId), ("$r", delta.Reinforced), ("$p", delta.Penalized), ("$m", delta.Missed),
            ("$e", delta.Explored), ("$d", delta.Disputed));
    }

    public NodeVisits Visits(string scope, string nodeId)
    {
        using var command = Command(
            "SELECT hits, accepts, exits, last_used_at FROM node_stats WHERE scope = $scope AND node_id = $node",
            [("$scope", scope), ("$node", nodeId)]);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new NodeVisits(0, 0, 0, null);
        }

        DateTimeOffset? lastUsed = reader.IsDBNull(3)
            ? null
            : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return new NodeVisits(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), lastUsed);
    }

    public IReadOnlyDictionary<string, int> Choices(string scope, string nodeId)
    {
        using var command = Command(
            "SELECT chosen_id, accepts FROM node_choices WHERE scope = $scope AND node_id = $node",
            [("$scope", scope), ("$node", nodeId)]);
        using var reader = command.ExecuteReader();
        var choices = new Dictionary<string, int>();
        while (reader.Read())
        {
            choices[reader.GetString(0)] = reader.GetInt32(1);
        }

        return choices;
    }

    public HabitCounts Reliability(string scope, string itemId)
    {
        using var command = Command(
            "SELECT reinforced, penalized, missed, explored, disputed FROM habit_reliability WHERE scope = $scope AND item_id = $item",
            [("$scope", scope), ("$item", itemId)]);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new HabitCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4))
            : new HabitCounts();
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

    private void InTransaction(Action work)
    {
        using var transaction = _connection.BeginTransaction();
        _transaction = transaction;
        try
        {
            work();
            transaction.Commit();
        }
        finally
        {
            _transaction = null;
        }
    }

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
        command.Transaction = _transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
