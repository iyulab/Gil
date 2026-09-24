namespace Gil;

/// <summary>One of the top alternatives the model reported at the first generated token.</summary>
public sealed record TokenLogprob(string Token, double Logprob);

/// <summary>A candidate exactly as a judgment showed it. The first entry is always "none of these" (<see cref="Id"/> is null).</summary>
/// <param name="Id">Node or option id; null for "none of these".</param>
/// <param name="ShownAs">The label symbol the judge was asked to answer with.</param>
/// <param name="Label">The candidate's name as shown.</param>
/// <param name="Description">The candidate's description as shown; null when it had none.</param>
/// <param name="Answer">The output choosing this candidate would produce (answer options and shadows); null for category nodes.</param>
public sealed record ShownCandidate(string? Id, string ShownAs, string Label, string? Description, string? Answer);

/// <summary>One model call. Every call that costs something is recorded, including retried ones.</summary>
public sealed record CallRecord
{
    public required string CallId { get; init; }
    public required string TraceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>embed, judge, fallback, slot_fill, baseline or probe.</summary>
    public required string Role { get; init; }

    public required string Model { get; init; }
    public string? NodeId { get; init; }
    public int? Layer { get; init; }
    public required int PromptTokens { get; init; }
    public required int CachedTokens { get; init; }
    public required int CompletionTokens { get; init; }

    /// <summary>Round trip including queueing.</summary>
    public required double LatencyMs { get; init; }

    /// <summary>Processing time reported by a self-hosted server; null when the server does not report it.</summary>
    public double? GpuPromptMs { get; init; }

    public double? GpuPredictedMs { get; init; }
    public string Content { get; init; } = "";
    public string? FirstToken { get; init; }
    public IReadOnlyList<TokenLogprob> TopLogprobs { get; init; } = [];

    /// <summary>Judgments only: candidate id to normalized probability.</summary>
    public IReadOnlyDictionary<string, double>? Distribution { get; init; }

    public double? LabelMass { get; init; }
    public double? Confidence { get; init; }

    /// <summary>Judgments only: accept or exit.</summary>
    public string? Outcome { get; init; }

    public required double Energy { get; init; }

    /// <summary>The provider response as received, serialized JSON.</summary>
    public string RawResponse { get; init; } = "{}";

    /// <summary>Judgments only: the candidates as shown, in display order.</summary>
    public IReadOnlyList<ShownCandidate>? Candidates { get; init; }
}

/// <summary>One step of a traversal: a judgment at one node.</summary>
/// <param name="Node">The node judged.</param>
/// <param name="Layer">Depth, 1 at the root.</param>
/// <param name="Chosen">The accepted child or option id; the shadow id on defer; null on exit or skip.</param>
/// <param name="P">Confidence of the step.</param>
/// <param name="Outcome">accept, exit, skip or defer.</param>
/// <param name="Probs">Normalized probability per candidate id.</param>
/// <param name="Energy">Energy of the step's call.</param>
public sealed record PathStep(
    string Node,
    int Layer,
    string? Chosen,
    double P,
    string Outcome,
    IReadOnlyDictionary<string, double> Probs,
    double Energy);

/// <summary>The nearest remembered request, kept even on a miss so the threshold can be re-chosen offline.</summary>
public sealed record Recall(string Source, double Similarity, double Threshold, bool Hit);

/// <summary>How a request was finally closed.</summary>
public sealed record TraceOutcome
{
    /// <summary>memory, habit/answer, habit/template, habit/procedure, partial, fallback or abstain.</summary>
    public required string Mode { get; init; }

    /// <summary>Null when the output contract was never met, or on abstain.</summary>
    public string? Output { get; init; }

    public double? Confidence { get; init; }
    public required double Energy { get; init; }
    public IReadOnlyList<PathStep> Path { get; init; } = [];
    public Recall? Recall { get; init; }
}
