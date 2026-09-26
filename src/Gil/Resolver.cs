using System.Diagnostics;
using Gil.Fallback;
using Gil.Habits;
using Gil.Traverse;

namespace Gil;

/// <summary>
/// Resolves requests in the fixed order: memory, then the tree, then a fallback narrowed to what the tree confirmed,
/// then the full fallback — or a hand-off to a person when the task does not allow generation. The output contract
/// is the same whichever path answered; <see cref="Resolution.Mode"/> tells which one did.
/// </summary>
public sealed class Resolver(
    GreedyTraverser traverser,
    FallbackGenerator fallback,
    SlotFiller slots,
    ITelemetrySink? sink = null,
    IMemory? memory = null,
    IHabitStatistics? statistics = null,
    Random? random = null,
    IShadowEvidenceSource? shadowEvidence = null)
{
    private readonly Random _random = random ?? Random.Shared;

    public async Task<Resolution> ResolveAsync(TaskDefinition task, string state, string? traceId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        using var activity = GilDiagnostics.Source.StartActivity("gil.resolve");
        activity?.SetTag("gil.task", task.Name);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var resolution = await ResolveCoreAsync(task, state, traceId ?? Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
            GilDiagnostics.Resolved(activity, task.Name, resolution, MemoryOutcome(task, resolution.Recall), Stopwatch.GetElapsedTime(started));
            return resolution;
        }
        catch (Exception error)
        {
            GilDiagnostics.Failed(activity, error);
            throw;
        }
    }

    private async Task<Resolution> ResolveCoreAsync(TaskDefinition task, string state, string traceId, CancellationToken cancellationToken)
    {
        sink?.OpenTrace(traceId, task.Name, state);

        // 1. Memory. A hit never touches the tree, so it is not evidence for the tree's habits either.
        Recall? recall = null;
        var energy = 0.0;
        if (memory is not null && task.Policy.MemoryThreshold is double threshold)
        {
            MemoryMatch? match = null;
            try
            {
                (match, var cost) = await memory.LookupAsync(task.Name, state, traceId, cancellationToken).ConfigureAwait(false);
                energy += cost;
            }
            catch (Exception error) when (IsDegradable(task, error, cancellationToken))
            {
                recall = Recall.Failed(threshold, error);
            }

            if (match is not null)
            {
                recall = new Recall(match.Source, match.Similarity, threshold, match.Similarity >= threshold);
                if (recall.Hit)
                {
                    return Close(task, new Resolution(match.Answer, "memory", [], null, energy, traceId, recall));
                }
            }
        }

        // 2. The tree, with shadows rebuilt per request: they are a pure function of the feedback so far.
        var shadows = task.Policy.Shadows && shadowEvidence is not null
            ? ShadowIndex.Build(shadowEvidence.ShadowEvidence(task.Name), task.Ontology, task.Policy.NonAnswers)
            : null;
        var traversed = await traverser.TraverseAsync(state, task.Ontology, task.Policy.Thresholds, task.Language, traceId, shadows, cancellationToken).ConfigureAwait(false);
        energy += traversed.Energy;
        var confidence = traversed.Path.Count > 0 ? traversed.Path[^1].P : 0;
        string? output;
        string mode;
        string? explored = null;
        string? failure = null;
        if (traversed.Habit is Habit habit)
        {
            (output, mode, var cost, failure) = await FromHabitAsync(task, habit, state, traceId, cancellationToken).ConfigureAwait(false);
            energy += cost;
            if (output is null && habit.Kind == HabitKind.Template && task.Policy.AllowFallback)
            {
                // The blanks could not be filled: rather than leave the answer empty, generate within the habit's
                // category — what the judgments confirmed before the one that chose the habit.
                var categories = traversed.Confirmed.SkipLast(1).ToList();
                mode = categories.Count > 0 ? "partial" : "fallback";
                (output, cost, failure) = await GenerateAsync(task, state, categories, traceId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (explored, cost) = await ExploreAsync(task, habit, output, state, traceId, cancellationToken).ConfigureAwait(false);
            }

            energy += cost;
        }
        else if (!task.Policy.AllowFallback)
        {
            (output, mode) = (null, "abstain");
        }
        else
        {
            // 3–4. Fallback: narrowed when the tree confirmed a category, otherwise full.
            var confirmed = traversed.Confirmed;
            mode = confirmed.Count > 0 ? "partial" : "fallback";
            (output, var cost, failure) = await GenerateAsync(task, state, confirmed, traceId, cancellationToken).ConfigureAwait(false);
            energy += cost;
        }

        return Close(task, new Resolution(output, mode, traversed.Path, confidence, energy, traceId, recall, output is null ? failure : null), explored);
    }

    /// <summary>
    /// Records a verdict. A request that went through the tree credits or blames the judgments on its path (see
    /// <see cref="HabitAttribution"/>). Memory keeps only confirmed answers: a correct output, or the correction of a
    /// wrong one. A remembered answer that turned out wrong is forgotten.
    /// </summary>
    public async Task FeedbackAsync(TaskDefinition task, string traceId, bool correct, string? correction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (sink is null)
        {
            return;
        }

        sink.RecordFeedback(traceId, correct ? "correct" : "wrong", correction);
        if (sink.FindTrace(traceId) is not TraceSummary trace)
        {
            return;
        }

        if (statistics is not null && trace.Path.Count > 0 && trace.Mode is string mode)
        {
            var credit = HabitAttribution.Attribute(task.Ontology, trace.Path, mode, trace.Output, correct, correction);
            foreach (var item in credit.Reinforce)
            {
                statistics.RecordOutcome(task.Name, item, new HabitCounts(Reinforced: 1));
            }

            if (credit.Penalize is not null)
            {
                statistics.RecordOutcome(task.Name, credit.Penalize, new HabitCounts(Penalized: 1));
            }

            if (credit.Missed is not null)
            {
                statistics.RecordOutcome(task.Name, credit.Missed, new HabitCounts(Missed: 1));
            }
        }

        if (memory is null || task.Policy.MemoryThreshold is null)
        {
            return;
        }

        if (!correct && trace.Mode == "memory" && trace.Recall?.Source is string source)
        {
            memory.Forget(task.Name, source);
        }

        var answer = correct ? trace.Output : correction;
        if (!string.IsNullOrEmpty(answer))
        {
            try
            {
                await memory.RememberAsync(task.Name, traceId, trace.State, answer, traceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (IsDegradable(task, error, cancellationToken))
            {
                // The verdict is recorded above; rebuilding memory from the feedback history restores this answer.
            }
        }
    }

    /// <summary>What memory did for a request: <c>off</c>, <c>hit</c>, <c>miss</c>, or <c>failed</c> (treated as a miss).</summary>
    private string MemoryOutcome(TaskDefinition task, Recall? recall) =>
        memory is null || task.Policy.MemoryThreshold is null ? "off"
        : recall?.Error is not null ? "failed"
        : recall?.Hit == true ? "hit"
        : "miss";

    /// <summary>A memory failure the task treats as a miss — never a cancellation the caller asked for.</summary>
    private static bool IsDegradable(TaskDefinition task, Exception error, CancellationToken cancellationToken) =>
        task.Policy.MemoryFailure == MemoryFailure.Miss && !(error is OperationCanceledException && cancellationToken.IsCancellationRequested);

    /// <summary>
    /// The exploration cross-check (<see cref="TaskPolicy.ExplorationRate"/>): a share of accepted answer habits is
    /// also solved by the full fallback, without the path as context, so the whole answer — the judgments above
    /// included — is checked independently. Only answer habits: a template's or a procedure's output cannot be compared
    /// to the fallback's character for character. The draw is taken only when it can matter, so a task that never
    /// explores consumes no randomness.
    /// </summary>
    private async Task<(string? Output, double Energy)> ExploreAsync(
        TaskDefinition task, Habit habit, string? output, string state, string traceId, CancellationToken cancellationToken)
    {
        var rate = task.Policy.ExplorationRate;
        if (habit.Kind != HabitKind.Answer || rate <= 0 || _random.NextDouble() >= rate)
        {
            return (null, 0);
        }

        var check = await fallback.GenerateAsync(state, task.Contract, task.Language, traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var agreed = check.Output is not null && output is not null && check.Output.Trim() == output.Trim();
        statistics?.RecordOutcome(task.Name, habit.Id, new HabitCounts(Explored: 1, Disputed: agreed ? 0 : 1));
        return (check.Output?.Trim(), check.Energy);
    }

    /// <summary>The habit's output, its mode and cost, and why the output is missing when it is.</summary>
    private async Task<(string? Output, string Mode, double Energy, string? Failure)> FromHabitAsync(TaskDefinition task, Habit habit, string state, string traceId, CancellationToken cancellationToken)
    {
        switch (habit.Kind)
        {
            case HabitKind.Answer:
                return (habit.Text, "habit/answer", 0, null);
            case HabitKind.Template:
                var filled = await slots.FillAsync(state, habit, task.Language, traceId, cancellationToken).ConfigureAwait(false);
                return (filled.Output, "habit/template", filled.Energy, filled.FailedReason);
            default:
                // A procedure is handed to generation as instructions; there is no procedure executor.
                var procedure = $"{state}\n\n{PromptText.Fill(task.Language.Procedure, ("steps", habit.Steps ?? ""))}";
                var generated = await fallback.GenerateAsync(procedure, new TextContract(), task.Language, traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (generated.Output, "habit/procedure", generated.Energy, generated.FailedReason);
        }
    }

    /// <summary>The fallback's output and cost, and the last contract violation when it produced none.</summary>
    private async Task<(string? Output, double Energy, string? Failure)> GenerateAsync(
        TaskDefinition task, string state, IReadOnlyList<string> confirmed, string traceId, CancellationToken cancellationToken)
    {
        var labels = confirmed.Select(id => task.Ontology.Find(id)?.Label ?? id).ToList();
        var scoped = task.Policy.FallbackScope == FallbackScope.Path && confirmed.Count > 0 && task.Contract is IScopableContract scopable
            ? scopable.Scoped(confirmed[^1])
            : null;
        if (scoped is null)
        {
            var result = await fallback.GenerateAsync(state, task.Contract, task.Language, traceId, labels, cancellationToken: cancellationToken).ConfigureAwait(false);
            return (result.Output, result.Energy, result.FailedReason);
        }

        var narrow = await fallback.GenerateAsync(state, scoped, task.Language, traceId, labels, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (narrow.Output is null || narrow.Output != task.Language.OutOfCategory)
        {
            return (narrow.Output, narrow.Energy, narrow.FailedReason);
        }

        // The model says the answer is outside the confirmed category: the judgment above was wrong. Solve in full,
        // without the (wrong) path as context.
        var full = await fallback.GenerateAsync(state, task.Contract, task.Language, traceId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (full.Output, narrow.Energy + full.Energy, full.FailedReason);
    }

    private Resolution Close(TaskDefinition task, Resolution resolution, string? explored = null)
    {
        // Visits are counted per task: the same ids recur across tasks sharing a store.
        if (resolution.Path.Count > 0)
        {
            statistics?.RecordPath(task.Name, resolution.Path);
        }

        sink?.CloseTrace(resolution.TraceId, new TraceOutcome
        {
            Mode = resolution.Mode,
            Output = resolution.Output,
            Confidence = resolution.Confidence,
            Energy = resolution.Energy,
            Path = resolution.Path,
            Recall = resolution.Recall,
            ExploredOutput = explored,
            Failure = resolution.Failure,
        });
        return resolution;
    }
}
