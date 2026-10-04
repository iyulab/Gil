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
/// suggested again until it is reverted or settled. A field that takes several values (<see cref="FieldDefinition.Multiple"/>)
/// stays open once settled: the values chosen so far are evidence for the rest, which go on being suggested — settling it
/// again with more or fewer values replaces them. Not thread-safe.
/// </remarks>
public sealed class FormSession
{
    private readonly FormResolver _resolver;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _sets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private readonly List<string> _arrival = []; // fields with a value, in the order their current values arrived
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
    /// <exception cref="ArgumentException">
    /// The field is not a judged field of the form, or the settlement is of the other kind of field: values of a set for a
    /// single-valued field, or a single value for a field that takes several.
    /// </exception>
    public async Task<IReadOnlyList<FieldSuggestion>> SettleAsync(string field, Settlement settlement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var definition = Form.Field(field);
        if (definition.Role != FieldRole.Judged)
        {
            throw new ArgumentException($"'{field}' is observed; observe it instead.", nameof(field));
        }

        var clears = settlement.Kind is SettlementKind.Reject or SettlementKind.Revert;
        if (!clears && settlement.IsSet != definition.Multiple)
        {
            throw new ArgumentException(
                definition.Multiple ? $"'{field}' takes several values; settle it with Settlement.Set." : $"'{field}' takes one value; settle it with a single value.",
                nameof(settlement));
        }

        _rejected.Remove(field);
        if (clears)
        {
            Clear(field);
            if (settlement.Kind == SettlementKind.Reject)
            {
                _rejected.Add(field);
            }
        }
        else
        {
            if (definition.Multiple)
            {
                SetMany(field, settlement.Values!);
            }
            else
            {
                Set(field, settlement.Value);
            }

            if (settlement.Kind != SettlementKind.Restore)
            {
                _settledAt = _resolver.Time.GetUtcNow(); // a saved value keeps the saved document's time
            }
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
    /// Suggestions for every open judged field while a person types into some: <paramref name="typed"/> holds, for each such
    /// field, the text typed so far — it stays open, as typing is not settling — and its suggestion offers only values that
    /// begin with it (ignoring case), the key layer held to <see cref="FieldDefinition.KeyThresholdFor"/> that many
    /// characters, as <see cref="FormResolver.SuggestAsync(FormDefinition, string, IReadOnlyDictionary{string, string}, IReadOnlyDictionary{string, IReadOnlyList{string}}, IReadOnlyDictionary{string, string}, CancellationToken)"/> describes.
    /// </summary>
    /// <exception cref="ArgumentException">Text is typed into a field that is not judged or already has a value.</exception>
    public async Task<IReadOnlyList<FieldSuggestion>> SuggestAsync(IReadOnlyDictionary<string, string> typed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typed);
        FormResolver.Typed(Form, typed, f => !Form.Field(f).Multiple && _values.ContainsKey(f));
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in Form.Fields.Where(IsOpen))
        {
            suggestions.Add(await _resolver.SuggestFieldAsync(Form, DocumentId, _values, field, cancellationToken, _arrival, _sets, typed.GetValueOrDefault(field.Name)).ConfigureAwait(false));
        }

        return suggestions;
    }

    /// <summary>
    /// The document's values as they stand — observed and settled — which is what the application saves, with the time a
    /// field was last accepted or corrected (or the time the document was opened with, if none was since), and the order
    /// the values arrived in, by which choosing thresholds replays the document.
    /// </summary>
    public SettledDocument Snapshot() =>
        new(DocumentId, new Dictionary<string, string>(_values, StringComparer.Ordinal), _settledAt, [.. _arrival])
        {
            Sets = new Dictionary<string, IReadOnlyList<string>>(_sets, StringComparer.Ordinal),
        };

    private void Clear(string field)
    {
        _sets.Remove(field);
        Set(field, null);
    }

    /// <summary>A set field's values: they arrive together, as one value would — the field holds one place in the arrival order.</summary>
    private void SetMany(string field, IReadOnlyList<string> values)
    {
        _arrival.Remove(field);
        if (values.Count == 0)
        {
            _sets.Remove(field);
            return;
        }

        _sets[field] = values;
        _arrival.Add(field);
    }

    private void Set(string field, string? value)
    {
        _arrival.Remove(field);
        if (value is null)
        {
            _values.Remove(field);
        }
        else
        {
            _values[field] = value;
            _arrival.Add(field); // a changed value is new history: it moves to the end
        }
    }

    private async Task<IReadOnlyList<FieldSuggestion>> ChangedAsync(string changed, bool reopened, CancellationToken cancellationToken)
    {
        await _resolver.PutAsync(Form, Snapshot(), Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
        var suggestions = new List<FieldSuggestion>();
        foreach (var field in Form.Fields.Where(IsOpen))
        {
            // A set's own chosen values are evidence for the rest of it.
            if (Form.Supports(changed, field.Name) || (field.Name == changed && (reopened || field.Multiple)))
            {
                suggestions.Add(await SuggestAsync(field, cancellationToken).ConfigureAwait(false));
            }
        }

        return suggestions;
    }

    private bool IsOpen(FieldDefinition field) =>
        field.Role == FieldRole.Judged
        && field.Policy != FieldPolicy.Off
        && (field.Multiple || !_values.ContainsKey(field.Name))
        && !_rejected.Contains(field.Name);

    private Task<FieldSuggestion> SuggestAsync(FieldDefinition field, CancellationToken cancellationToken) =>
        _resolver.SuggestFieldAsync(Form, DocumentId, _values, field, cancellationToken, _arrival, _sets);
}
