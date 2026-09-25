namespace Gil.Habits;

/// <summary>When a habit is proposed for deactivation.</summary>
/// <param name="HalfLife">Requests without use after which recency halves.</param>
/// <param name="StaleBelow">Recency below this is stale.</param>
/// <param name="ReliabilityBelow">A reliability score below this is unreliable…</param>
/// <param name="MinVerdicts">…once confirmations and penalties add up to at least this, so no verdict comes without evidence.</param>
/// <param name="DisputeRate">Disputed over explored at or above this is disputed…</param>
/// <param name="MinExplored">…once at least this many cross-checks were made.</param>
public sealed record DeactivationPolicy(
    double HalfLife,
    double StaleBelow,
    double ReliabilityBelow,
    int MinVerdicts,
    double DisputeRate,
    int MinExplored);

/// <summary>Why a habit is proposed for deactivation. The reasons call for different remedies, so they are never merged into one score.</summary>
public enum DeactivationReason
{
    /// <summary>Evidence that it answers wrongly has built up: fix it or retire it.</summary>
    Unreliable,

    /// <summary>The exploration cross-check often disagrees with it. The fallback errs too, so this is a reason to look, not a penalty.</summary>
    Disputed,

    /// <summary>Unused for long: forgetting, not a defect — it may go or stay.</summary>
    Stale,
}

/// <summary>A habit proposed for deactivation, for a person to decide.</summary>
public sealed record DeactivationProposal(string HabitId, string Anchor, DeactivationReason Reason, double Recency, HabitCounts Counts, double Score);

/// <summary>
/// Proposes habits to deactivate. Only proposes: a person applies the change in the tree's authored YAML.
/// </summary>
public static class Deactivation
{
    /// <summary>
    /// One proposal per habit, for its heaviest reason: unreliable, then disputed, then stale; within a reason, the
    /// least reliable and then the least recent first. Recency is <c>0.5^(Δ / halfLife)</c> over the requests since the
    /// habit last answered; a habit never used is measured from the task's start, so a generous half-life keeps a
    /// fresh promotion from going stale at once.
    /// </summary>
    public static IReadOnlyList<DeactivationProposal> Propose(
        Node root,
        IHabitStatistics statistics,
        string scope,
        HabitUsage usage,
        DeactivationPolicy policy,
        ReliabilityWeights weights)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(policy);
        var proposals = new List<DeactivationProposal>();
        foreach (var node in root.Walk())
        {
            foreach (var habit in node.Habits)
            {
                var counts = statistics.Reliability(scope, habit.Id);
                var score = counts.Score(weights);
                var since = usage.Total - 1 - usage.LastIndex.GetValueOrDefault(habit.Id, -1);
                var recency = policy.HalfLife > 0 ? Math.Pow(0.5, since / policy.HalfLife) : 1.0;
                DeactivationReason? reason =
                    counts.Reinforced + counts.Penalized >= policy.MinVerdicts && score < policy.ReliabilityBelow ? DeactivationReason.Unreliable
                    : counts.Explored >= policy.MinExplored && (double)counts.Disputed / counts.Explored >= policy.DisputeRate ? DeactivationReason.Disputed
                    : recency < policy.StaleBelow ? DeactivationReason.Stale
                    : null;
                if (reason is { } found)
                {
                    proposals.Add(new DeactivationProposal(habit.Id, node.Id, found, recency, counts, score));
                }
            }
        }

        return [.. proposals.OrderBy(p => p.Reason).ThenBy(p => p.Score).ThenBy(p => p.Recency)];
    }
}
