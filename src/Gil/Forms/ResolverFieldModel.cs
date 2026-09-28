namespace Gil.Forms;

/// <summary>
/// A field model made of the resolver and one task per field: the field's evidence, as <c>name: value</c> lines, is the
/// request. A field with a closed list of values fits a one-layer tree whose leaves are the values. Fields without a task
/// get no model suggestion.
/// </summary>
/// <remarks>
/// Give the resolver the form resolver's sink and name each task <c>form/field</c>: the resolution then closes the
/// suggestion's trace itself, so <see cref="Resolver.FeedbackAsync"/> on the suggestion's trace id credits the tree as it
/// would any request. The resolver puts the request inside its prompt, so a model that could reuse the form's fixed
/// prefix across a document's events does not get to here; that needs a prompt built for it.
/// </remarks>
public sealed class ResolverFieldModel : IFieldModel
{
    private readonly Resolver _resolver;
    private readonly IReadOnlyDictionary<string, TaskDefinition> _tasks;

    /// <param name="resolver">The resolver that judges and generates.</param>
    /// <param name="tasks">Field name to the task that suggests it.</param>
    public ResolverFieldModel(Resolver resolver, IReadOnlyDictionary<string, TaskDefinition> tasks)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(tasks);
        (_resolver, _tasks) = (resolver, tasks);
    }

    public async Task<FieldModelResult> SuggestAsync(
        FormDefinition form,
        string field,
        IReadOnlyList<KeyValuePair<string, string>> evidence,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!_tasks.TryGetValue(field, out var task))
        {
            return new FieldModelResult([], null, 0);
        }

        var resolution = await _resolver.ResolveAsync(task, FormResolver.Lines(evidence), traceId, cancellationToken).ConfigureAwait(false);
        return resolution.Output is null
            ? new FieldModelResult([], null, resolution.Energy)
            : new FieldModelResult([new FieldCandidate(resolution.Output, resolution.Confidence ?? 0, FieldSource.Model, null)], resolution.Confidence, resolution.Energy);
    }
}
