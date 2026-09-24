namespace Gil;

/// <summary>
/// The shape every answer must have, whichever path produced it. A generated answer that breaks it is regenerated
/// with the reason; one that never meets it is returned as no output rather than a broken one.
/// </summary>
public interface IOutputContract
{
    /// <summary>What the generator is told about the expected output.</summary>
    string Instruction();

    /// <summary>Why <paramref name="text"/> breaks the contract, or null when it meets it.</summary>
    string? Validate(string text);
}

/// <summary>
/// A contract narrowed to a confirmed category. The category itself may be wrong — the judgment above it can err —
/// so the generator may answer <see cref="Escape"/> instead, and the caller then solves again under the full contract.
/// </summary>
public sealed record ScopedContract(IOutputContract Contract, string Escape);

/// <summary>A contract that can be narrowed to the part of the tree below a node.</summary>
public interface IScopableContract
{
    /// <summary>The narrowed contract, or null when there is nothing to narrow to.</summary>
    ScopedContract? Scoped(string nodeId);
}
