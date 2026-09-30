namespace Gil.Forms;

/// <summary>A similarity threshold chosen for a field's document memory, and how it did on the replay it was chosen on.</summary>
/// <param name="Threshold">The lowest score down to which every band of answers met the target precision.</param>
/// <param name="Precision">The share of answers at or above the threshold that matched the settled value.</param>
/// <param name="AnswerRate">The share of lookups answered at or above the threshold.</param>
/// <param name="Answered">How many lookups were answered.</param>
/// <param name="Lookups">How many lookups the replay made: every document with a settled value, except the first.</param>
public sealed record ThresholdChoice(double Threshold, double Precision, double AnswerRate, int Answered, int Lookups);

/// <summary>
/// What replaying settled documents found for one memory layer of a field: the threshold chosen, if any, and — whether or
/// not one was — how close the layer came, so a field that falls short of the target can say by how much.
/// </summary>
/// <param name="Chosen">The lowest threshold down to which every band of answers met the target precision; null when none does — the layer then should not answer this field on its own.</param>
/// <param name="MostPrecise">
/// The most precise threshold among those resting on at least the minimum number of answers — the highest one that
/// gathers that many; null when the layer found fewer candidates than that at any score. Its precision is the best the
/// layer reached, whether or not that met the target — taken together it can meet the target and still not be chosen,
/// when a band within it falls short.
/// </param>
/// <param name="Lookups">How many lookups the replay made for this layer.</param>
/// <param name="Candidates">How many of them found a candidate at any score.</param>
public sealed record ThresholdReplay(ThresholdChoice? Chosen, ThresholdChoice? MostPrecise, int Lookups, int Candidates);

/// <summary>The replays of a field's two memory layers, in the order the form resolver consults them.</summary>
/// <param name="Key">For <see cref="FieldDefinition.KeyThreshold"/>, replayed on every lookup.</param>
/// <param name="Memory">For <see cref="FieldDefinition.MemoryThreshold"/>, replayed on the lookups the chosen key threshold would not have answered.</param>
public sealed record LayerThresholds(ThresholdReplay Key, ThresholdReplay Memory);

/// <summary>
/// Chooses <see cref="FieldDefinition.MemoryThreshold"/> and <see cref="FieldDefinition.KeyThreshold"/> by replaying settled documents. The right threshold moves as a
/// memory grows — a small memory's nearest document is rarely close, a large one's often is — so a threshold fixed once
/// loses answers it could give; choose it again as the memory grows (for instance whenever it has grown by a tenth).
/// </summary>
public static class ThresholdSelection
{
    /// <summary>
    /// Replays the documents in the order they were settled: each is looked up in a memory holding only the documents
    /// settled before it, by the evidence lines a session would send, and then remembered — replacing an earlier document
    /// of the same case, as the form resolver does. Chooses the lowest threshold down to which every band of answers
    /// reaches <paramref name="targetPrecision"/> — precision fitted as a non-decreasing function of similarity, so a weak
    /// band is not admitted on the strength of good answers above it — with at least <paramref name="minimumAnswered"/>
    /// answers in all. When no threshold does, <see cref="ThresholdReplay.Chosen"/> is null and memory should not answer
    /// this field on its own; <see cref="ThresholdReplay.MostPrecise"/> still says how close it came. On its own this
    /// is right only for a field without a <see cref="FieldDefinition.KeyThreshold"/>: with one, a similar document answers
    /// only where no key did — choose both with <see cref="SelectLayersAsync"/>.
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
    public static async Task<ThresholdReplay> SelectAsync(
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
        Check(form, field, targetPrecision, minimumAnswered);

        var steps = await ReplayAsync(null, memory, form, field, documents, cancellationToken).ConfigureAwait(false);
        return Similarity(steps.Where(s => s.Looked), targetPrecision, minimumAnswered);
    }

