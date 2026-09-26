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
public sealed record MemoryReplay(IReadOnlyList<ConfirmedAnswer> Remember, IReadOnlyCollection<string> Forget)
{
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
    public async Task<double> ApplyAsync(IMemory memory, string task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        foreach (var key in Forget)
        {
            memory.Forget(task, key);
        }

        var energy = 0.0;
        foreach (var answer in Remember)
        {
            energy += await memory.RememberAsync(task, answer.Key, answer.State, answer.Answer, cancellationToken).ConfigureAwait(false);
        }

        return energy;
    }
}
