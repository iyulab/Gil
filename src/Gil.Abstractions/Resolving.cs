namespace Gil;

/// <summary>Where the fallback's contract applies after the tree confirmed some categories.</summary>
public enum FallbackScope
{
    /// <summary>Always the full contract.</summary>
    Full,

    /// <summary>The contract narrowed to the deepest confirmed category, retried in full if the model escapes.</summary>
    Path,
}

/// <summary>How a task resolves requests.</summary>
public sealed record TaskPolicy
{
    /// <summary>When false, requests the tree cannot settle are handed to a person (abstain) instead of generated.</summary>
    public bool AllowFallback { get; init; } = true;

    public FallbackScope FallbackScope { get; init; } = FallbackScope.Full;

    /// <summary>
    /// Similarity at or above which a remembered answer is returned. There is no default: similarity scales differ
    /// between embedding models. Null skips the memory stage and never updates memory.
    /// </summary>
    public double? MemoryThreshold { get; init; }
}

/// <summary>A task: its output contract, its decision tree and its policy.</summary>
public sealed record TaskDefinition(string Name, IOutputContract Contract, Node Ontology, TaskPolicy Policy);

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
public sealed record Resolution(
    string? Output,
    string Mode,
    IReadOnlyList<PathStep> Path,
    double? Confidence,
    double Energy,
    string TraceId,
    Recall? Recall);
