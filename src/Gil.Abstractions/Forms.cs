namespace Gil;

/// <summary>Whether a field's value is given to the form or is suggested for it.</summary>
public enum FieldRole
{
    /// <summary>Filled by the person or the application; never suggested, only used as evidence for other fields.</summary>
    Observed,

    /// <summary>Suggested, then settled by a person.</summary>
    Judged,
}

/// <summary>How suggestions for a judged field are offered.</summary>
public enum FieldPolicy
{
    /// <summary>Suggest when a layer is confident enough.</summary>
    Suggest,

    /// <summary>
    /// Suggest, but mark the field so that the application never fills it on its own — for fields where a wrong value
    /// that is accepted without a look costs more than typing it.
    /// </summary>
    ConfirmRequired,

    /// <summary>Never suggest — for fields a model or a memory should not decide, such as an assessment of a person.</summary>
    Off,
}

/// <summary>One field of a form.</summary>
/// <param name="Name">Unique within the form; compared ordinally.</param>
/// <param name="Role">Given, or suggested.</param>
public sealed record FieldDefinition(string Name, FieldRole Role)
{
    /// <summary>A closed list of values; when present, a model chooses only among them. Null means the value is open.</summary>
    public IReadOnlyList<string>? Candidates { get; init; }

    public FieldPolicy Policy { get; init; } = FieldPolicy.Suggest;

    /// <summary>
    /// Similarity at or above which a similar settled document's value is suggested. There is no default, for the same
    /// reason as <see cref="TaskPolicy.MemoryThreshold"/>: the scale belongs to the memory in use. Null skips that layer.
    /// </summary>
    public double? MemoryThreshold { get; init; }

    /// <summary>
    /// The fields whose values this field's suggestions may rest on. A hint that removes noisy evidence; null means every
    /// other field that is evidence (see <see cref="UseAsEvidence"/>). Which fields actually matter is learned from settled
    /// documents either way.
    /// </summary>
    public IReadOnlyList<string>? DependsOn { get; init; }

    /// <summary>
    /// False keeps this field's value out of every other field's evidence, in every layer: it is never a key of the settled
    /// field memory, never a line of the request a similar document is looked up by, and never in a model's context. For
    /// fields that identify a person, so that "this person, therefore this outcome" cannot harden into memory. The field's
    /// own value is still recorded and settled as usual.
    /// </summary>
    public bool UseAsEvidence { get; init; } = true;
}

/// <summary>A form: the fields a document of it has, and the language a model reads it in.</summary>
public sealed record FormDefinition
{
    /// <param name="name">Scopes memory, statistics and telemetry, like a task's name.</param>
    /// <param name="fields">At least one judged field; names unique.</param>
    /// <param name="language">The wording model calls for this form are made in.</param>
    /// <exception cref="ArgumentException">
    /// A duplicate field name, no judged field, or a <see cref="FieldDefinition.DependsOn"/> that names an unknown field, the
    /// field itself, or a field that is not evidence.
    /// </exception>
    public FormDefinition(string name, IReadOnlyList<FieldDefinition> fields, PromptLanguage language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(language);

        var byName = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Name, nameof(fields));
            if (!byName.TryAdd(field.Name, field))
            {
                throw new ArgumentException($"Field '{field.Name}' appears more than once.", nameof(fields));
            }
        }

        if (!fields.Any(f => f.Role == FieldRole.Judged))
        {
            throw new ArgumentException("A form needs at least one judged field.", nameof(fields));
        }

        foreach (var field in fields)
        {
            foreach (var dependency in field.DependsOn ?? [])
            {
                if (dependency == field.Name || !byName.TryGetValue(dependency, out var other))
                {
                    throw new ArgumentException($"Field '{field.Name}' depends on '{dependency}', which is not another field of the form.", nameof(fields));
                }

                if (!other.UseAsEvidence)
                {
                    throw new ArgumentException($"Field '{field.Name}' depends on '{dependency}', which is not evidence.", nameof(fields));
                }
            }
        }

        (Name, Fields, Language, _byName) = (name, [.. fields], language, byName);
    }

    private readonly Dictionary<string, FieldDefinition> _byName;

    public string Name { get; }

    public IReadOnlyList<FieldDefinition> Fields { get; }

    public PromptLanguage Language { get; }

    /// <summary>The field with this name.</summary>
    /// <exception cref="ArgumentException">The form has no such field.</exception>
    public FieldDefinition Field(string name) =>
        _byName.TryGetValue(name, out var field) ? field : throw new ArgumentException($"Form '{Name}' has no field '{name}'.", nameof(name));

    /// <summary>
    /// Whether the value of the field named <paramref name="evidence"/> may support suggestions for the field named
    /// <paramref name="field"/>: another field that is evidence and, when the field declares its dependencies, one of them.
    /// </summary>
    /// <exception cref="ArgumentException">Either name is not a field of the form.</exception>
    public bool Supports(string evidence, string field)
    {
        var (source, target) = (Field(evidence), Field(field));
        return source.Name != target.Name
            && source.UseAsEvidence
            && (target.DependsOn is null || target.DependsOn.Contains(source.Name, StringComparer.Ordinal));
    }
}

