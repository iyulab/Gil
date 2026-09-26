namespace Gil;

/// <summary>Something a judgment can choose: a child category or a habit.</summary>
/// <param name="Id">Node or habit id.</param>
/// <param name="Label">Name shown to the judge.</param>
/// <param name="Description">Description shown to the judge — the main lever on judgment accuracy.</param>
/// <param name="Answer">The output choosing it would produce (answer habits); never used to judge, kept in the record.</param>
public sealed record Candidate(string Id, string Label, string Description, string? Answer = null);

/// <summary>A pairwise scorer's verdict over the candidates it was shown.</summary>
public sealed record Judgment
{
    /// <summary>Candidate id to probability, renormalized over the labels the judge could answer with.</summary>
    public required IReadOnlyDictionary<string, double> Probs { get; init; }

    /// <summary>The chosen candidate; null for "none of these" or when the judgment is not trusted.</summary>
    public string? Choice { get; init; }

    /// <summary>Probability of the top label (which may be "none of these").</summary>
    public required double Confidence { get; init; }

    public required double NoneProb { get; init; }

    /// <summary>Total probability the model put on valid labels before renormalizing; low means it wanted to say something else.</summary>
    public required double LabelMass { get; init; }

    public required bool Trusted { get; init; }

    public required CallRecord Call { get; init; }
}

/// <summary>
/// Scores one input against a set of candidates that changes from call to call — no fixed label head, so adding a
/// habit never requires retraining.
/// </summary>
public interface IJudge
{
    Task<Judgment> JudgeAsync(
        string state,
        IReadOnlyList<Candidate> candidates,
        PromptLanguage language,
        string traceId,
        string? nodeId = null,
        int? layer = null,
        CancellationToken cancellationToken = default);
}
