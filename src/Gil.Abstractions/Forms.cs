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
    /// <summary>
    /// A closed list of values — the field's domain. When present, no suggestion offers a value outside it: a model chooses
    /// only among them, and a value settled outside it, such as one since dropped from the list, is still remembered but
    /// never suggested. Null means the value is open.
    /// </summary>
    public IReadOnlyList<string>? Candidates { get; init; }

    /// <summary>Whether <paramref name="value"/> lies in the field's domain: always when it is open, else when <see cref="Candidates"/> lists it (ordinally).</summary>
    public bool Admits(string value) => Candidates is null || Candidates.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// Another field of the form whose value in the same document bounds this field's domain — for a field that takes
    /// several values (<see cref="Multiple"/>), the values chosen in it: the main topic among a document's topics, a
    /// subcategory within its category. Once that field has a value, no suggestion offers this field a value outside it,
    /// as with <see cref="Candidates"/>, which still applies as well; while it is empty, the domain is not narrowed. A value
    /// settled outside it is still remembered, and suggested for documents whose domain admits it.
    /// </summary>
    public string? CandidatesFrom { get; init; }

    /// <summary>
    /// Whether <paramref name="value"/> lies in the field's domain for a document with <paramref name="values"/> and
    /// <paramref name="sets"/> so far: in <see cref="Candidates"/> when the field has them, and among the values of
    /// <see cref="CandidatesFrom"/> when that field has any (ordinally).
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="values">The document's single values so far.</param>
    /// <param name="sets">The values chosen so far of the document's fields that take several.</param>
    public bool Admits(string value, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, IReadOnlyList<string>>? sets = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Admits(value))
        {
            return false;
        }

        if (CandidatesFrom is not { } from)
        {
            return true;
        }

        if (sets is not null && sets.TryGetValue(from, out var chosen) && chosen.Count > 0)
        {
            return chosen.Contains(value, StringComparer.Ordinal);
        }

        return !values.TryGetValue(from, out var given) || string.IsNullOrEmpty(given) || string.Equals(given, value, StringComparison.Ordinal);
    }

    public FieldPolicy Policy { get; init; } = FieldPolicy.Suggest;

    /// <summary>
    /// Score at or above which the value the similar settled documents vote for is suggested — the score of
    /// <see cref="SimilarDocumentVotes"/>: the vote's margin, or with a single voter the nearest document's similarity.
    /// There is no default, for the same reason as <see cref="TaskPolicy.MemoryThreshold"/>: the scale belongs to the
    /// memory in use and to the vote. A threshold chosen before 0.16.0 measured the nearest document's similarity and must
    /// be chosen again (<c>ThresholdSelection.SelectAsync</c>, <c>SelectLayersAsync</c>). Null makes no promise: with a
    /// document memory the layer still looks, reports <see cref="FieldSuggestion.SimilarDocuments"/>, and offers the
    /// value voted for as a guess, as it does below a threshold.
    /// </summary>
    public double? MemoryThreshold { get; init; }

    /// <summary>
    /// The similarity the nearest document voting for the similar document layer's value must also reach for the value to
    /// be trusted — set it together with <see cref="MemoryThreshold"/>, from the same choice
    /// (<c>ThresholdChoice.SimilarityFloor</c>). A threshold promises its precision for drafts like the ones the replay
    /// answered; the vote's score measures how far the neighbours agree, not how closely they resemble the draft, so a draft
    /// written in words no settled document uses can still find weak neighbours agreeing on a value. The floor is the lower
    /// end of how similar the documents behind the replay's answers were: below it the promise was never measured and the
    /// value stays a guess. Null sets no floor. A replay with a single voter leaves it null: the score is then the
    /// similarity itself.
    /// </summary>
    public double? MemorySimilarityFloor { get; init; }

    /// <summary>
    /// How many of the most similar settled documents vote on the similar document layer's value. Each votes for its value
    /// with its similarity; the value with the most weight is the layer's candidate, its evidence the nearest document that
    /// voted for it. Its score is the vote's margin — the winner's weight less the runner-up's, over all the weight cast —
    /// from 0 (a tie) to 1 (every voter agrees), so a lone near document that its neighbours contradict is not trusted on
    /// its similarity alone. Only documents whose value lies in the field's domain vote. With 1 — or a memory that ranks
    /// only its nearest document — the nearest such document decides alone and the score is its similarity. Ten by default: in replays of settled documents across four forms,
    /// ten voters answered more often than the nearest document alone at a higher realized precision. Choose
    /// <see cref="MemoryThreshold"/> again after changing it. At least 1.
    /// </summary>
    public int SimilarDocumentVotes { get; init; } = 10;

    /// <summary>
    /// Score at or above which values settled alongside the document's known values are suggested as backed by them.
    /// A value's strength under a key is its weighted count there divided by the key's weighted total plus one — how pure
    /// the key is for it, discounted when the key was seen only a few times, so a key settled once adds at most 0.5. Its
    /// score is the sum of its strengths under the document's keys, so it ranges up to the number of keys: a value that
    /// several keys back scores higher than one that a single key backs as purely. The same score ranks the values, and
    /// the best-ranked value's score decides for the whole layer. A threshold chosen before 0.9.0 measured the strongest
    /// key alone and must be chosen again. Null offers those values only as guesses, after
    /// every layer that answers: a key that rarely decides the field, such as a choice among a handful of values that
    /// every document has, must not outrank a similar document that meets its threshold. Among guesses it comes first,
    /// before the field's most frequent value and the nearest document below its threshold. Choose it by replaying settled documents
    /// (<c>ThresholdSelection.SelectKeyThreshold</c>).
    /// </summary>
    public double? KeyThreshold { get; init; }

    /// <summary>
    /// Thresholds for the key layer while a person types into the field, by how many characters are typed: the first
    /// entry applies once one character is typed, the second at two, and so on. Typed text narrows the candidates to the
    /// values that begin with it (ignoring case); a value under the keys is then trusted when its score reaches the entry
    /// for that length. A value already trusted with fewer characters typed — or none, under <see cref="KeyThreshold"/> —
    /// stays trusted while the typed text still leads to it: narrowing leaves its score and its lead unchanged, so it is
    /// the same suggestion, not a new one to judge again. Past the list, or where an entry is null, other narrowed values
    /// are offered as guesses only. A person types into a field only when the suggestion before did not do, so each entry
    /// is chosen on the documents typed that far: choose them with <c>ThresholdSelection.SelectTypedKeyThresholds</c>,
    /// after <see cref="KeyThreshold"/>, which they follow. Not for a field that takes several values.
    /// </summary>
    public IReadOnlyList<double?>? TypedKeyThresholds { get; init; }

    /// <summary>
    /// The key threshold a suggestion is held to with <paramref name="typed"/> characters typed into the field:
    /// <see cref="KeyThreshold"/> with none, else the entry of <see cref="TypedKeyThresholds"/> for that many, if any.
    /// </summary>
    public double? KeyThresholdFor(int typed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(typed);
        return typed == 0 ? KeyThreshold : TypedKeyThresholds is { } thresholds && typed <= thresholds.Count ? thresholds[typed - 1] : null;
    }

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

    /// <summary>
    /// True makes a judged field's value a set: several values, in no order, such as tags, the topics a document covers or
    /// the answers of a multiple-choice question. Its values are settled in <see cref="SettledDocument.Sets"/>, never in
    /// <see cref="SettledDocument.Values"/>. Each value is remembered on its own — a document with three values settles
    /// each of the three under every key — and is suggested on its own, so several candidates may each be trusted.
    /// Values of the field already chosen are evidence for the rest, whatever <see cref="DependsOn"/> and
    /// <see cref="UseAsEvidence"/> say, which govern evidence between fields; as evidence for another field, each value is a
    /// key of its own. <see cref="Candidates"/> lists the values each element may take. A value that has a main one among
    /// several is better a second, single-valued field. The settled field memory and a model suggest such a field; the
    /// similar document layer does not — a document memory holds one value per document — so it takes no
    /// <see cref="MemoryThreshold"/>.
    /// </summary>
    public bool Multiple { get; init; }

    /// <summary>
    /// A coarser level of the field's values to offer when neither memory layer answers the value itself — for values
    /// whose leading characters name a broader class, as a chapter of a fault code, a class of a patent classification or a
    /// group of a product code. Null offers none. See <see cref="CoarseLevel"/>. Not for a field that takes several values.
    /// </summary>
    public CoarseLevel? Coarse { get; init; }
}

