using Gil.Memory;

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
/// way (a memory that reweighs as it grows may still score them slightly differently). Where documents disagree, the
/// later <see cref="SettledDocument.SettledAt"/> wins: documents whose evidence for a field reads the same are one case,
/// and the document memory holds only the case's latest settlement. Not thread-safe; one instance serves one caller at a
/// time.
/// </remarks>
public sealed class FormResolver
{
    private readonly Dictionary<string, Cases> _cases = new(StringComparer.Ordinal);

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
    /// <param name="timeProvider">The clock a session's settlements are timed by; the system clock when null.</param>
    public FormResolver(
        FieldMemory fieldMemory,
        IMemory? documentMemory = null,
        IFieldModel? model = null,
        ITelemetrySink? sink = null,
        int candidateCount = 3,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(fieldMemory);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateCount, 1);
        (FieldMemory, DocumentMemory, Model, Sink, CandidateCount, Time) =
            (fieldMemory, documentMemory, model, sink, candidateCount, timeProvider ?? TimeProvider.System);
    }

    internal FieldMemory FieldMemory { get; }

    internal IMemory? DocumentMemory { get; }

    internal IFieldModel? Model { get; }

    internal ITelemetrySink? Sink { get; }

    internal int CandidateCount { get; }

    internal TimeProvider Time { get; }

    /// <summary>
    /// Opens a document of the form. Put back a saved document's values with <see cref="FormSession.ObserveAsync"/> and
    /// <see cref="Settlement.Restore"/>, and pass its <see cref="SettledDocument.SettledAt"/>: restoring does not make old
    /// values new, and only accepting or correcting a field moves the document's settlement time on.
    /// </summary>
    /// <param name="form">The form.</param>
    /// <param name="documentId">Opaque; the key the document's contribution to memory is kept under.</param>
    /// <param name="settledAt">When a saved document was last settled; null for a new document, which starts at the present.</param>
    public FormSession Open(FormDefinition form, string documentId, DateTimeOffset? settledAt = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return new FormSession(this, form, documentId, settledAt ?? Time.GetUtcNow());
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
            if (!_cases.TryGetValue(task, out var cases))
            {
                _cases[task] = cases = new Cases();
            }

            var entry = document.Values.TryGetValue(field.Name, out var value)
                ? new CaseEntry(document.DocumentId, Evidence(form, field.Name, document.Values), value, document.SettledAt)
                : null;
            var (forget, remember) = cases.Put(document.DocumentId, entry);
            foreach (var id in forget)
            {
                DocumentMemory.Forget(task, id);
            }

            foreach (var latest in remember)
            {
                try
                {
                    energy += await DocumentMemory.RememberAsync(task, latest.DocumentId, latest.Evidence, latest.Value, traceId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Dropped like the resolver's failed writes: the document still holds the value and a rebuild restores it.
                }
            }
        }

        return energy;
    }

    /// <summary>
    /// The case a field's evidence makes: evidence that reads the same after normalisation — the documents a lookup could
    /// not tell apart.
    /// </summary>
    internal static string CaseKey(string evidence) => TextNormal.Collapse(evidence);

    /// <summary>
    /// Whether <paramref name="a"/> was settled later than <paramref name="b"/>; equal times go to the ordinally larger
    /// document id, so the order is total and the same whatever order documents arrive in.
    /// </summary>
    internal static bool Later(DateTimeOffset a, string aId, DateTimeOffset b, string bId) =>
        a != b ? a > b : string.CompareOrdinal(aId, bId) > 0;

    /// <summary>A document's settled value for one field and the evidence it was settled on.</summary>
    private sealed record CaseEntry(string DocumentId, string Evidence, string Value, DateTimeOffset SettledAt);

    /// <summary>One field's documents grouped into cases, each represented in the document memory by its latest settlement.</summary>
    private sealed class Cases
    {
        private readonly Dictionary<string, string> _caseOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, CaseEntry>> _members = new(StringComparer.Ordinal);

        /// <summary>
        /// Replaces the document's entry (null: it has no value for the field) and returns the document memory writes that
        /// keep every affected case represented by its latest settlement.
        /// </summary>
        public (List<string> Forget, List<CaseEntry> Remember) Put(string documentId, CaseEntry? entry)
        {
            var affected = new List<string>();
            if (_caseOf.Remove(documentId, out var previous))
            {
                affected.Add(previous);
            }

            var current = entry is null ? null : CaseKey(entry.Evidence);
            if (current is not null && current != previous)
            {
                affected.Add(current);
            }

            var before = affected.ToDictionary(c => c, Latest, StringComparer.Ordinal);
            if (previous is not null)
            {
                Remove(previous, documentId);
            }

            if (entry is not null)
            {
                _caseOf[documentId] = current!;
                if (!_members.TryGetValue(current!, out var members))
                {
                    _members[current!] = members = new Dictionary<string, CaseEntry>(StringComparer.Ordinal);
                }

                members[documentId] = entry;
            }

            var (forget, remember) = (new List<string>(), new List<CaseEntry>());
            var represents = false;
            foreach (var key in affected)
            {
                var (was, now) = (before[key], Latest(key));
                represents |= now?.DocumentId == documentId;
                if (was is not null && was.DocumentId != documentId && was.DocumentId != now?.DocumentId)
                {
                    forget.Add(was.DocumentId);
                }

                // The document itself is written again whenever it represents its case: its value or evidence may have changed.
                if (now is not null && (now.DocumentId != was?.DocumentId || now.DocumentId == documentId))
                {
                    remember.Add(now);
                }
            }

            if (!represents)
            {
                forget.Add(documentId);
            }

            return (forget, remember);
        }

        private void Remove(string key, string documentId)
        {
            var members = _members[key];
            members.Remove(documentId);
            if (members.Count == 0)
            {
                _members.Remove(key);
            }
        }

        private CaseEntry? Latest(string key)
        {
            if (!_members.TryGetValue(key, out var members))
            {
                return null;
            }

            CaseEntry? latest = null;
            foreach (var entry in members.Values)
            {
                if (latest is null || Later(entry.SettledAt, entry.DocumentId, latest.SettledAt, latest.DocumentId))
                {
                    latest = entry;
                }
            }

            return latest;
        }
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
