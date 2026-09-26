namespace Gil;

/// <summary>
/// A request the fallback answered after the tree stopped: the evidence promotion learns from. It is not stored
/// separately — the request log already holds input, path, output and feedback, and a copy would go stale when
/// feedback arrives late.
/// </summary>
/// <param name="TraceId">The request; proposals cite it so a reviewer can read the original.</param>
/// <param name="State">The request's input; the first one behind a proposal becomes its description.</param>
/// <param name="Anchor">Where the tree stopped — the node a promoted habit would hang from.</param>
/// <param name="Output">The fallback's output; null when it never met the contract.</param>
/// <param name="Verdict">Feedback as it stands when read: correct, wrong or null.</param>
/// <param name="Correction">The right output when the verdict was wrong.</param>
/// <param name="FallbackEnergy">The cost of this request's fallback calls alone — what a habit would save.</param>
public sealed record PromotionCandidate(
    string TraceId,
    string State,
    string Anchor,
    string? Output,
    string? Verdict,
    string? Correction,
    double FallbackEnergy);

/// <summary>One judgment as a cost sample: characters the judge read (input plus shown labels and descriptions) and its energy.</summary>
/// <param name="Chars">Input characters plus every shown candidate's label and description.</param>
/// <param name="Energy">The call's cost.</param>
/// <param name="StateChars">Input characters alone.</param>
/// <param name="NoneChars">Characters of the "none of these" candidate.</param>
public sealed record JudgeCostSample(int Chars, double Energy, int StateChars, int NoneChars);

/// <summary>What promotion reads from a task's log.</summary>
public interface IPromotionEvidenceSource
{
    /// <summary>Requests the fallback answered on a non-empty path, in arrival order.</summary>
    IReadOnlyList<PromotionCandidate> PromotionCandidates(string task);

    /// <summary>Mean energy of one judgment per node.</summary>
    IReadOnlyDictionary<string, double> JudgeEnergyByNode(string task);

    /// <summary>Every judgment that recorded the candidates it showed.</summary>
    IReadOnlyList<JudgeCostSample> JudgeCostSamples(string task);
}

/// <summary>A proposed new habit, for a person to review.</summary>
/// <param name="Anchor">The node the habit would hang from.</param>
/// <param name="Habit">The proposed habit.</param>
/// <param name="Sources">The requests behind it, so a reviewer can follow them to the original input and output.</param>
/// <param name="Support">Confirmed fallback answers that converged on this output.</param>
/// <param name="AnchorVolume">Every fallback at this anchor — the denominator of the hit rate.</param>
/// <param name="ExpectedSaving">Expected energy saved per visit to the anchor.</param>
/// <param name="AddedCost">Judgment energy added per visit to the anchor.</param>
/// <param name="Warnings">Loading warnings the new habit raises, such as a description overlapping a sibling's.</param>
public sealed record PromotionProposal(
    string Anchor,
    Habit Habit,
    IReadOnlyList<string> Sources,
    int Support,
    int AnchorVolume,
    double ExpectedSaving,
    double AddedCost,
    IReadOnlyList<string> Warnings)
{
    public double NetSaving => ExpectedSaving - AddedCost;
}

/// <summary>Turns a task's promotion evidence into proposals. A port, so other proposers can run over the same log.</summary>
public interface IPromotionProposer
{
    IReadOnlyList<PromotionProposal> Propose(Node root, IReadOnlyList<PromotionCandidate> candidates);
}

/// <summary>
/// Where promotion rounds are recorded: when the proposer ran and the habits the tree took. A restart or a later review
/// reads the rounds back instead of running the proposer again — a second run would see requests the first did not.
/// The tree at any point is the authored tree with every recorded proposal up to that point applied, in order.
/// </summary>
public interface IPromotionLog
{
    /// <summary>
    /// Records a round. <paramref name="atIndex"/> is the number of the task's requests resolved before the proposer
    /// ran. Pass the proposals applied to the tree (a review lists them as applied); an empty list still
    /// records that the round ran. A round is recorded once — recording the same task and index again throws.
    /// Warnings are not stored.
    /// </summary>
    void RecordPromotionRound(string task, int atIndex, IReadOnlyList<PromotionProposal> applied);

    /// <summary>The proposals recorded for that round, in the order given, or null when the round was never recorded.</summary>
    IReadOnlyList<PromotionProposal>? PromotionRound(string task, int atIndex);

    /// <summary>Every recorded proposal of the task, round by round.</summary>
    IReadOnlyList<(int AtIndex, PromotionProposal Proposal)> PromotionHistory(string task);
}
