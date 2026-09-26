namespace Gil;

/// <summary>Where the fallback's contract applies after the tree confirmed some categories.</summary>
public enum FallbackScope
{
    /// <summary>Always the full contract.</summary>
    Full,

    /// <summary>The contract narrowed to the deepest confirmed category, retried in full if the model escapes.</summary>
    Path,
}

/// <summary>
/// Acceptance thresholds. Inner layers ask "is the input in this category"; the leaf asks "is this habit a fitting
/// answer" — different questions, so they get separate thresholds.
/// </summary>
/// <param name="PerLayer">Per inner layer, from the root; deeper layers reuse the last value.</param>
/// <param name="Leaf">For choosing a habit.</param>
public sealed record Thresholds(IReadOnlyList<double> PerLayer, double Leaf)
{
    public double For(int layer, bool isLeaf)
    {
        if (isLeaf)
        {
            return Leaf;
        }

        ArgumentNullException.ThrowIfNull(PerLayer);
        return PerLayer[Math.Min(layer - 1, PerLayer.Count - 1)];
    }
}

/// <summary>What a failing memory does; see <see cref="TaskPolicy.MemoryFailure"/>.</summary>
public enum MemoryFailure
{
    /// <summary>Treat the failure as a miss and record it.</summary>
    Miss,

    /// <summary>Propagate the exception.</summary>
    Throw,
}

/// <summary>How a task resolves requests.</summary>
public sealed record TaskPolicy
{
    /// <summary>
    /// When the tree accepts a judgment. There is no default, for two reasons. A judgment's probability is not a
    /// calibrated accuracy — its scale depends on the judging model. And the right threshold depends on the task: a
    /// layer should accept whenever the answer it leads to is expected to beat what exiting leads to, so a task whose
    /// full fallback is weak should accept far lower than one whose full fallback is strong.
    /// </summary>
    public required Thresholds Thresholds { get; init; }

    /// <summary>When false, requests the tree cannot settle are handed to a person (abstain) instead of generated.</summary>
    public bool AllowFallback { get; init; } = true;

    public FallbackScope FallbackScope { get; init; } = FallbackScope.Full;

    /// <summary>
    /// Similarity at or above which a remembered answer is returned. There is no default: similarity scales differ
    /// between embedding models. Null skips the memory stage and never updates memory.
    /// </summary>
    public double? MemoryThreshold { get; init; }

    /// <summary>
    /// What a failing memory does. Memory is an optional cache in front of the tree, so by default a failed lookup is
    /// a miss — the request goes on to the tree and the error is kept in the trace's <see cref="Recall"/> — and a
    /// failed write after feedback is dropped: the verdict is already recorded, and rebuilding memory from the
    /// feedback history restores the answer. <see cref="MemoryFailure.Throw"/> propagates both instead.
    /// </summary>
    public MemoryFailure MemoryFailure { get; init; } = MemoryFailure.Miss;

    /// <summary>
    /// Share of accepted answer habits that are also solved by the full fallback, as an independent cross-check. A
    /// reinforced habit gets picked more and so loses chances to be checked; this keeps checking it without human
    /// feedback. A disagreement is recorded as evidence about the pair, never as a penalty — the fallback errs too.
    /// Zero turns it off.
    /// </summary>
    public double ExplorationRate { get; init; }

    /// <summary>
    /// Shows answers already known at a node, but not habits yet, beside its habits (see <see cref="IShadowEvidenceSource"/>):
    /// a judgment that picks one defers to the fallback instead of letting a sibling that shares its topic absorb the request.
    /// </summary>
    public bool Shadows { get; init; }

    /// <summary>
    /// Outputs that mean there is no answer (such as "not applicable"). As habits or shadows they would duplicate the
    /// judgment's own "none of these".
    /// </summary>
    public IReadOnlySet<string> NonAnswers { get; init; } = new HashSet<string>();
}

/// <summary>A task: its output contract, its decision tree, its policy and the language its tree is written in.</summary>
/// <param name="Name">Scopes memory, statistics and telemetry.</param>
/// <param name="Contract">The shape every answer must have.</param>
/// <param name="Ontology">The decision tree.</param>
/// <param name="Policy">How requests are resolved.</param>
/// <param name="Language">
/// The wording every model call for this task is made in. There is no default: "none of these" must be in the same
/// language as the tree's candidates for out-of-scope input to be rejected, and only the task knows its tree's language.
/// </param>
public sealed record TaskDefinition(string Name, IOutputContract Contract, Node Ontology, TaskPolicy Policy, PromptLanguage Language);

/// <summary>The nearest remembered request and the answer it confirmed.</summary>
public sealed record MemoryMatch(string Source, double Similarity, string Answer);

/// <summary>
/// Answers confirmed by feedback, found by similarity. It is an index derived from the request log, not a store of its
/// own: only confirmed answers enter it, and a remembered answer that turns out wrong is forgotten.
/// </summary>
public interface IMemory
{
    /// <summary>The nearest remembered request (null when memory is empty) and the energy the lookup cost.</summary>
    Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default);

    /// <summary>Adds a confirmed answer; returns the energy it cost.</summary>
    Task<double> RememberAsync(string task, string traceId, string state, string answer, CancellationToken cancellationToken = default);

    void Forget(string task, string traceId);
}

/// <summary>What a request resolved to. <see cref="Mode"/> tells where the answer came from; the contract is the same either way.</summary>
/// <param name="Output">The answer; null when the contract was never met, or on abstain.</param>
/// <param name="Mode">memory, habit/answer, habit/template, habit/procedure, partial, fallback or abstain.</param>
/// <param name="Path">The tree steps taken; empty for memory answers.</param>
/// <param name="Confidence">Null for memory answers — a similarity is not a judgment probability.</param>
/// <param name="Energy">Total cost of every call made for this request.</param>
/// <param name="TraceId">The request's id; feedback refers to it.</param>
/// <param name="Recall">The nearest remembered request when memory was consulted, hit or miss.</param>
/// <param name="Failure">
/// Why <paramref name="Output"/> is null: the last contract violation, or the blanks a template could not fill, from the
/// step that produced the final result. Null whenever there is an output, and on abstain.
/// </param>
public sealed record Resolution(
    string? Output,
    string Mode,
    IReadOnlyList<PathStep> Path,
    double? Confidence,
    double Energy,
    string TraceId,
    Recall? Recall,
    string? Failure = null);
