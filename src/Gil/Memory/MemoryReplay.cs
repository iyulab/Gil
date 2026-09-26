namespace Gil.Memory;

/// <summary>An answer confirmed by feedback: the output judged correct, or the correction of a wrong one.</summary>
/// <param name="Key">The request's trace id — the key memory stores it under and a later verdict overturns it by.</param>
/// <param name="State">The request as it arrived.</param>
/// <param name="Answer">The confirmed answer.</param>
public sealed record ConfirmedAnswer(string Key, string State, string Answer);

/// <summary>
/// What a task's memory should hold, read from its feedback history: every confirmed answer except those later
/// overturned, and the keys to forget — remembered answers that were served and judged wrong. The same rule the resolver
/// applies one verdict at a time, so any <see cref="IMemory"/> can be rebuilt from the log.
/// </summary>
/// <param name="Remember">The answers to hold, in the order they were confirmed.</param>
/// <param name="Forget">The keys of remembered answers that were served and judged wrong.</param>
public sealed record MemoryReplay(IReadOnlyList<ConfirmedAnswer> Remember, IReadOnlyCollection<string> Forget)
{
    /// <summary>Reads the replay off a task's feedback history, oldest first.</summary>
    public static MemoryReplay From(IReadOnlyList<FeedbackEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var overturned = history
            .Where(e => e.Mode == "memory" && e.Verdict == "wrong" && e.Recall is not null)
            .Select(e => e.Recall!.Source)
            .OfType<string>()
            .ToHashSet();
        var confirmed = history
            .Select(e => (e.TraceId, e.State, Answer: e.Verdict == "correct" ? e.Output : e.Correction))
            .Where(e => !string.IsNullOrEmpty(e.Answer) && !overturned.Contains(e.TraceId))
            .Select(e => new ConfirmedAnswer(e.TraceId, e.State, e.Answer!))
            .ToList();
        return new MemoryReplay(confirmed, overturned);
    }

    /// <summary>
    /// Applies the replay to any memory through its own interface: forgets, then remembers one answer at a time. A
    /// store that keys rows by <see cref="ConfirmedAnswer.Key"/> ends up the same whether or not it held some of them.
    /// Returns the energy the memory reported.
    /// </summary>
    /// <param name="memory">The memory to fill.</param>
    /// <param name="task">The task whose memory it is.</param>
    /// <param name="traceId">What the embedding calls are recorded against — a trace of its own, so the rebuild's cost is
    /// not charged to the requests it replays.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    public async Task<double> ApplyAsync(IMemory memory, string task, string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        foreach (var key in Forget)
        {
            memory.Forget(task, key);
        }

        var energy = 0.0;
        foreach (var answer in Remember)
        {
            energy += await memory.RememberAsync(task, answer.Key, answer.State, answer.Answer, traceId, cancellationToken).ConfigureAwait(false);
        }

        return energy;
    }
}
