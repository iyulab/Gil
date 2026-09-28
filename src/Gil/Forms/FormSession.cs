using System.Diagnostics;

namespace Gil.Forms;

/// <summary>
/// One open document of a form. The form is given once; field values arrive afterwards as events — observed values
/// through <see cref="ObserveAsync"/>, judged values through <see cref="SettleAsync"/> — and after each the document is put
/// into memory again and the fields whose evidence changed are suggested afresh.
/// </summary>
/// <remarks>
/// A settled value can be settled again: accepting a suggestion and correcting it later leaves only the correction in
/// memory, because the document's contribution is always what its current values imply. Accepting or correcting a field
/// moves the document's settlement time to the present; observing, restoring, rejecting and reverting do not. A rejected field is not
/// suggested again until it is reverted or settled. Not thread-safe.
/// </remarks>
public sealed class FormSession
{
    private readonly FormResolver _resolver;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private DateTimeOffset _settledAt;

    internal FormSession(FormResolver resolver, FormDefinition form, string documentId, DateTimeOffset settledAt) =>
        (_resolver, Form, DocumentId, _settledAt) = (resolver, form, documentId, settledAt);

    public FormDefinition Form { get; }

    public string DocumentId { get; }

    /// <summary>
    /// Sets an observed field's value, or clears it with null, and returns fresh suggestions for the open judged fields it
    /// is evidence for.
    /// </summary>
    /// <exception cref="ArgumentException">The field is not an observed field of the form.</exception>
    public async Task<IReadOnlyList<FieldSuggestion>> ObserveAsync(string field, string? value, CancellationToken cancellationToken = default)
    {
        if (Form.Field(field).Role != FieldRole.Observed)
        {
            throw new ArgumentException($"'{field}' is judged; settle it instead.", nameof(field));
        }

        Set(field, value);
        return await ChangedAsync(field, reopened: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles a judged field and returns fresh suggestions for the open judged fields whose evidence changed — and for the
    /// field itself when a revert reopened it.
    /// </summary>
    /// <exception cref="ArgumentException">The field is not a judged field of the form.</exception>
    public async Task<IReadOnlyList<FieldSuggestion>> SettleAsync(string field, Settlement settlement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        if (Form.Field(field).Role != FieldRole.Judged)
        {
            throw new ArgumentException($"'{field}' is observed; observe it instead.", nameof(field));
        }

        _rejected.Remove(field);
        switch (settlement.Kind)
        {
            case SettlementKind.Reject:
                Set(field, null);
                _rejected.Add(field);
                break;
            case SettlementKind.Revert:
                Set(field, null);
                break;
            case SettlementKind.Restore:
                Set(field, settlement.Value); // a saved value keeps the saved document's time
                break;
            default:
                Set(field, settlement.Value);
                _settledAt = _resolver.Time.GetUtcNow();
                break;
        }

        return await ChangedAsync(field, reopened: settlement.Kind == SettlementKind.Revert, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Suggestions for every open judged field: not settled, not rejected, and not <see cref="FieldPolicy.Off"/>.</summary>
    public async Task<IReadOnlyList<FieldSuggestion>> SuggestAsync(CancellationToken cancellationToken = default)
    {
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in Form.Fields.Where(IsOpen))
        {
            suggestions.Add(await SuggestAsync(field, cancellationToken).ConfigureAwait(false));
        }

        return suggestions;
    }

    /// <summary>
    /// The document's values as they stand — observed and settled — which is what the application saves, with the time a
    /// field was last accepted or corrected (or the time the document was opened with, if none was since).
    /// </summary>
    public SettledDocument Snapshot() => new(DocumentId, new Dictionary<string, string>(_values, StringComparer.Ordinal), _settledAt);

    private void Set(string field, string? value)
    {
        if (value is null)
        {
            _values.Remove(field);
        }
        else
        {
            _values[field] = value;
        }
    }

    private async Task<IReadOnlyList<FieldSuggestion>> ChangedAsync(string changed, bool reopened, CancellationToken cancellationToken)
    {
        await _resolver.PutAsync(Form, Snapshot(), Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in Form.Fields.Where(IsOpen))
        {
            if (Form.Supports(changed, field.Name) || (reopened && field.Name == changed))
            {
                suggestions.Add(await SuggestAsync(field, cancellationToken).ConfigureAwait(false));
            }
        }

        return suggestions;
    }

    private bool IsOpen(FieldDefinition field) =>
        field.Role == FieldRole.Judged
        && field.Policy != FieldPolicy.Off
        && !_values.ContainsKey(field.Name)
        && !_rejected.Contains(field.Name);

    private async Task<FieldSuggestion> SuggestAsync(FieldDefinition field, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var traceId = Guid.NewGuid().ToString("N");
        var task = FormResolver.TaskName(Form, field.Name);
        var evidence = FormResolver.EvidenceValues(Form, field.Name, _values);
        var lines = FormResolver.Lines(evidence);
        _resolver.Sink?.OpenTrace(traceId, task, lines); // opened first: a model resolving under the same id closes it

        var remembered = _resolver.FieldMemory.Rank(Form, field.Name, _values, _resolver.CandidateCount);
        var keyed = remembered.Where(c => c.Evidence is not null).ToList();
        var (similar, recall, energy) = await SimilarAsync(field, task, lines, traceId, cancellationToken).ConfigureAwait(false);

        // A model only where neither memory had evidence: a value backed by what the document says beats a model's guess.
        FieldModelResult? modelled = null;
        if (_resolver.Model is IFieldModel model && keyed.Count == 0 && similar.Count == 0)
        {
            modelled = await model.SuggestAsync(Form, field.Name, evidence, traceId, cancellationToken).ConfigureAwait(false);
            energy += modelled.Energy;
        }

        // Values backed by a known key first, then a similar document, then the model; the field's overall frequency
        // comes last, being a guess without evidence.
        var candidates = keyed
            .Concat(similar)
            .Concat(modelled?.Candidates ?? [])
            .Concat(remembered.Where(c => c.Evidence is null))
            .DistinctBy(c => c.Value, StringComparer.Ordinal)
            .Take(_resolver.CandidateCount)
            .ToList();
        var source = candidates.Count > 0 ? candidates[0].Source : FieldSource.None;
        var confidence = source == FieldSource.Model ? modelled?.Confidence : null;

        // A model that resolved under this id through the same sink has closed the trace with its own outcome.
        if (_resolver.Sink is ITelemetrySink sink && (modelled is null || sink.FindTrace(traceId) is null))
        {
            sink.CloseTrace(traceId, new TraceOutcome
            {
                Mode = Mode(source),
                Output = candidates.Count > 0 ? candidates[0].Value : null,
                Confidence = confidence,
                Energy = energy,
                Recall = recall,
            });
        }

        return new FieldSuggestion(field.Name, candidates, source, field.Policy, confidence, Stopwatch.GetElapsedTime(started), energy, traceId);
    }

    /// <summary>The nearest similar settled document's value when it is similar enough, and what the lookup found.</summary>
    private async Task<(IReadOnlyList<FieldCandidate> Candidates, Recall? Recall, double Energy)> SimilarAsync(
        FieldDefinition field, string task, string evidence, string traceId, CancellationToken cancellationToken)
    {
        if (_resolver.DocumentMemory is not IMemory memory || field.MemoryThreshold is not double threshold || evidence.Length == 0)
        {
            return ([], null, 0);
        }

        try
        {
            var (match, energy) = await memory.LookupAsync(task, evidence, traceId, cancellationToken).ConfigureAwait(false);
            // The document itself is never its own evidence.
            if (match is null || match.Source == DocumentId)
            {
                return ([], null, energy);
            }

            var hit = match.Similarity >= threshold;
            var recall = new Recall(match.Source, match.Similarity, threshold, hit);
            return (hit ? [new FieldCandidate(match.Answer, match.Similarity, FieldSource.SimilarDocument, match.Source)] : [], recall, energy);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return ([], Recall.Failed(threshold, error), 0);
        }
    }

    private static string Mode(FieldSource source) => source switch
    {
        FieldSource.SettledFieldMemory => "field_memory",
        FieldSource.SimilarDocument => "memory",
        FieldSource.Model => "model",
        _ => "abstain",
    };
}
