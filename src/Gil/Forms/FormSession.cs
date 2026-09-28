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

    private Task<FieldSuggestion> SuggestAsync(FieldDefinition field, CancellationToken cancellationToken) =>
        _resolver.SuggestFieldAsync(Form, DocumentId, _values, field, cancellationToken);
}