/// <summary>
/// The coarse level of a judged field (<see cref="FieldDefinition.Coarse"/>): the first <see cref="Prefix"/> characters of
/// its values. When neither memory layer answers a value, the same two layers are asked for the level instead — the
/// scores of the values under the document's keys added up by their prefix, then the similarities of the documents voting
/// added up by theirs — and the prefix they back is offered as <see cref="FieldSuggestion.Coarse"/>. A person who cannot
/// be given the value can often be given its class, which is enough for what follows from the class (routing, a
/// reviewer, a form section) and narrows what is left to type. The hierarchy is the values' own: a field whose classes are
/// not prefixes of its values maps them in the application. Choose the thresholds with
/// <c>ThresholdSelection.SelectCoarseAsync</c>, after the field's own.
/// </summary>
/// <param name="Prefix">How many leading characters of a value name its class; at least 1.</param>
public sealed record CoarseLevel(int Prefix)
{
    /// <summary>
    /// Score at or above which the prefix the document's keys back is trusted: the summed key scores of the values that
    /// share it, on the key layer's scale. Null makes no promise from the keys.
    /// </summary>
    public double? KeyThreshold { get; init; }

    /// <summary>
    /// Score at or above which the prefix the similar documents vote for is trusted, where the keys' prefix is not: the
    /// vote's margin over prefixes, on the scale of <see cref="FieldDefinition.MemoryThreshold"/>. Null makes no promise.
    /// </summary>
    public double? MemoryThreshold { get; init; }

