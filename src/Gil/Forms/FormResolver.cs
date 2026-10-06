using System.Diagnostics;
using Gil.Memory;

namespace Gil.Forms;

/// <summary>
/// Suggests the judged fields of a form, one document at a time. Each field is tried in a fixed order — values settled
/// alongside the document's other values (<see cref="FieldMemory"/>) under a key strong enough, then the value of a
/// similar settled document (an optional <see cref="IMemory"/>) close enough, then a model (an optional
/// <see cref="IFieldModel"/>, called only when neither memory had evidence). A layer answers only above its threshold;
/// below it, what it found is offered after the answering layers as a guess, followed by the values settled most often
/// overall, and a field without an answer is left to a person (<see cref="FieldSuggestion.Answered"/>). Without a
/// document memory or a model the order simply has a layer less; every result has the same shape.
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
    /// <param name="similarDocumentCount">
    /// How many of the most similar settled documents a suggestion reports as <see cref="FieldSuggestion.SimilarDocuments"/>
    /// — the evidence behind its candidate from a similar document. None by default. They come from the same lookup, so
    /// asking for them costs no second call; a document memory that ranks only its nearest reports that one.
    /// </param>
    public FormResolver(
        FieldMemory fieldMemory,
        IMemory? documentMemory = null,
        IFieldModel? model = null,
        ITelemetrySink? sink = null,
        int candidateCount = 3,
        TimeProvider? timeProvider = null,
        int similarDocumentCount = 0)
    {
        ArgumentNullException.ThrowIfNull(fieldMemory);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(similarDocumentCount);
        (FieldMemory, DocumentMemory, Model, Sink, CandidateCount, Time, SimilarDocumentCount) =
            (fieldMemory, documentMemory, model, sink, candidateCount, timeProvider ?? TimeProvider.System, similarDocumentCount);
    }

    internal FieldMemory FieldMemory { get; }

    internal IMemory? DocumentMemory { get; }

    internal IFieldModel? Model { get; }

    internal ITelemetrySink? Sink { get; }

    internal int CandidateCount { get; }

    internal TimeProvider Time { get; }

    internal int SimilarDocumentCount { get; }

    /// <summary>
    /// Opens a document of the form. Put back a saved document's values with <see cref="FormSession.ObserveAsync"/> and
    /// <see cref="Settlement.Restore(string)"/> (or <see cref="Settlement.Restore(IEnumerable{string})"/> for a set), and pass its <see cref="SettledDocument.SettledAt"/>: restoring does not make old
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
        var judged = form.Fields.Where(f => f.Role == FieldRole.Judged).ToList();
        // Documents in groups whose evidence the document memory embeds together first, each distinct text once — the
        // judged fields of a form often read the same evidence. The writes that follow find the vectors already made.
        foreach (var group in documents.Chunk(Math.Max(1, PrefetchTexts / judged.Count)))
        {
            if (DocumentMemory is IEmbeddingPrefetch prefetch)
            {
                var texts = group.SelectMany(d => judged.Where(f => d.Values.ContainsKey(f.Name)).Select(f => Evidence(form, f.Name, d.Values, d.Sets)));
                try
                {
                    energy += await prefetch.PrefetchAsync(texts, traceId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The writes embed one by one instead, and are dropped the same way if the memory is still failing.
                }
            }

            foreach (var document in group)
            {
                energy += await PutAsync(form, document, traceId, cancellationToken).ConfigureAwait(false);
            }
        }

        return energy;
    }

    /// <summary>
    /// About how many evidence texts a rebuild has the document memory embed ahead at a time — well within what the memory
    /// keeps (<see cref="EmbeddingMemory.RecentLimit"/>), so none is dropped before its write uses it.
    /// </summary>
    internal const int PrefetchTexts = 256;

    /// <summary>
    /// Suggests every open judged field — without a value and not <see cref="FieldPolicy.Off"/> — from the values given,
    /// writing nothing to memory. For applications where saving is settling: ask with the values on screen, and put the
    /// document with <see cref="RebuildAsync"/> once it is saved. The saved version of <paramref name="documentId"/>, if
    /// memory holds one, is not evidence for itself: its settlements are left out of the field memory, and a similar
    /// document lookup that finds it passes it over for the next most similar document.
    /// </summary>
    /// <param name="form">The form.</param>
    /// <param name="documentId">The document asked about; opaque, as in <see cref="Open"/>.</param>
    /// <param name="values">The document's values as they stand — observed and judged alike.</param>
    /// <param name="cancellationToken">Cancels the calls made.</param>
    public Task<IReadOnlyList<FieldSuggestion>> SuggestAsync(
        FormDefinition form,
        string documentId,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default) =>
        SuggestAsync(form, documentId, values, NoSets, cancellationToken);

    /// <summary>
    /// As <see cref="SuggestAsync(FormDefinition, string, IReadOnlyDictionary{string, string}, CancellationToken)"/>, for a form
    /// with fields that take several values: <paramref name="sets"/> holds the values chosen so far of each such field. A
    /// field that takes several values is suggested whether or not some are chosen — the rest, with the chosen ones as
    /// evidence.
    /// </summary>
    /// <exception cref="ArgumentException">A field is given where its kind does not belong (<see cref="SettledDocument.Validate"/>).</exception>
    public async Task<IReadOnlyList<FieldSuggestion>> SuggestAsync(
        FormDefinition form,
        string documentId,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(sets);
        new SettledDocument(documentId, values, default) { Sets = sets }.Validate(form);
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in form.Fields.Where(f => f.Role == FieldRole.Judged && f.Policy != FieldPolicy.Off && (f.Multiple || !values.ContainsKey(f.Name))))
        {
            suggestions.Add(await SuggestFieldAsync(form, documentId, values, field, cancellationToken, sets: sets).ConfigureAwait(false));
        }

        return suggestions;
    }

    /// <summary>
    /// As <see cref="SuggestAsync(FormDefinition, string, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, IReadOnlyList{string}}, CancellationToken)"/>,
    /// while a person types into judged fields: <paramref name="typed"/> holds, for each such field, the text typed so far.
    /// Such a field is open — it has no value yet — and its suggestion offers only values that begin with the typed text
    /// (ignoring case). Only the key layer answers then, held to <see cref="FieldDefinition.KeyThresholdFor"/> that many
    /// characters; a similar document's value that fits is offered as a guess, and no model is asked — a person who is
    /// typing asks again with every pause.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A field is given where its kind does not belong, or text is typed into a field that is not judged or already has a value.
    /// </exception>
    public async Task<IReadOnlyList<FieldSuggestion>> SuggestAsync(
        FormDefinition form,
        string documentId,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sets,
        IReadOnlyDictionary<string, string> typed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(typed);
        new SettledDocument(documentId, values, default) { Sets = sets }.Validate(form);
        Typed(form, typed, values.ContainsKey);
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in form.Fields.Where(f => f.Role == FieldRole.Judged && f.Policy != FieldPolicy.Off && (f.Multiple || !values.ContainsKey(f.Name))))
        {
            suggestions.Add(await SuggestFieldAsync(form, documentId, values, field, cancellationToken, sets: sets, typed: typed.GetValueOrDefault(field.Name)).ConfigureAwait(false));
        }

        return suggestions;
    }

    /// <summary>Checks that text is typed only into judged fields without a value.</summary>
    internal static void Typed(FormDefinition form, IReadOnlyDictionary<string, string> typed, Func<string, bool> hasValue)
    {
        foreach (var (name, text) in typed)
        {
            ArgumentNullException.ThrowIfNull(text, nameof(typed));
            if (form.Field(name).Role != FieldRole.Judged)
            {
                throw new ArgumentException($"Text is typed into '{name}', which is not a judged field.", nameof(typed));
            }

            if (hasValue(name))
            {
                throw new ArgumentException($"Text is typed into '{name}', which already has a value.", nameof(typed));
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoSets =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// One field's suggestion. Layers that answer come first — values under a key strong enough
    /// (<see cref="FieldDefinition.KeyThreshold"/>), a document similar enough (<see cref="FieldDefinition.MemoryThreshold"/>),
    /// and a model where neither memory had anything — then guesses: values under weaker keys, the field's most frequent
    /// value, the nearest document below the threshold, the field's other values. <c>arrival</c> is the order the document's values arrived in,
    /// oldest first — the order a model sees its evidence in; null when the caller has no history (a stateless
    /// suggestion), and the form's order then. <c>sets</c> holds the values chosen so far of fields that take several; for
    /// such a field itself, its chosen values are evidence and are not offered again.
    /// </summary>
    internal async Task<FieldSuggestion> SuggestFieldAsync(
        FormDefinition form,
        string documentId,
        IReadOnlyDictionary<string, string> values,
        FieldDefinition field,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? arrival = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? sets = null,
        string? typed = null)
    {
        var started = Stopwatch.GetTimestamp();
        typed = string.IsNullOrEmpty(typed) ? null : typed;
        var traceId = Guid.NewGuid().ToString("N");
        var task = TaskName(form, field.Name);
        sets ??= NoSets;
        var evidence = EvidenceValues(form, field.Name, values, sets);
        var lines = Lines(evidence);
        Sink?.OpenTrace(traceId, task, lines); // opened first: a model resolving under the same id closes it

        var chosen = field.Multiple && sets.TryGetValue(field.Name, out var picked) ? new HashSet<string>(picked, StringComparer.Ordinal) : [];
        var remembered = FieldMemory.Rank(form, field.Name, values, CandidateCount, excluding: documentId, knownSets: sets, typed: typed);
        var keyed = remembered.Where(c => c.Evidence is not null).ToList();
        var (similar, neighbours, recall, energy) = await SimilarAsync(field, documentId, task, lines, values, sets, typed, traceId, cancellationToken).ConfigureAwait(false);

        // A model only where neither memory had evidence: a value backed by what the document says — even by a key too
        // weak to answer — beats a model's guess. Not while a person is typing: they ask again with every pause.
        FieldModelResult? modelled = null;
        if (Model is IFieldModel model && typed is null && keyed.Count == 0 && !similar.Any(c => c.Trusted))
        {
            var ordered = arrival is null ? evidence : InArrivalOrder(evidence, arrival);
            modelled = await model.SuggestAsync(form, field.Name, ordered, traceId, cancellationToken).ConfigureAwait(false);
            energy += modelled.Energy;
        }

        // Guesses, strongest first, as replaying settled streams ranked them: values under weaker keys, the field's most
        // frequent value, the nearest document below its threshold, then the field's other values by frequency.
        var frequent = remembered.Where(c => c.Evidence is null).ToList();
        var candidates = keyed.Where(c => c.Trusted)
            .Concat(similar.Where(c => c.Trusted))
            .Concat(modelled?.Candidates.Where(c => field.Admits(c.Value, values, sets) && !chosen.Contains(c.Value)) ?? [])
            .Concat(keyed.Where(c => !c.Trusted))
            .Concat(frequent.Take(1))
            .Concat(similar.Where(c => !c.Trusted))
            .Concat(frequent.Skip(1))
            .DistinctBy(c => c.Value, StringComparer.Ordinal)
            .Take(CandidateCount)
            .ToList();
        var source = candidates.Count > 0 ? candidates[0].Source : FieldSource.None;
        var answered = candidates.Count > 0 && candidates[0].Trusted;
        var confidence = source == FieldSource.Model ? modelled?.Confidence : null;

        // A model that resolved under this id through the same sink has closed the trace with its own outcome.
        if (Sink is ITelemetrySink sink && (modelled is null || sink.FindTrace(traceId) is null))
        {
            sink.CloseTrace(traceId, new TraceOutcome
            {
                Mode = answered ? Mode(source) : "abstain",
                // A set's answer is every value trusted on its own score, one per line, best first.
                Output = !answered ? null : field.Multiple ? string.Join("\n", candidates.Where(c => c.Trusted).Select(c => c.Value)) : candidates[0].Value,
                Confidence = confidence,
                Energy = energy,
                Recall = recall,
            });
        }

        return new FieldSuggestion(field.Name, candidates, source, field.Policy, confidence, Stopwatch.GetElapsedTime(started), energy, traceId)
        {
            SimilarDocuments = neighbours,
        };
    }

    /// <summary>
    /// The value the most similar settled documents vote for (<see cref="FieldDefinition.SimilarDocumentVotes"/>) — trusted
    /// when its score reaches the threshold, a guess when the field sets none — the documents behind it, and what the lookup
    /// found. The document itself is never its own evidence: one more is asked for, and it is passed over. Only documents
    /// whose value lies in the field's domain vote or are reported, so with a memory that ranks only its nearest document,
    /// a nearest document outside the domain leaves the layer with nothing — as a replay finds with the same memory. The
    /// domain is the document's: it is narrowed by <paramref name="values"/> and <paramref name="sets"/> when the field
    /// takes its candidates from another.
    /// </summary>
    private async Task<(IReadOnlyList<FieldCandidate> Candidates, IReadOnlyList<MemoryMatch> Neighbours, Recall? Recall, double Energy)> SimilarAsync(
        FieldDefinition field,
        string documentId,
        string task,
        string evidence,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, IReadOnlyList<string>> sets,
        string? typed,
        string traceId,
        CancellationToken cancellationToken)
    {
        if (DocumentMemory is not IMemory memory || evidence.Length == 0 || field.Multiple)
        {
            return ([], [], null, 0); // a document memory holds one value per document: it has nothing for a set
        }

        var threshold = field.MemoryThreshold;

        try
        {
            var asked = Math.Max(SimilarDocumentCount, field.SimilarDocumentVotes) + 1;
            var (found, energy) = await memory.NearestAsync(task, evidence, asked, traceId, cancellationToken).ConfigureAwait(false);
            var others = found.Where(m => m.Source != documentId).ToList();
            if (others.Count == 0)
            {
                return ([], [], null, energy);
            }

            var admitted = others.Where(m => field.Admits(m.Answer, values, sets) && FieldMemory.Begins(m.Answer, typed)).ToList();
            var neighbours = admitted.Take(SimilarDocumentCount).ToList();
            if (SimilarVote.Decide(admitted, field.SimilarDocumentVotes) is not ({ } match, var score))
            {
                // Every document found lies outside the domain: it reports what the nearest was, and offers nothing.
                return ([], neighbours, new Recall(others[0].Source, others[0].Similarity, threshold, false), energy);
            }

            // Typed text: only the key layer answers. The floor keeps a vote among documents less like the draft than those the
            // threshold was measured on from being trusted on their agreement alone; a lone voter's score is its similarity.
            var floored = field.MemorySimilarityFloor is { } floor && SimilarVote.Voters(admitted, field.SimilarDocumentVotes) > 1 && match.Similarity < floor;
            var hit = typed is null && score >= threshold && !floored;
            var recall = new Recall(match.Source, score, threshold, hit);
            return ([new FieldCandidate(match.Answer, score, FieldSource.SimilarDocument, match.Source, hit)], neighbours, recall, energy);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], [], Recall.Failed(threshold, error), 0);
        }
    }

    private static string Mode(FieldSource source) => source switch
    {
        FieldSource.SettledFieldMemory => "field_memory",
        FieldSource.SimilarDocument => "memory",
        FieldSource.Model => "model",
        _ => "abstain",
    };

    /// <summary>Replaces the document's contribution to both memories with what its current values imply.</summary>
    internal async Task<double> PutAsync(FormDefinition form, SettledDocument document, string traceId, CancellationToken cancellationToken)
    {
        FieldMemory.Put(form, document);
        if (DocumentMemory is null)
        {
            return 0;
        }

        var energy = 0.0;
        foreach (var field in form.Fields.Where(f => f.Role == FieldRole.Judged))
        {
            var task = TaskName(form, field.Name);
            if (!_cases.TryGetValue(task, out var cases))
            {
                _cases[task] = cases = new Cases();
            }

            var entry = document.Values.TryGetValue(field.Name, out var value)
                ? new CaseEntry(document.DocumentId, Evidence(form, field.Name, document.Values, document.Sets), value, document.SettledAt)
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

    /// <summary>
    /// The values of the fields that support <paramref name="field"/>, in the form's order — a field that takes several
    /// values gives a line for each, ordinally, so the same set reads the same; a field that takes several is supported by
    /// its own values chosen so far.
    /// </summary>
    internal static List<KeyValuePair<string, string>> EvidenceValues(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, IReadOnlyList<string>>? sets = null)
    {
        var target = form.Field(field);
        var evidence = new List<KeyValuePair<string, string>>();
        foreach (var f in form.Fields.Where(f => form.Supports(f.Name, field) || (f.Name == field && target.Multiple)))
        {
            if (f.Multiple)
            {
                if (sets is not null && sets.TryGetValue(f.Name, out var elements))
                {
                    evidence.AddRange(elements.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(e => KeyValuePair.Create(f.Name, e)));
                }
            }
            else if (values.TryGetValue(f.Name, out var value))
            {
                evidence.Add(KeyValuePair.Create(f.Name, value));
            }
        }

        return evidence;
    }

    /// <summary>
    /// Evidence re-ordered to the order its values arrived in. Memory lookups keep the form's order — they match content, and
    /// the same values must find the same documents whatever order they were entered in — but a model reads its evidence as
    /// a history, and a history that only grows at the end keeps an inference server's cached prefix valid.
    /// </summary>
    internal static List<KeyValuePair<string, string>> InArrivalOrder(IReadOnlyList<KeyValuePair<string, string>> evidence, IReadOnlyList<string> arrival)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < arrival.Count; i++)
        {
            rank[arrival[i]] = i;
        }

        return [.. evidence.OrderBy(e => rank.TryGetValue(e.Key, out var r) ? r : int.MaxValue)];
    }

    /// <summary>The <c>name: value</c> lines of the fields that support <paramref name="field"/>, in the form's order.</summary>
    internal static string Evidence(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, IReadOnlyList<string>>? sets = null) =>
        Lines(EvidenceValues(form, field, values, sets));

    /// <summary>Evidence as <c>name: value</c> lines — the request a similar document is looked up by, and a model's input.</summary>
    internal static string Lines(IEnumerable<KeyValuePair<string, string>> evidence) =>
        string.Join('\n', evidence.Select(e => $"{e.Key}: {e.Value}"));
}