    /// <summary>
    /// Chooses both thresholds of a field in the order the form resolver consults its layers. A value under a key answers
    /// first, so <see cref="FieldDefinition.KeyThreshold"/> is chosen as <see cref="SelectKeyThreshold"/> does, on every
    /// lookup. A similar document answers only where no key did, so <see cref="FieldDefinition.MemoryThreshold"/> is
    /// chosen on the lookups the chosen key threshold leaves — those are harder than the rest, and a threshold chosen on
    /// all of them promises more precision than the similar document layer then delivers. One replay serves both: each
    /// document is asked about in memories holding only the documents settled before it, then put into both.
    /// </summary>
    /// <param name="fieldMemory">An empty field memory configured as the one in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="memory">An empty memory of the kind in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="documents">Settled documents, in any order.</param>
    /// <param name="targetPrecision">The share of answers that must match, in (0, 1], for each layer.</param>
    /// <param name="minimumAnswered">The fewest answers a precision may rest on, for each layer; the application's call.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>
    /// A replay of each layer. The memory layer's <see cref="ThresholdReplay.Lookups"/>, and the answer rates of its
    /// thresholds, count only the lookups left to it.
    /// </returns>
    public static async Task<LayerThresholds> SelectLayersAsync(
        FieldMemory fieldMemory,
        IMemory memory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        double targetPrecision,
        int minimumAnswered,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fieldMemory);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        Check(form, field, targetPrecision, minimumAnswered);