    /// <summary>
    /// The similarity the nearest document voting for the prefix must also reach, as
    /// <see cref="FieldDefinition.MemorySimilarityFloor"/> is for the value — set with <see cref="MemoryThreshold"/> from the
    /// same choice.
    /// </summary>
    public double? MemorySimilarityFloor { get; init; }

    /// <summary>The class of <paramref name="value"/>: its first <see cref="Prefix"/> characters, or all of it when shorter.</summary>
    public string Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= Prefix ? value : value[..Prefix];
    }
}

/// <summary>A form: the fields a document of it has, and the language a model reads it in.</summary>
public sealed record FormDefinition
{
    /// <param name="name">Scopes memory, statistics and telemetry, like a task's name.</param>
    /// <param name="fields">At least one judged field; names unique.</param>
    /// <param name="language">The wording model calls for this form are made in.</param>
    /// <exception cref="ArgumentException">
    /// A duplicate field name, no judged field, an observed field marked <see cref="FieldDefinition.Multiple"/> or such a
    /// field with a <see cref="FieldDefinition.MemoryThreshold"/> or <see cref="FieldDefinition.TypedKeyThresholds"/>, a
    /// <see cref="FieldDefinition.SimilarDocumentVotes"/> below 1, or a
    /// <see cref="FieldDefinition.DependsOn"/> that names an unknown field, the field itself, or a field that is not evidence,
    /// or a <see cref="FieldDefinition.CandidatesFrom"/> that names an unknown field or the field itself.
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

        if (fields.FirstOrDefault(f => f.Multiple && f.Role != FieldRole.Judged) is { } observedSet)
        {
            throw new ArgumentException($"Field '{observedSet.Name}' is observed; only a judged field can take several values.", nameof(fields));
        }

        if (fields.FirstOrDefault(f => f.Multiple && f.MemoryThreshold is not null) is { } rememberedSet)
        {
            throw new ArgumentException($"Field '{rememberedSet.Name}' takes several values; the similar document layer does not suggest it, so it takes no memory threshold.", nameof(fields));
        }

        if (fields.FirstOrDefault(f => f.SimilarDocumentVotes < 1) is { } voteless)
        {
            throw new ArgumentException($"Field '{voteless.Name}' gives {voteless.SimilarDocumentVotes} similar documents a vote; at least one must.", nameof(fields));
        }

        if (fields.FirstOrDefault(f => f.Coarse is { Prefix: < 1 }) is { } prefixless)
        {
            throw new ArgumentException($"Field '{prefixless.Name}' gives its coarse level a prefix of {prefixless.Coarse!.Prefix} characters; at least one.", nameof(fields));
        }

        if (fields.FirstOrDefault(f => f.Coarse is not null && (f.Multiple || f.Role != FieldRole.Judged)) is { } coarseField)
        {
            throw new ArgumentException($"Field '{coarseField.Name}' takes a coarse level, which only a judged field with a single value has.", nameof(fields));
        }

