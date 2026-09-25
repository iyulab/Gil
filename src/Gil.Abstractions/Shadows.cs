namespace Gil;

/// <summary>
/// A recorded request that can back a shadow: it reached the tree, and it has feedback or an exploration output.
/// </summary>
/// <param name="State">The request's input; the first one behind a shadow becomes its description.</param>
/// <param name="Anchor">The last node on the request's path.</param>
/// <param name="Mode">How the request was answered.</param>
/// <param name="Output">What it answered.</param>
/// <param name="Verdict">Feedback: correct or wrong; null without feedback.</param>
/// <param name="Correction">The right output when the verdict was wrong.</param>
/// <param name="Explored">The exploration cross-check's output, when the request was explored.</param>
public sealed record ShadowEvidence(
    string State,
    string Anchor,
    string? Mode,
    string? Output,
    string? Verdict,
    string? Correction,
    string? Explored);

/// <summary>Where shadows are derived from: a task's recorded requests, in the order they arrived.</summary>
public interface IShadowEvidenceSource
{
    IReadOnlyList<ShadowEvidence> ShadowEvidence(string task);
}
