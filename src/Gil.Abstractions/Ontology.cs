namespace Gil;

/// <summary>What a habit produces: a fixed answer, a template with slots, or a procedure to follow.</summary>
public enum HabitKind
{
    Answer,
    Template,
    Procedure,
}

/// <summary>A blank in a template. A slot with a <see cref="Fixed"/> value is not filled from the input.</summary>
public sealed record Slot(string Name, string Instruction, string? Fixed = null);

/// <summary>A concrete candidate under a node: a habit. The YAML calls these <c>options</c>.</summary>
public sealed record Habit
{
    public required string Id { get; init; }
    public required HabitKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }

    /// <summary>Answer habits: the output, verbatim.</summary>
    public string? Text { get; init; }

    /// <summary>Template habits: text with <c>{slot}</c> blanks.</summary>
    public string? Template { get; init; }

    public IReadOnlyList<Slot> Slots { get; init; } = [];

    /// <summary>Procedure habits: the instructions.</summary>
    public string? Steps { get; init; }

    /// <summary>seed (authored) or promoted (learned from repeated answers).</summary>
    public string Origin { get; init; } = "seed";
}

/// <summary>
/// A category in the decision tree. A node asks either which child category applies or which option does —
/// never both. With neither, it has no habits yet and is skipped without a model call.
/// </summary>
public sealed record Node
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<Node> Children { get; init; } = [];
    public IReadOnlyList<Habit> Habits { get; init; } = [];
    public string Origin { get; init; } = "seed";

    /// <summary>concept or habit.</summary>
    public string Kind { get; init; } = "concept";

    /// <summary>digits (up to 9 candidates) or letters (up to 25); inherited by children.</summary>
    public string LabelScheme { get; init; } = "letters";

    /// <summary>The ids of what this node asks about: its children, or else its habits.</summary>
    public IReadOnlyList<string> CandidateIds =>
        Children.Count > 0 ? [.. Children.Select(c => c.Id)] : [.. Habits.Select(h => h.Id)];

    /// <summary>This node and every descendant node, depth first.</summary>
    public IEnumerable<Node> Walk()
    {
        yield return this;
        foreach (var descendant in Children.SelectMany(child => child.Walk()))
        {
            yield return descendant;
        }
    }

    public Node? Find(string nodeId) => Walk().FirstOrDefault(node => node.Id == nodeId);
}
