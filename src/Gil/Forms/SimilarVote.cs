namespace Gil.Forms;

/// <summary>
/// The similar document layer's vote (<see cref="FieldDefinition.SimilarDocumentVotes"/>), shared by suggestions and by
/// the replays thresholds are chosen on, so the score a threshold is chosen on is the score it is compared with.
/// </summary>
internal static class SimilarVote
{
    /// <summary>
    /// The value the nearest <paramref name="votes"/> documents vote for, each with its similarity (a negative one counts
    /// as none): the nearest document that voted for it, and the vote's margin — the winner's weight less the runner-up's,
    /// over all the weight cast; 0 when none was. A tie goes to the value whose nearest voter is nearer. With a single voter
    /// — one vote, or a memory that holds or ranks only one document (the default <see cref="IMemory.NearestAsync"/>) —
    /// the score is its similarity, so such a memory keeps the scale it had. Null when there are no documents.
    /// </summary>
    /// <param name="nearest">Documents whose value lies in the field's domain, most similar first.</param>
    /// <param name="votes">How many of them vote; at least 1.</param>
    /// <summary>How many of <paramref name="nearest"/> vote when <paramref name="votes"/> may: the fewer of the two.</summary>
    public static int Voters(IReadOnlyList<MemoryMatch> nearest, int votes) => Math.Min(nearest.Count, votes);

    public static (MemoryMatch Match, double Score)? Decide(IReadOnlyList<MemoryMatch> nearest, int votes)
    {
        if (nearest.Count == 0)
        {
            return null;
        }

        if (votes == 1 || nearest.Count == 1)
        {
            return (nearest[0], nearest[0].Similarity);
        }

        var tally = new List<(MemoryMatch First, double Weight)>();
        foreach (var match in nearest.Take(votes))
        {
            var weight = Math.Max(match.Similarity, 0);
            var i = tally.FindIndex(t => string.Equals(t.First.Answer, match.Answer, StringComparison.Ordinal));
            if (i < 0)
            {
                tally.Add((match, weight));
            }
            else
            {
                tally[i] = (tally[i].First, tally[i].Weight + weight);
            }
        }

        // Stable: values enter the tally in order of their nearest voter, so a tie keeps the nearer one first.
        var ranked = tally.OrderByDescending(t => t.Weight).ToList();
        var total = ranked.Sum(t => t.Weight);
        var runnerUp = ranked.Count > 1 ? ranked[1].Weight : 0;
        return (ranked[0].First, total > 0 ? (ranked[0].Weight - runnerUp) / total : 0);
    }
}