/// <summary>A document's field values as last settled — observed fields included, since they are the evidence.</summary>
/// <param name="DocumentId">Opaque to the runtime: a path, a key, whatever the application identifies documents by.</param>
/// <param name="Values">Field name to value; a field without a value is absent.</param>
public sealed record SettledDocument(string DocumentId, IReadOnlyDictionary<string, string> Values);

/// <summary>The layer a suggested value came from.</summary>
public enum FieldSource
{
    /// <summary>How often each value was settled alongside the values the document's other fields have.</summary>
    SettledFieldMemory,

    /// <summary>The value a similar settled document has.</summary>
    SimilarDocument,

    Model,

    /// <summary>No layer had a suggestion; a person decides.</summary>
    None,
}

/// <summary>One suggested value.</summary>
/// <param name="Value">The value as it was settled before (or as the model chose it).</param>
/// <param name="Score">On the layer's own scale; scores of different layers are not comparable.</param>
/// <param name="Source">The layer.</param>
/// <param name="Evidence">
/// What the value rests on, for display: the other field value that backs it most (<c>field: value</c>), or the id of the
/// similar document; null when there is nothing specific.
/// </param>
public sealed record FieldCandidate(string Value, double Score, FieldSource Source, string? Evidence);

/// <summary>The suggestion for one judged field.</summary>
/// <param name="Field">The field.</param>
/// <param name="Candidates">Best first, at most the resolver's candidate count; empty when no layer had one and a person decides.</param>
/// <param name="Source">The layer the first candidate came from; <see cref="FieldSource.None"/> when there is none.</param>
/// <param name="Policy">The field's policy, so the application can tell a value it may offer from one it must have confirmed.</param>
/// <param name="Confidence">A judgment probability, from a model only. Null otherwise — a frequency or a similarity is not one.</param>
/// <param name="Elapsed">How long producing the suggestion took, so the application can show that it is waiting.</param>
/// <param name="Energy">Total cost of every call made for it.</param>
/// <param name="TraceId">The suggestion's id in telemetry.</param>
public sealed record FieldSuggestion(
    string Field,
    IReadOnlyList<FieldCandidate> Candidates,
    FieldSource Source,
    FieldPolicy Policy,
    double? Confidence,
    TimeSpan Elapsed,
    double Energy,
    string TraceId);

/// <summary>What happened to a judged field.</summary>
public enum SettlementKind
{
    /// <summary>The suggested value was taken as it was.</summary>
    Accept,

    /// <summary>A value other than the suggested one was settled — the suggestion was wrong.</summary>
    Correct,

    /// <summary>The suggestion was turned down and no value settled.</summary>
    Reject,

    /// <summary>The field's earlier settlement was undone.</summary>
    Revert,

    /// <summary>
    /// A saved value put back when a document is reopened. It is not an outcome of a suggestion, so it records no
    /// acceptance or correction; the value itself is a settled value like any other.
    /// </summary>
    Restore,
}

/// <summary>A settlement of one judged field. Accepting is not the same as being right: settling the field again later corrects it.</summary>
public sealed record Settlement
{
    private Settlement(SettlementKind kind, string? value) => (Kind, Value) = (kind, value);

    public SettlementKind Kind { get; }

    /// <summary>The settled value; null for <see cref="SettlementKind.Reject"/> and <see cref="SettlementKind.Revert"/>.</summary>
    public string? Value { get; }

    public static Settlement Accept(string value) => new(SettlementKind.Accept, Required(value));

    public static Settlement Correct(string value) => new(SettlementKind.Correct, Required(value));

    public static Settlement Reject() => new(SettlementKind.Reject, null);

    public static Settlement Revert() => new(SettlementKind.Revert, null);

    public static Settlement Restore(string value) => new(SettlementKind.Restore, Required(value));

    private static string Required(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
