using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Gil.Habits;
using Gil.Llm;
using Microsoft.Data.Sqlite;

namespace Gil.Telemetry;

/// <summary>
/// Telemetry in a single SQLite file. Table and column names and meanings are the contract: tools written in
/// any language read these files directly, so columns may be added but never renamed or repurposed.
/// Timestamps are UTC ISO-8601 strings; JSON columns keep non-ASCII text unescaped.
/// </summary>
public sealed class SqliteTelemetryStore : ITelemetrySink, IHabitStatistics, IShadowEvidenceSource, IPromotionEvidenceSource, IPromotionLog, IHabitUsageSource, IDisposable
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
            recall              TEXT,
            failure             TEXT
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

        CREATE TABLE IF NOT EXISTS promotion_rounds (
            task      TEXT NOT NULL,
            at_index  INTEGER NOT NULL,
            PRIMARY KEY (task, at_index)
        );

        CREATE TABLE IF NOT EXISTS promotions (
            task             TEXT NOT NULL,
            at_index         INTEGER NOT NULL,
            anchor           TEXT NOT NULL,
            option_id        TEXT NOT NULL,
            option           TEXT NOT NULL,
            sources          TEXT NOT NULL,
            support          INTEGER NOT NULL,
            anchor_volume    INTEGER NOT NULL,
            expected_saving  REAL NOT NULL,
            added_cost       REAL NOT NULL,
            PRIMARY KEY (task, at_index, option_id)
        );
        """;

    /// <summary>Columns added after the first files were written. They hold NULL until written, so adding them is enough.</summary>
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("traces", "explored_output", "TEXT"),
        ("traces", "recall", "TEXT"),
        ("traces", "failure", "TEXT"),
        ("calls", "candidates", "TEXT"),
    ];

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
        // The store holds one connection for its lifetime; unpooled, so disposing it releases the file without clearing
        // the process-wide pool other stores may be using.
        var source = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30, Pooling = false };
        _connection = new SqliteConnection(source.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        // The stored schema text must not depend on how this file was checked out: other tools compare the files.
        Execute(Schema.ReplaceLineEndings("\n"));
        Migrate();
        if (restricted)
        {
            Execute("INSERT OR REPLACE INTO store_meta (key, value) VALUES ('restricted', '1')");
        }
    }

    /// <summary>Adds the columns an older file lacks, so files written before a column existed open and stay readable.</summary>
    private void Migrate()
    {
        foreach (var table in AddedColumns.Select(c => c.Table).Distinct())
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            using (var command = Command($"PRAGMA table_info({table})", []))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    present.Add(reader.GetString(1));
                }
            }

            foreach (var (_, column, type) in AddedColumns.Where(c => c.Table == table && !present.Contains(c.Column)))
            {
                Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
            }
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
                + "path = $path, recall = $recall, explored_output = $explored, failure = $failure WHERE trace_id = $id",
            ("$mode", outcome.Mode),
            ("$output", outcome.Output),
            ("$confidence", outcome.Confidence),
            ("$energy", outcome.Energy),
            ("$path", JsonSerializer.Serialize(outcome.Path, Json)),
            ("$recall", outcome.Recall is null ? null : JsonSerializer.Serialize(outcome.Recall, Json)),
            ("$explored", outcome.ExploredOutput),
            ("$failure", outcome.Failure),
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
            if (LastNode(reader.GetString(1)) is not { } anchor)
            {
                continue;
            }

            evidence.Add(new ShadowEvidence(
                reader.GetString(0), anchor, Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6)));
        }

        return evidence;
    }

    public IReadOnlyList<PromotionCandidate> PromotionCandidates(string task)
    {
        using var command = Command(
            "SELECT trace_id, state, path, output, feedback_verdict, feedback_correction, "
            + "(SELECT COALESCE(SUM(energy), 0) FROM calls WHERE calls.trace_id = traces.trace_id AND role = 'fallback') "
            + "FROM traces WHERE task = $task AND mode IN ('fallback', 'partial') AND path IS NOT NULL ORDER BY created_at, trace_id",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var candidates = new List<PromotionCandidate>();
        while (reader.Read())
        {
            if (LastNode(reader.GetString(2)) is not { } anchor)
            {
                continue;
            }

            candidates.Add(new PromotionCandidate(
                reader.GetString(0), reader.GetString(1), anchor, Text(reader, 3), Text(reader, 4), Text(reader, 5), reader.GetDouble(6)));
        }

        return candidates;
    }

    public IReadOnlyDictionary<string, double> JudgeEnergyByNode(string task)
    {
        using var command = Command(
            "SELECT calls.node_id, AVG(calls.energy) FROM calls JOIN traces ON traces.trace_id = calls.trace_id "
            + "WHERE traces.task = $task AND calls.role = 'judge' AND calls.node_id IS NOT NULL GROUP BY calls.node_id",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var energy = new Dictionary<string, double>(StringComparer.Ordinal);
        while (reader.Read())
        {
            energy[reader.GetString(0)] = reader.GetDouble(1);
        }

        return energy;
    }

    /// <summary>
    /// Chat calls whose server reported its processing time, with that time (prompt + generation, in ms) as the cost
    /// — the samples <see cref="EnergyModel.Fit"/> turns into GPU-millisecond coefficients for that server. Pass the
    /// model to keep one server's calls apart from another's. Embedding calls are left out: servers report no time for
    /// them.
    /// </summary>
    public IReadOnlyList<CallCostSample> ServerTimeSamples(string? model = null)
    {
        using var command = Command(
            "SELECT prompt_tokens, cached_tokens, completion_tokens, gpu_prompt_ms + COALESCE(gpu_predicted_ms, 0) FROM calls "
            + "WHERE gpu_prompt_ms IS NOT NULL AND role != 'embed' AND ($model IS NULL OR model = $model)",
            [("$model", model)]);
        using var reader = command.ExecuteReader();
        var samples = new List<CallCostSample>();
        while (reader.Read())
        {
            samples.Add(new CallCostSample(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.GetInt32(2), reader.GetDouble(3)));
        }

        return samples;
    }

    public IReadOnlyList<JudgeCostSample> JudgeCostSamples(string task)
    {
        using var command = Command(
            "SELECT calls.energy, calls.candidates, traces.state FROM calls JOIN traces ON traces.trace_id = calls.trace_id "
            + "WHERE traces.task = $task AND calls.role = 'judge' AND calls.candidates IS NOT NULL",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var samples = new List<JudgeCostSample>();
        while (reader.Read())
        {
            var shown = JsonSerializer.Deserialize<List<ShownCandidate>>(reader.GetString(1), Json) ?? [];
            var state = DerivedHabits.Length(Text(reader, 2));
            samples.Add(new JudgeCostSample(
                state + shown.Sum(c => DerivedHabits.Length(c.Label) + DerivedHabits.Length(c.Description)),
                reader.GetDouble(0),
                state,
                shown.Where(c => c.Id is null).Select(c => DerivedHabits.Length(c.Label)).DefaultIfEmpty(0).Max()));
        }

        return samples;
    }

    /// <summary>
    /// The task's observed accuracy per mode, habit and memory rates, and cost per request in windows of
    /// <paramref name="window"/> requests. Costs are the energy recorded with each request; pass
    /// <paramref name="pricing"/> to price every chat call again from its tokens instead — after fitting new
    /// coefficients, so that old and new requests are compared in the same unit. Embedding calls keep their recorded
    /// energy either way.
    /// </summary>
    public TaskStats Stats(string task, int window = 100, EnergyModel? pricing = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        var repriced = pricing is null ? null : Repriced(task, pricing);
        using var command = Command(
"SELECT trace_id, mode, feedback_verdict, energy, json_extract(recall, '$.error') IS NOT NULL FROM traces "
            + "WHERE task = $task AND mode IS NOT NULL ORDER BY created_at, trace_id",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var rows = new List<(string Mode, string? Verdict, double Cost)>();
        var memoryFailures = 0;
        while (reader.Read())
        {
            var cost = repriced is null ? (reader.IsDBNull(3) ? 0 : reader.GetDouble(3)) : repriced.GetValueOrDefault(reader.GetString(0));
            rows.Add((reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), cost));
            memoryFailures += reader.GetBoolean(4) ? 1 : 0;
        }

        var modes = rows
            .GroupBy(r => r.Mode, StringComparer.Ordinal)
            .Select(g =>
            {
                var judged = g.Count(r => r.Verdict is "correct" or "wrong");
                var correct = g.Count(r => r.Verdict == "correct");
                var (low, high) = judged == 0 ? ((double?)null, (double?)null) : Wilson(correct, judged);
                return new ModeStats(g.Key, g.Count(), judged, correct, judged == 0 ? null : (double)correct / judged, low, high);
            })
            .OrderByDescending(m => m.Requests)
            .ThenBy(m => m.Mode, StringComparer.Ordinal)
            .ToList();
        var total = rows.Count;
        var windows = rows.Chunk(window).Select((chunk, i) => new CostWindow(i * window, chunk.Length, chunk.Average(r => r.Cost))).ToList();
        return new TaskStats(
            task,
            total,
            modes.Sum(m => m.Judged),
            modes,
            total == 0 ? 0 : (double)rows.Count(r => r.Mode.StartsWith("habit/", StringComparison.Ordinal)) / total,
            total == 0 ? 0 : (double)rows.Count(r => r.Mode == "memory") / total,
            memoryFailures,
            windows);
    }

    /// <summary>Each request's calls priced again: chat calls with <paramref name="pricing"/>, embedding calls as recorded.</summary>
    private Dictionary<string, double> Repriced(string task, EnergyModel pricing)
    {
        using var command = Command(
            "SELECT calls.trace_id, calls.role, calls.prompt_tokens, calls.cached_tokens, calls.completion_tokens, calls.energy "
            + "FROM calls JOIN traces ON traces.trace_id = calls.trace_id WHERE traces.task = $task",
            [("$task", task)]);
        using var reader = command.ExecuteReader();
        var costs = new Dictionary<string, double>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var cost = reader.GetString(1) == "embed"
                ? (reader.IsDBNull(5) ? 0 : reader.GetDouble(5))
                : pricing.Of(reader.GetInt32(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3), reader.GetInt32(4));
            costs[reader.GetString(0)] = costs.GetValueOrDefault(reader.GetString(0)) + cost;
        }

        return costs;
    }

    /// <summary>The 95% Wilson score interval — sound at small counts and at 0 or 100%, where the normal one is not.</summary>
    private static (double Low, double High) Wilson(int correct, int judged)
    {
        const double z = 1.959963984540054;
        var p = (double)correct / judged;
        var denominator = 1 + (z * z / judged);
        var centre = (p + (z * z / (2 * judged))) / denominator;
        var half = z * Math.Sqrt((p * (1 - p) / judged) + (z * z / (4.0 * judged * judged))) / denominator;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    public HabitUsage Usage(string task)
    {
        using var command = Command(
            "SELECT mode, path FROM traces WHERE task = $task AND mode IS NOT NULL ORDER BY created_at, trace_id", [("$task", task)]);
        using var reader = command.ExecuteReader();
        var last = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = -1;
        while (reader.Read())
        {
            index++;
            if (!reader.GetString(0).StartsWith("habit/", StringComparison.Ordinal) || reader.IsDBNull(1))
            {
                continue;
            }

            using var steps = JsonDocument.Parse(reader.GetString(1));
            var count = steps.RootElement.GetArrayLength();
            if (count == 0)
            {
                continue;
            }

            var step = steps.RootElement[count - 1];
            if (step.GetProperty("outcome").GetString() == "accept" && step.GetProperty("chosen").GetString() is { Length: > 0 } chosen)
            {
                last[chosen] = index;
            }
        }

        return new HabitUsage(last, index + 1);
    }

    /// <summary>The last node of a recorded path — read loosely, since another implementation may record more per step.</summary>
    private static string? LastNode(string path)
    {
        using var steps = JsonDocument.Parse(path);
        var count = steps.RootElement.GetArrayLength();
        return count == 0 ? null : steps.RootElement[count - 1].GetProperty("node").GetString();
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

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

    public void RecordPromotionRound(string task, int atIndex, IReadOnlyList<PromotionProposal> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        InTransaction(() =>
        {
            Execute("INSERT INTO promotion_rounds (task, at_index) VALUES ($task, $at)", ("$task", task), ("$at", atIndex));
            foreach (var proposal in applied)
            {
                Execute(
                    "INSERT INTO promotions (task, at_index, anchor, option_id, option, sources, support, anchor_volume, "
                        + "expected_saving, added_cost) VALUES ($task, $at, $anchor, $id, $option, $sources, $support, $volume, $saving, $cost)",
                    ("$task", task), ("$at", atIndex), ("$anchor", proposal.Anchor), ("$id", proposal.Habit.Id),
                    ("$option", OptionJson(proposal.Habit)), ("$sources", JsonSerializer.Serialize(proposal.Sources, Json)),
                    ("$support", proposal.Support), ("$volume", proposal.AnchorVolume), ("$saving", proposal.ExpectedSaving),
                    ("$cost", proposal.AddedCost));
            }
        });
    }

    public IReadOnlyList<PromotionProposal>? PromotionRound(string task, int atIndex)
    {
        if (Scalar("SELECT 1 FROM promotion_rounds WHERE task = $task AND at_index = $at", ("$task", task), ("$at", atIndex)) is null)
        {
            return null;
        }

        using var command = Command(
            "SELECT anchor, option, sources, support, anchor_volume, expected_saving, added_cost FROM promotions "
                + "WHERE task = $task AND at_index = $at ORDER BY rowid",
            [("$task", task), ("$at", atIndex)]);
        using var reader = command.ExecuteReader();
        var proposals = new List<PromotionProposal>();
        while (reader.Read())
        {
            proposals.Add(new PromotionProposal(
                reader.GetString(0),
                HabitFromOptionJson(reader.GetString(1)),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(2), Json) ?? [],
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                []));
        }

        return proposals;
    }

    public IReadOnlyList<(int AtIndex, PromotionProposal Proposal)> PromotionHistory(string task)
    {
        var rounds = new List<int>();
        using (var command = Command("SELECT at_index FROM promotion_rounds WHERE task = $task ORDER BY at_index", [("$task", task)]))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rounds.Add(reader.GetInt32(0));
            }
        }

        return [.. rounds.SelectMany(at => (PromotionRound(task, at) ?? []).Select(p => (at, p)))];
    }

    /// <summary>
    /// A habit as the <c>promotions.option</c> column holds it: every field, in the order and with the names every
    /// implementation reads — the kind as its YAML word, absent values as null, no slots as an empty list.
    /// </summary>
    internal static string OptionJson(Habit habit)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = Json.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", habit.Id);
            writer.WriteString("kind", habit.Kind.ToString().ToLowerInvariant());
            writer.WriteString("label", habit.Label);
            writer.WriteString("description", habit.Description);
            writer.WriteString("text", habit.Text);
            writer.WriteString("template", habit.Template);
            writer.WriteStartArray("slots");
            foreach (var slot in habit.Slots)
            {
                writer.WriteStartObject();
                writer.WriteString("name", slot.Name);
                writer.WriteString("instruction", slot.Instruction);
                writer.WriteString("fixed", slot.Fixed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("steps", habit.Steps);
            writer.WriteString("origin", habit.Origin);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads <see cref="OptionJson"/>, tolerating fields another implementation leaves out.</summary>
    internal static Habit HabitFromOptionJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? Optional(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new Habit
        {
            Id = root.GetProperty("id").GetString()!,
            Kind = Enum.Parse<HabitKind>(root.GetProperty("kind").GetString()!, ignoreCase: true),
            Label = root.GetProperty("label").GetString()!,
            Description = root.GetProperty("description").GetString()!,
            Text = Optional(root, "text"),
            Template = Optional(root, "template"),
            Slots = root.TryGetProperty("slots", out var slots) && slots.ValueKind == JsonValueKind.Array
                ? [.. slots.EnumerateArray().Select(s => new Slot(s.GetProperty("name").GetString()!, s.GetProperty("instruction").GetString()!, Optional(s, "fixed")))]
                : [],
            Steps = Optional(root, "steps"),
            Origin = Optional(root, "origin") ?? "seed",
        };
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
