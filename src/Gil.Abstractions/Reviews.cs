namespace Gil;

/// <summary>What a person reviewed: a proposed habit, a habit proposed for retirement, or a node signalled for splitting.</summary>
public enum ReviewKind
{
    /// <summary>A habit proposed by a promotion round.</summary>
    Promotion,

    /// <summary>A habit proposed for retirement.</summary>
    Deactivation,

    /// <summary>A node signalled for splitting or a new category.</summary>
    Differentiation,
}

/// <summary>What the reviewer decided.</summary>
public enum ReviewDecision
{
    /// <summary>Taken into the tree.</summary>
    Accepted,

    /// <summary>Left out, with the reason in the note.</summary>
    Rejected,
}

/// <summary>One review decision.</summary>
/// <param name="At">When it was recorded.</param>
/// <param name="Kind">Which review list the item came from.</param>
/// <param name="ItemId">The proposed habit's id (promotion), the habit's id (deactivation), or the node's id (differentiation).</param>
/// <param name="Decision">Accepted or rejected.</param>
/// <param name="Note">Why, in the reviewer's words; optional.</param>
public sealed record ReviewRecord(DateTimeOffset At, ReviewKind Kind, string ItemId, ReviewDecision Decision, string? Note);

/// <summary>
/// The decisions people make on review lists. Nothing reads them to change behaviour: they are the record of what was
/// accepted and rejected, and of how much reviewing a task costs. An item may be reviewed more than once; every
/// decision is kept.
/// </summary>
public interface IReviewLog
{
    /// <summary>Records one decision on one item of a review list.</summary>
    void RecordReview(string task, ReviewKind kind, string itemId, ReviewDecision decision, string? note = null);

    /// <summary>The task's decisions, oldest first.</summary>
    IReadOnlyList<ReviewRecord> Reviews(string task);
}