        var steps = await ReplayAsync(fieldMemory, memory, form, field, documents, cancellationToken).ConfigureAwait(false);
        var keyed = steps.Where(s => s.KeyLooked).ToList();
        var key = Fit(
            [.. keyed.Where(s => s.Key is not null).Select(s => s.Key!.Value)],
            keyed.Count,
            targetPrecision,
            minimumAnswered);
        var left = steps.Where(s => s.Looked && !(key.Chosen is { } chosen && s.Key is { } first && first.Strength >= chosen.Threshold));
        return new LayerThresholds(key, Similarity(left, targetPrecision, minimumAnswered));
    }

    /// <summary>
    /// Chooses <see cref="FieldDefinition.KeyThreshold"/> the same way: replays the documents in the order they were
    /// settled, asking a field memory holding only the documents settled before each for the best-ranked value under the
    /// document's keys, then putting the document. Chooses the lowest strength down to which every band of the values
    /// asked about reaches <paramref name="targetPrecision"/>, as for similarity, with at least
    /// <paramref name="minimumAnswered"/> of them in all; when no strength does, <see cref="ThresholdReplay.Chosen"/> is
    /// null — the field's keys then should not answer on their own. A document whose keys were never seen before is a lookup without an answer. Repeats of
    /// the same documents answering each other well do not lower the strength that a weakly backed key needs. The key layer
    /// is consulted first, on every lookup, so this is right on its own; <see cref="SelectLayersAsync"/> chooses it the
    /// same way together with the memory threshold.
    /// </summary>
    /// <param name="memory">An empty field memory configured as the one in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="documents">Settled documents, in any order.</param>
    /// <param name="targetPrecision">The share of answers that must match, in (0, 1].</param>
    /// <param name="minimumAnswered">The fewest answers a precision may rest on; the application's call, as for similarity.</param>
    public static ThresholdReplay SelectKeyThreshold(
        FieldMemory memory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        double targetPrecision,
        int minimumAnswered)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        Check(form, field, targetPrecision, minimumAnswered);

        var matches = new List<(double Strength, bool Correct)>();
        var lookups = 0;
        var ordered = documents
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.DocumentId, StringComparer.Ordinal); // the order the field memory weighs settlements in
        foreach (var document in ordered)
        {
            if (!document.Values.TryGetValue(field, out var settled))
            {
                continue;
            }

            if (memory.Count(form.Name) > 0)
            {
                lookups++;
                if (memory.First(form, field, document.Values, settled) is { } first)
                {
                    matches.Add((first.Strength, first.Matches));
                }
            }

            memory.Put(form, document);
        }

        return Fit(matches, lookups, targetPrecision, minimumAnswered);
    }

    /// <summary>One document of a replay: what the field memory and the document memory held before it said about it.</summary>
    private readonly record struct Step(bool KeyLooked, (double Strength, bool Matches)? Key, bool Looked, (double Similarity, bool Correct)? Match);

    /// <summary>
    /// Replays the documents in the order they were settled, asking each memory about each document before putting it in
    /// — the document memory keeping one document per case, as the form resolver does.
    /// </summary>
    private static async Task<List<Step>> ReplayAsync(
        FieldMemory? fieldMemory,
        IMemory memory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        CancellationToken cancellationToken)
    {
        var task = FormResolver.TaskName(form, field);
        var traceId = Guid.NewGuid().ToString("N"); // the replay's cost is its own
        var steps = new List<Step>();
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

            var keyLooked = fieldMemory is not null && fieldMemory.Count(form.Name) > 0;
            var first = keyLooked ? fieldMemory!.First(form, field, document.Values, settled) : null;
            var evidence = FormResolver.Evidence(form, field, document.Values);
            (double, bool)? match = null;
            if (remembered > 0)
            {
                var (found, _) = await memory.LookupAsync(task, evidence, traceId, cancellationToken).ConfigureAwait(false);
                match = found is null ? null : (found.Similarity, found.Answer == settled);
            }

            steps.Add(new Step(keyLooked, first, remembered > 0, match));
            fieldMemory?.Put(form, document);
            var key = FormResolver.CaseKey(evidence);
            if (cases.TryGetValue(key, out var earlier))
            {
                memory.Forget(task, earlier);
            }

            cases[key] = document.DocumentId;
            await memory.RememberAsync(task, document.DocumentId, evidence, settled, traceId, cancellationToken).ConfigureAwait(false);
            remembered++;
        }

        return steps;
    }

    /// <summary>The similarity choice over the given lookups: every one counts, answered or not.</summary>
    private static ThresholdReplay Similarity(IEnumerable<Step> looked, double targetPrecision, int minimumAnswered)
    {
        var steps = looked.ToList();
        return Fit([.. steps.Where(s => s.Match is not null).Select(s => s.Match!.Value)], steps.Count, targetPrecision, minimumAnswered);
    }

    private static void Check(FormDefinition form, string field, double targetPrecision, int minimumAnswered)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetPrecision, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPrecision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAnswered, 1);
        if (form.Field(field).Role != FieldRole.Judged)
        {
            throw new ArgumentException($"'{field}' is not a judged field.", nameof(field));
        }
    }

    /// <summary>
    /// Fits precision as a non-decreasing function of the score (isotonic regression by pooling adjacent violators), then
    /// walks the fitted blocks from the highest score down while each block's own precision meets the target. The
    /// threshold is the lowest score of the last such block. Checking every block, not only the answers taken together,
    /// keeps a band of weak answers from being admitted on the strength of many good ones above it. The fitted precision
    /// falls from the top block down, so the answers above a block are at their most precise at the first block that
    /// gathers <paramref name="minimumAnswered"/> of them.
    /// </summary>
    private static ThresholdReplay Fit(List<(double Score, bool Correct)> matches, int lookups, double targetPrecision, int minimumAnswered)
    {
        var blocks = new List<(double Lowest, int Answered, int Correct)>();
        foreach (var group in matches.GroupBy(m => m.Score).OrderBy(g => g.Key))
        {
            var block = (Lowest: group.Key, Answered: group.Count(), Correct: group.Count(m => m.Correct));
            while (blocks.Count > 0 && (double)blocks[^1].Correct / blocks[^1].Answered >= (double)block.Correct / block.Answered)
            {
                var below = blocks[^1];
                blocks.RemoveAt(blocks.Count - 1);
                block = (below.Lowest, below.Answered + block.Answered, below.Correct + block.Correct);
            }

            blocks.Add(block);
        }

        ThresholdChoice? chosen = null;
        ThresholdChoice? mostPrecise = null;
        var meets = true;
        var (answered, correct) = (0, 0);
        for (var i = blocks.Count - 1; i >= 0 && (meets || mostPrecise is null); i--)
        {
            answered += blocks[i].Answered;
            correct += blocks[i].Correct;
            meets &= (double)blocks[i].Correct / blocks[i].Answered >= targetPrecision;
            if (answered >= minimumAnswered)
            {
                var threshold = new ThresholdChoice(blocks[i].Lowest, (double)correct / answered, (double)answered / lookups, answered, lookups);
                mostPrecise ??= threshold;
                if (meets)
                {
                    chosen = threshold;
                }
            }
        }

        return new ThresholdReplay(chosen, mostPrecise, lookups, matches.Count);
    }
}
