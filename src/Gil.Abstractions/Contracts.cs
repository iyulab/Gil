namespace Gil;

/// <summary>
/// The shape every answer must have, whichever path produced it. A generated answer that breaks it is regenerated
/// with the reason; one that never meets it is returned as no output rather than a broken one.
/// </summary>
public interface IOutputContract
{
    /// <summary>What the generator is told about the expected output, in the task's language.</summary>
    string Instruction(PromptLanguage language);

    /// <summary>Why <paramref name="text"/> breaks the contract, in the task's language (it is fed back to the model), or null when it meets it.</summary>
    string? Validate(string text, PromptLanguage language);
}

/// <summary>A contract that can be narrowed to the part of the tree below a node.</summary>
public interface IScopableContract
{
    /// <summary>
    /// The contract narrowed to a confirmed category, or null when there is nothing to narrow to. The category itself
    /// may be wrong — the judgment above it can err — so the narrowed contract also accepts the language's
    /// <see cref="PromptLanguage.OutOfCategory"/>, and the caller then solves again under the full contract.
    /// </summary>
    IOutputContract? Scoped(string nodeId);
}