        if (fields.FirstOrDefault(f => f.Multiple && f.TypedKeyThresholds is not null) is { } typedSet)
        {
            throw new ArgumentException($"Field '{typedSet.Name}' takes several values; typed text narrows its candidates but takes no typed key thresholds.", nameof(fields));
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

            if (field.CandidatesFrom is { } from && (from == field.Name || !byName.ContainsKey(from)))
            {
                throw new ArgumentException($"Field '{field.Name}' takes its candidates from '{from}', which is not another field of the form.", nameof(fields));
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
/// <param name="SettledAt">
/// When a judged value of the document was last settled. Where settled documents disagree, the later settlement wins, so
/// memory reaches the same state whatever order documents arrive in. A file's last write time serves.
/// </param>
/// <param name="Arrival">
/// The fields with a value, in the order their current values arrived, oldest first — what a form session's snapshot
/// gives. Choosing thresholds replays each judged field with only the values that had arrived before it, which is what
/// its last suggestion was made from. A field the list leaves out counts as there from the start. Null when the order is
/// unknown: the observed values are then taken as there from the start and the judged ones as settled in the form's
/// order. Memory does not use it — a settled document is remembered with all its values.
/// </param>
public sealed record SettledDocument(
    string DocumentId,
    IReadOnlyDictionary<string, string> Values,
    DateTimeOffset SettledAt,
    IReadOnlyList<string>? Arrival = null)
{
    /// <summary>
    /// Field name to the values of a field that takes several (<see cref="FieldDefinition.Multiple"/>); such a field is
    /// never in <see cref="Values"/>. Order and repeats carry no meaning. A field without values is absent, or empty.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sets { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Checks that the document keeps each field of <paramref name="form"/> where its kind belongs: a field that takes
    /// several values only in <see cref="Sets"/>, any other field only in <see cref="Values"/>. Names the form does not
    /// have are left alone, as everywhere.
    /// </summary>
    /// <exception cref="ArgumentException">A field of the form is kept where its kind does not belong.</exception>
    public void Validate(FormDefinition form)
    {
        ArgumentNullException.ThrowIfNull(form);
        foreach (var field in form.Fields)
        {
            if (field.Multiple && Values.ContainsKey(field.Name))
            {
                throw new ArgumentException($"Field '{field.Name}' takes several values; settle them in Sets, not Values.", nameof(form));
            }

            if (!field.Multiple && Sets.ContainsKey(field.Name))
            {
                throw new ArgumentException($"Field '{field.Name}' takes one value; settle it in Values, not Sets.", nameof(form));
            }
        }
    }
}

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
/// <param name="Trusted">
/// Whether the layer answered: its threshold was met (<see cref="FieldDefinition.KeyThreshold"/>,
/// <see cref="FieldDefinition.MemoryThreshold"/>) or it is a model's choice. False marks a guess — values under a key too
/// weak to decide, the field's most frequent value, the value similar documents vote for below the threshold (or with
/// none set), the field's other values, in that order — which an application may still list but should not present as a
/// suggestion.
/// </param>
public sealed record FieldCandidate(string Value, double Score, FieldSource Source, string? Evidence, bool Trusted = true);

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
    string TraceId)
{
    /// <summary>
    /// Whether a layer answered: the first candidate is trusted. False when there are only guesses or no candidates — the
    /// field is left to a person, and its trace records an abstention.
    /// </summary>
    public bool Answered => Candidates.Count > 0 && Candidates[0].Trusted;

    /// <summary>
    /// The settled documents most similar to this one, most similar first — the evidence behind the candidate from similar
    /// documents, so a person can see what it rests on and whether its neighbours agree. That candidate's own document
    /// (<see cref="FieldCandidate.Evidence"/>) is the nearest of them with its value, when it is among them. Empty unless the resolver was asked for them and has a document memory that found a document
    /// — whether or not the field sets <see cref="FieldDefinition.MemoryThreshold"/>; the document itself, and documents
    /// whose value lies outside the field's domain, are never among them. Memory keeps one document per case, so each is a different case.
    /// </summary>
    public IReadOnlyList<MemoryMatch> SimilarDocuments { get; init; } = [];

    /// <summary>
    /// When the field has a coarse level (<see cref="FieldDefinition.Coarse"/>) and no layer answered its value: the class
    /// the memory layers back, as a candidate whose value is the prefix — trusted when its layer's coarse threshold is met,
    /// otherwise a guess. Kept apart from <see cref="Candidates"/>, which hold settled values only. Null when a layer
    /// answered the value, the field has no coarse level, text is being typed into it, or no settled document backs a
    /// prefix.
    /// </summary>
    public FieldCandidate? Coarse { get; init; }
}

/// <summary>What a model suggested for one field.</summary>
/// <param name="Candidates">Best first; empty when the model abstained.</param>
/// <param name="Confidence">The model's judgment probability for the first candidate; null when it reported none.</param>
/// <param name="Energy">Total cost of the calls made.</param>
public sealed record FieldModelResult(IReadOnlyList<FieldCandidate> Candidates, double? Confidence, double Energy);

/// <summary>
/// A model that suggests one field. The form resolver calls it only for a field no memory had evidence for, and passes
/// only the values the field may rest on — fields that are not evidence never reach it.
/// </summary>
/// <remarks>
/// The form resolver opens the suggestion's trace first, under the task <c>form/field</c>. A model that resolves under the
/// same id through the same sink and closes the trace makes its own outcome the suggestion's record — so feedback on the
/// suggestion reaches the model — and the form resolver leaves it as it is; otherwise the form resolver closes it.
/// </remarks>
public interface IFieldModel
{
    /// <param name="form">The form.</param>
    /// <param name="field">The judged field to suggest.</param>
    /// <param name="evidence">
    /// The values the field may rest on. From an open document (<c>FormSession</c>) they come in the order they arrived, a
    /// changed value moving to the end, so the evidence only grows at its end as the document fills; from a stateless
    /// suggestion, which has no history, in the form's order.
    /// </param>
    /// <param name="traceId">The suggestion's id, already opened as a trace: record the call under it, or record nothing.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<FieldModelResult> SuggestAsync(
        FormDefinition form,
        string field,
        IReadOnlyList<KeyValuePair<string, string>> evidence,
        string traceId,
        CancellationToken cancellationToken = default);
}

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

