namespace Gil;

/// <summary>
/// Raw outcome counts for one judged item — a child node or a habit, which identifies the judgment that picked it,
/// since every item has exactly one parent. Only counts are stored; the weights are applied by whoever reads them,
/// so they can be re-tuned without re-running anything.
/// </summary>
/// <param name="Reinforced">Times the item was on the path of a confirmed answer.</param>
/// <param name="Penalized">Times it was the first wrong judgment on the path of a wrong answer.</param>
/// <param name="Missed">Times the fallback produced this habit's answer after the tree failed to reach it.</param>
/// <param name="Explored">Times the habit's answer was cross-checked by an independent fallback.</param>
/// <param name="Disputed">Times that cross-check disagreed. Evidence about the pair, not a penalty: the fallback errs too.</param>
public sealed record HabitCounts(int Reinforced = 0, int Penalized = 0, int Missed = 0, int Explored = 0, int Disputed = 0)
{
    /// <summary>
    /// Reliability in [0, 1]: confirmations against penalties, pulled towards 0.5 by a prior while evidence is thin.
    /// Only confirmations and penalties count; a miss says the description failed, not that the habit is wrong.
    /// </summary>
    public double Score(ReliabilityWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        var good = weights.Reinforce * Reinforced;
        var bad = weights.Penalty * Penalized;
        var total = weights.PriorStrength + good + bad;
        return total > 0 ? (weights.PriorStrength / 2 + good) / total : 0.5;
    }
}

/// <summary>
/// How outcomes weigh. The penalty should exceed the reinforcement: a wrong habit produces a wrong answer, while
/// the fallback it would otherwise have taken only costs time.
/// </summary>
/// <param name="Reinforce">Weight of one confirmation.</param>
/// <param name="Penalty">Weight of one penalty.</param>
/// <param name="PriorStrength">Virtual observations pulling the score towards 0.5 before evidence arrives.</param>
public sealed record ReliabilityWeights(double Reinforce, double Penalty, double PriorStrength);

/// <summary>How often a node was visited and how its judgments ended. A skip is a hit that is neither.</summary>
public sealed record NodeVisits(int Hits, int Accepts, int Exits, DateTimeOffset? LastUsedAt);

/// <summary>
/// Runtime statistics of a tree, kept apart from the tree itself so that the authored file does not change on every
/// request. Statistics belong to a scope — one task — because the same ids recur across tasks sharing a store.
/// </summary>
public interface IHabitStatistics
{
    /// <summary>
    /// Counts one visit per step: an accept also counts the choice made, a deferral to the fallback counts as an
    /// exit, and a skip counts only as a hit.
    /// </summary>
    void RecordPath(string scope, IReadOnlyList<PathStep> path);

    /// <summary>Adds <paramref name="delta"/> to the item's counts.</summary>
    void RecordOutcome(string scope, string itemId, HabitCounts delta);

    NodeVisits Visits(string scope, string nodeId);

    /// <summary>Accepts per chosen candidate at a node.</summary>
    IReadOnlyDictionary<string, int> Choices(string scope, string nodeId);

    HabitCounts Reliability(string scope, string itemId);
}
