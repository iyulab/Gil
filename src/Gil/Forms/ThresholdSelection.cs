namespace Gil.Forms;

/// <summary>A similarity threshold chosen for a field's document memory, and how it did on the replay it was chosen on.</summary>
/// <param name="Threshold">The lowest similarity at which answering met the target precision.</param>
/// <param name="Precision">The share of answers at or above the threshold that matched the settled value.</param>
/// <param name="AnswerRate">The share of lookups answered at or above the threshold.</param>
/// <param name="Answered">How many lookups were answered.</param>
/// <param name="Lookups">How many lookups the replay made: every document with a settled value, except the first.</param>
public sealed record ThresholdChoice(double Threshold, double Precision, double AnswerRate, int Answered, int Lookups);

/// <summary>
/// Chooses <see cref="FieldDefinition.MemoryThreshold"/> by replaying settled documents. The right threshold moves as a
/// memory grows — a small memory's nearest document is rarely close, a large one's often is — so a threshold fixed once
/// loses answers it could give; choose it again as the memory grows (for instance whenever it has grown by a tenth).
/// </summary>
public static class ThresholdSelection
{
    /// <summary>
    /// Replays the documents in the order they were settled: each is looked up in a memory holding only the documents
    /// settled before it, by the evidence lines a session would send, and then remembered — replacing an earlier document
    /// of the same case, as the form resolver does. Returns the lowest threshold at which the answers given
    /// reach <paramref name="targetPrecision"/> with at least <paramref name="minimumAnswered"/> of them, or null when no
    /// threshold does — memory then should not answer this field on its own.
    /// </summary>
    /// <param name="memory">An empty memory of the kind in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="documents">Settled documents, in any order.</param>
    /// <param name="targetPrecision">The share of answers that must match, in (0, 1].</param>
    /// <param name="minimumAnswered">
    /// The fewest answers a precision may rest on. There is no default: a precision from a handful of answers is noise,
    /// and how much noise the field tolerates is the application's call.
    /// </param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    public static async Task<ThresholdChoice?> SelectAsync(
        IMemory memory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        double targetPrecision,
        int minimumAnswered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPrecision, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPrecision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAnswered, 1);
        if (form.Field(field).Role != FieldRole.Judged)
        {
            throw new ArgumentException($"'{field}' is not a judged field.", nameof(field));
        }

        var task = FormResolver.TaskName(form, field);
        var traceId = Guid.NewGuid().ToString("N"); // the replay's cost is its own
        var matches = new List<(double Similarity, bool Correct)>();
        var lookups = 0;
        var remembered = 0;
        var cases = new Dictionary<string, string>(StringComparer.Ordinal); // case → the document representing it
        var ordered = documents
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.DocumentId, StringComparer.Ordinal); // the form resolver's order: later wins, then the larger id
        foreach (var document in ordered)
        {
            if (!document.Values.TryGetValue(field, out var settled))
            {
                continue;
            }

            var evidence = FormResolver.Evidence(form, field, document.Values);
            if (remembered > 0)
            {
                lookups++;
                var (match, _) = await memory.LookupAsync(task, evidence, traceId, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                {
                    matches.Add((match.Similarity, match.Answer == settled));
                }
            }

            var key = FormResolver.CaseKey(evidence);
            if (cases.TryGetValue(key, out var earlier))
            {
                memory.Forget(task, earlier);
            }

            cases[key] = document.DocumentId;
            await memory.RememberAsync(task, document.DocumentId, evidence, settled, traceId, cancellationToken).ConfigureAwait(false);
            remembered++;
        }

        // Walk thresholds from the highest similarity down; the last one still meeting the target is the lowest.
        ThresholdChoice? choice = null;
        var (answered, correct) = (0, 0);
        foreach (var group in matches.GroupBy(m => m.Similarity).OrderByDescending(g => g.Key))
        {
            answered += group.Count();
            correct += group.Count(m => m.Correct);
            var precision = (double)correct / answered;
            if (answered >= minimumAnswered && precision >= targetPrecision)
            {
                choice = new ThresholdChoice(group.Key, precision, (double)answered / lookups, answered, lookups);
            }
        }

        return choice;
    }
}