    /// <summary>
    /// The values of a field that takes several (<see cref="FieldDefinition.Multiple"/>) were settled — whichever of them
    /// were suggested and whichever a person added. Settling the field again replaces them.
    /// </summary>
    Set,
}

/// <summary>
/// A settlement of one judged field. Accepting is not the same as being right: settling the field again later corrects it.
/// A field that takes several values (<see cref="FieldDefinition.Multiple"/>) is settled with <see cref="Set"/>, restored
/// with <see cref="Restore(IEnumerable{string})"/>, and rejected or reverted as any other.
/// </summary>
public sealed record Settlement
{
    private Settlement(SettlementKind kind, string? value, IReadOnlyList<string>? values = null) => (Kind, Value, Values) = (kind, value, values);

    public SettlementKind Kind { get; }

    /// <summary>The settled value of a single-valued field; null for <see cref="SettlementKind.Reject"/>, <see cref="SettlementKind.Revert"/> and settlements of a set.</summary>
    public string? Value { get; }

    /// <summary>The settled values of a field that takes several, once each and ordinally; null for a single-valued field's settlement, <see cref="SettlementKind.Reject"/> and <see cref="SettlementKind.Revert"/>.</summary>
    public IReadOnlyList<string>? Values { get; }

    /// <summary>Whether this settles a field that takes several values.</summary>
    public bool IsSet => Values is not null;

    public static Settlement Accept(string value) => new(SettlementKind.Accept, Required(value));

    public static Settlement Correct(string value) => new(SettlementKind.Correct, Required(value));

    public static Settlement Reject() => new(SettlementKind.Reject, null);

    public static Settlement Revert() => new(SettlementKind.Revert, null);

    public static Settlement Restore(string value) => new(SettlementKind.Restore, Required(value));

    /// <summary>Settles a field that takes several values with these — none at all clears it. Order and repeats carry no meaning.</summary>
    public static Settlement Set(IEnumerable<string> values) => new(SettlementKind.Set, null, Elements(values));

    /// <summary>Puts back the saved values of a field that takes several, as <see cref="Restore(string)"/> does for one.</summary>
    public static Settlement Restore(IEnumerable<string> values) => new(SettlementKind.Restore, null, Elements(values));

    private static string[] Elements(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return [.. values.Select(Required).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static string Required(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
