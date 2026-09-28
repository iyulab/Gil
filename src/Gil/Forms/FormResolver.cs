namespace Gil.Forms;

/// <summary>
/// Suggests the judged fields of a form, one document at a time. Each field is tried in a fixed order — values settled
/// alongside the document's other values (<see cref="FieldMemory"/>), then the value of a similar settled document (an
/// optional <see cref="IMemory"/>), then a model (an optional <see cref="IFieldModel"/>, called only when neither memory
/// had evidence), then values settled most often overall — and a field no layer can suggest is left to a person.
/// Without a document memory or a model the order simply has a layer less; every result has the same shape.
/// </summary>
/// <remarks>
/// Memory is derived from settled documents: a <see cref="FormSession"/> puts its document again after every change, and
/// <see cref="RebuildAsync"/> puts saved documents, and both replace what the document contributed before, so using the
/// two together never counts a document twice: each memory ends up holding the same values under the same keys either
/// way (a memory that reweighs as it grows may still score them slightly differently). Not thread-safe; one instance
/// serves one caller at a time.
/// </remarks>
public sealed class FormResolver
{
    /// <param name="fieldMemory">Values settled alongside other field values.</param>
    /// <param name="documentMemory">
    /// Similar settled documents, looked up by the evidence fields' <c>name: value</c> lines under the task
    /// <c>form/field</c>, keyed by document id. Consulted for a field only when it sets
    /// <see cref="FieldDefinition.MemoryThreshold"/>. A failing lookup is a miss and a failing write is dropped — memory
    /// is rebuilt from the documents.
    /// </param>
    /// <param name="model">
    /// Suggests a field neither memory had evidence for: no value settled alongside the known ones, no similar document
    /// close enough. A model is weaker than a memory with evidence, so it never overrides one.
    /// </param>
    /// <param name="sink">
    /// Records one trace per suggestion, under the task <c>form/field</c>; where a model closed it, with the model's outcome.
    /// </param>
    /// <param name="candidateCount">How many candidates a suggestion offers at most.</param>
    public FormResolver(FieldMemory fieldMemory, IMemory? documentMemory = null, IFieldModel? model = null, ITelemetrySink? sink = null, int candidateCount = 3)
    {
        ArgumentNullException.ThrowIfNull(fieldMemory);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateCount, 1);
        (FieldMemory, DocumentMemory, Model, Sink, CandidateCount) = (fieldMemory, documentMemory, model, sink, candidateCount);
    }

    internal FieldMemory FieldMemory { get; }

    internal IMemory? DocumentMemory { get; }

    internal IFieldModel? Model { get; }

    internal ITelemetrySink? Sink { get; }

    internal int CandidateCount { get; }

    /// <summary>Opens a document of the form. Put back a saved document's values with <see cref="FormSession.ObserveAsync"/> and <see cref="Settlement.Restore"/>.</summary>
    /// <param name="form">The form.</param>
    /// <param name="documentId">Opaque; the key the document's contribution to memory is kept under.</param>
    public FormSession Open(FormDefinition form, string documentId)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new FormSession(this, form, documentId);
    }

    /// <summary>
    /// Puts saved documents into memory, each replacing what it contributed before — in any order, as often as needed.
    /// Documents not given are left as they are; start from empty memories to rebuild from scratch. Returns the energy
    /// the document memory reported.
    /// </summary>
    public async Task<double> RebuildAsync(FormDefinition form, IEnumerable<SettledDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        var traceId = Guid.NewGuid().ToString("N"); // a trace of its own: the rebuild's cost is not charged to any suggestion
        var energy = 0.0;
        foreach (var document in documents)
        {
            energy += await PutAsync(form, document, traceId, cancellationToken).ConfigureAwait(false);
        }

        return energy;
    }

    /// <summary>Replaces the document's contribution to both memories with what its current values imply.</summary>
    internal async Task<double> PutAsync(FormDefinition form, SettledDocument document, string traceId, CancellationToken cancellationToken)
    {
        FieldMemory.Put(form, document);
        if (DocumentMemory is null)
        {
            return 0;
        }

        var energy = 0.0;
        foreach (var field in form.Fields.Where(f => f.Role == FieldRole.Judged && f.MemoryThreshold is not null))
        {
            var task = TaskName(form, field.Name);
            if (!document.Values.TryGetValue(field.Name, out var value))
            {
                DocumentMemory.Forget(task, document.DocumentId);
                continue;
            }

            try
            {
                energy += await DocumentMemory.RememberAsync(task, document.DocumentId, Evidence(form, field.Name, document.Values), value, traceId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Dropped like the resolver's failed writes: the document still holds the value and a rebuild restores it.
            }
        }

        return energy;
    }

    /// <summary>The task a field's document memory, statistics and traces are kept under.</summary>
    internal static string TaskName(FormDefinition form, string field) => $"{form.Name}/{field}";

    /// <summary>The values of the fields that support <paramref name="field"/>, in the form's order.</summary>
    internal static List<KeyValuePair<string, string>> EvidenceValues(FormDefinition form, string field, IReadOnlyDictionary<string, string> values) =>
        [.. form.Fields
            .Where(f => form.Supports(f.Name, field) && values.ContainsKey(f.Name))
            .Select(f => KeyValuePair.Create(f.Name, values[f.Name]))];

    /// <summary>The <c>name: value</c> lines of the fields that support <paramref name="field"/>, in the form's order.</summary>
    internal static string Evidence(FormDefinition form, string field, IReadOnlyDictionary<string, string> values) =>
        Lines(EvidenceValues(form, field, values));

    /// <summary>Evidence as <c>name: value</c> lines — the request a similar document is looked up by, and a model's input.</summary>
    internal static string Lines(IEnumerable<KeyValuePair<string, string>> evidence) =>
        string.Join('\n', evidence.Select(e => $"{e.Key}: {e.Value}"));
}
