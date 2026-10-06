namespace Gil.Forms;

/// <summary>A threshold chosen for one memory layer of a field, and how it did on the replay it was chosen on.</summary>
/// <param name="Threshold">The lowest score down to which every band of answers met the target precision.</param>
/// <param name="Precision">The share of answers at or above the threshold that matched the settled value.</param>
/// <param name="AnswerRate">The share of lookups answered at or above the threshold.</param>
/// <param name="Answered">How many lookups were answered.</param>
/// <param name="Lookups">How many lookups the replay made: every document with a settled value, except the first.</param>
public sealed record ThresholdChoice(double Threshold, double Precision, double AnswerRate, int Answered, int Lookups)
{
    /// <summary>
    /// For the similar document layer when more than one document votes, the similarity floor to set with the threshold
    /// (<see cref="FieldDefinition.MemorySimilarityFloor"/>): of the answers at or above the threshold, the similarity of the
    /// nearest document voting for the answer that only a hundredth of them fell below, counting the answers more than one
    /// document voted on. Null for the key layer, for a field with a single voter, and when no answer had more than one
    /// voter: a lone voter's score is its similarity, so the threshold already is its floor.
    /// </summary>
    public double? SimilarityFloor { get; init; }
}

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

/// <summary>One set of fields tried for a judged field's <see cref="FieldDefinition.DependsOn"/>, and how its key layer replayed.</summary>
/// <param name="DependsOn">The fields the judged field rested on in this replay.</param>
/// <param name="Replay">The key layer's replay with them, as <see cref="ThresholdSelection.SelectKeyThreshold"/> makes it.</param>
public sealed record DependsOnTrial(IReadOnlyList<string> DependsOn, ThresholdReplay Replay)
{
    /// <summary>
    /// When the field is typed into, the thresholds chosen for typing with these fields and their key threshold, as
    /// <see cref="ThresholdSelection.SelectTypedKeyThresholds"/> makes them; null when typing was not counted.
    /// </summary>
    public TypedKeyThresholds? Typed { get; init; }
}

/// <summary>
/// The fields a judged field's suggestions should rest on (<see cref="FieldDefinition.DependsOn"/>), chosen by replaying
/// its key layer, and the replays the choice was made from.
/// </summary>
/// <param name="DependsOn">
/// The fields to name, in the order they were added; null when no narrower set answered more often than every evidence
/// field together — leave <see cref="FieldDefinition.DependsOn"/> unset.
/// </param>
/// <param name="Chosen">The key layer's replay with the choice — with every evidence field when <paramref name="DependsOn"/> is null.</param>
/// <param name="AllFields">The key layer's replay with every evidence field, as the field stands without <see cref="FieldDefinition.DependsOn"/>.</param>
/// <param name="Trials">Every set tried: each evidence field alone, in the form's order, then each set the greedy additions tried.</param>
public sealed record DependsOnChoice(IReadOnlyList<string>? DependsOn, ThresholdReplay Chosen, ThresholdReplay AllFields, IReadOnlyList<DependsOnTrial> Trials)
{
    /// <summary>
    /// When typing was counted, the thresholds for typing chosen with the choice — set them as
    /// <see cref="FieldDefinition.TypedKeyThresholds"/> with <see cref="Chosen"/>'s threshold as the key threshold.
    /// </summary>
    public TypedKeyThresholds? Typed { get; init; }
}

/// <summary>The key thresholds chosen for typing into a field (<see cref="FieldDefinition.TypedKeyThresholds"/>).</summary>
/// <param name="ByLength">
/// The key layer's replay with one character typed, then two, and so on — each over the documents a person types that far,
/// those the thresholds before did not answer rightly.
/// </param>
public sealed record TypedKeyThresholds(IReadOnlyList<ThresholdReplay> ByLength)
{
    /// <summary>The thresholds to set as <see cref="FieldDefinition.TypedKeyThresholds"/>: each replay's chosen one, null where none was.</summary>
    public IReadOnlyList<double?> Thresholds => [.. ByLength.Select(r => r.Chosen?.Threshold)];
}

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
    /// settled before it, by the evidence lines its last suggestion was made from — the values that had arrived before
    /// the field (<see cref="SettledDocument.Arrival"/>), its nearest documents voting as a suggestion's do
    /// (<see cref="FieldDefinition.SimilarDocumentVotes"/>) — and then remembered with all its values, replacing an earlier
    /// document of the same case, as the form resolver does. Chooses the lowest threshold down to which every band of answers
    /// reaches <paramref name="targetPrecision"/> — precision fitted as a non-decreasing function of the layer's score, so a weak
    /// band is not admitted on the strength of good answers above it — with at least <paramref name="minimumAnswered"/>
    /// answers in all. When no threshold does, <see cref="ThresholdReplay.Chosen"/> is null and memory should not answer
    /// this field on its own; <see cref="ThresholdReplay.MostPrecise"/> still says how close it came. On its own this
    /// is right only for a field without a <see cref="FieldDefinition.KeyThreshold"/>: with one, a similar document answers
    /// only where no key did — choose both with <see cref="SelectLayersAsync"/>.
    /// </summary>
    /// <param name="memory">An empty memory of the kind in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="documents">Settled documents, in any order. One settled more than once under the same id is asked about each time without its earlier version as evidence, as a suggestion for a saved document passes over it.</param>
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
        NotASet(form, field);

        var steps = await ReplayAsync(null, memory, form, field, documents, cancellationToken).ConfigureAwait(false);
        return Similarity(steps.Where(s => s.Looked), form.Field(field), targetPrecision, minimumAnswered);
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
    /// <param name="documents">Settled documents, in any order. One settled more than once under the same id is asked about each time without its earlier version as evidence, as a suggestion for a saved document passes over it.</param>
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
        NotASet(form, field);

        var steps = await ReplayAsync(fieldMemory, memory, form, field, documents, cancellationToken).ConfigureAwait(false);
        var keyed = steps.Where(s => s.KeyLooked).ToList();
        var key = Fit(
            [.. keyed.Where(s => s.Key is not null).Select(s => s.Key!.Value)],
            keyed.Count,
            targetPrecision,
            minimumAnswered);
        var left = steps.Where(s => s.Looked && !(key.Chosen is { } chosen && s.Key is { } first && first.Score >= chosen.Threshold));
        return new LayerThresholds(key, Similarity(left, form.Field(field), targetPrecision, minimumAnswered));
    }

    /// <summary>
    /// Chooses the thresholds of a field's coarse level (<see cref="FieldDefinition.Coarse"/>) in the order a suggestion
    /// offers it: on the lookups the field's value layers leave unanswered at the thresholds it has
    /// (<see cref="FieldDefinition.KeyThreshold"/>, <see cref="FieldDefinition.MemoryThreshold"/> with its floor — choose
    /// those first), the prefix the keys back, as <see cref="SelectKeyThreshold"/> chooses for a value; on the lookups that
    /// leaves, the prefix the similar documents vote for, as <see cref="SelectAsync"/> does, with its similarity floor. A
    /// prefix is right when the settled value begins with it. Each document is asked about in memories holding only the
    /// documents settled before it, then put into both, as for the value.
    /// </summary>
    /// <param name="fieldMemory">An empty field memory configured as the one in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="memory">An empty memory of the kind in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form, with the field's value thresholds set.</param>
    /// <param name="field">A judged field of the form with a coarse level.</param>
    /// <param name="documents">Settled documents, in any order.</param>
    /// <param name="targetPrecision">The share of prefixes offered that must be right, in (0, 1], for each layer.</param>
    /// <param name="minimumAnswered">The fewest answers a precision may rest on, for each layer; the application's call.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>
    /// For <see cref="CoarseLevel.KeyThreshold"/>, and for <see cref="CoarseLevel.MemoryThreshold"/> with
    /// <see cref="ThresholdChoice.SimilarityFloor"/> for <see cref="CoarseLevel.MemorySimilarityFloor"/>. Their
    /// <see cref="ThresholdReplay.Lookups"/> count only the lookups left to each.
    /// </returns>
    public static async Task<LayerThresholds> SelectCoarseAsync(
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
        if (form.Field(field).Coarse is null)
        {
            throw new ArgumentException($"'{field}' has no coarse level to choose thresholds for.", nameof(field));
        }

        var steps = await ReplayAsync(fieldMemory, memory, form, field, documents, cancellationToken, coarse: true).ConfigureAwait(false);
        var left = steps.Where(s => (s.KeyLooked || s.Looked) && !s.Coarse!.ValueAnswered).ToList();
        var key = Fit([.. left.Where(s => s.Coarse!.Keys is not null).Select(s => s.Coarse!.Keys!.Value)], left.Count, targetPrecision, minimumAnswered);
        var rest = left.Where(s => !(key.Chosen is { } chosen && s.Coarse!.Keys is { } k && k.Score >= chosen.Threshold)).ToList();
        var votes = rest.Where(s => s.Coarse!.Vote is not null).Select(s => s.Coarse!.Vote!.Value).ToList();
        var vote = Fit([.. votes.Select(v => (v.Score, v.Correct))], rest.Count, targetPrecision, minimumAnswered);
        var voted = votes.Where(v => v.Voted).Select(v => (v.Score, v.Nearest)).ToList();
        ThresholdChoice? Floored(ThresholdChoice? choice) => choice is null ? null : choice with { SimilarityFloor = SimilarityFloor(voted, choice.Threshold) };
        return new LayerThresholds(key, vote with { Chosen = Floored(vote.Chosen), MostPrecise = Floored(vote.MostPrecise) });
    }

    /// <summary>
    /// Chooses <see cref="FieldDefinition.KeyThreshold"/> the same way: replays the documents in the order they were
    /// settled, asking a field memory holding only the documents settled before each for the best-ranked value under the
    /// keys of the values that had arrived before the field (<see cref="SettledDocument.Arrival"/>), then putting the
    /// whole document. Chooses the lowest score down to which every band of the values
    /// asked about reaches <paramref name="targetPrecision"/>, as for similarity, with at least
    /// <paramref name="minimumAnswered"/> of them in all; when no score does, <see cref="ThresholdReplay.Chosen"/> is
    /// null — the field's keys then should not answer on their own. A document whose keys were never seen before is a lookup without an answer. Repeats of
    /// the same documents answering each other well do not lower the score that a weakly backed value needs. The key layer
    /// is consulted first, on every lookup, so this is right on its own; <see cref="SelectLayersAsync"/> chooses it the
    /// same way together with the memory threshold.
    /// <para>
    /// A field that takes several values (<see cref="FieldDefinition.Multiple"/>) trusts each value on its own score, so
    /// every value offered under the keys counts, right when it is one of the settled values not yet chosen. A person
    /// picks such values one after another, and those chosen are evidence for the rest, so each document is asked about
    /// once with none of its values chosen, then again after each but the last is chosen — in a fixed order that depends
    /// on the document and its values but not on the order they are listed in. <see cref="ThresholdChoice.Lookups"/> then
    /// counts the values sought — those not yet chosen, summed over the times the document is asked about — and
    /// <see cref="ThresholdChoice.AnswerRate"/> the values answered per value sought.
    /// </para>
    /// </summary>
    /// <param name="memory">An empty field memory configured as the one in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="documents">Settled documents, in any order. One settled more than once under the same id is asked about each time without its earlier version as evidence, as a suggestion for a saved document passes over it.</param>
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

        var multiple = form.Field(field).Multiple;
        var matches = new List<(double Score, bool Correct)>();
        var lookups = 0;
        var ordered = documents
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.DocumentId, StringComparer.Ordinal); // the order the field memory weighs settlements in
        foreach (var document in ordered)
        {
            var settledSet = multiple ? PickOrder(document, field) : [];
            string? settled = null;
            if (multiple ? settledSet.Count == 0 : !document.Values.TryGetValue(field, out settled))
            {
                continue;
            }

            memory.Remove(form.Name, document.DocumentId); // a document settled again is not its own evidence
            if (memory.Count(form.Name) > 0)
            {
                var (before, beforeSets) = (Before(form, document, field), BeforeSets(form, document, field));
                if (!multiple)
                {
                    lookups++;
                    if (memory.First(form, field, before, settled!, beforeSets) is { } first)
                    {
                        matches.Add((first.Score, first.Matches));
                    }
                }
                else
                {
                    for (var chosen = 0; chosen < settledSet.Count; chosen++)
                    {
                        var sets = new Dictionary<string, IReadOnlyList<string>>(beforeSets, StringComparer.Ordinal) { [field] = settledSet[..chosen] };
                        var sought = new HashSet<string>(settledSet[chosen..], StringComparer.Ordinal);
                        lookups += sought.Count;
                        matches.AddRange(memory.Scored(form, field, before, sets).Select(s => (s.Score, sought.Contains(s.Value))));
                    }
                }
            }

            memory.Put(form, document);
        }

        return Fit(matches, lookups, targetPrecision, minimumAnswered);
    }

    /// <summary>
    /// Chooses the key thresholds that hold while a person types into the field
    /// (<see cref="FieldDefinition.TypedKeyThresholds"/>). Typed text narrows the field to the values that begin with it, so
    /// with each character typed the key layer is a different question, with a threshold of its own. A person types only
    /// where the suggestion before did not do — the threshold for one character is worth only what it does on the
    /// documents its <see cref="FieldDefinition.KeyThreshold"/> did not answer rightly, and so on — and a threshold chosen
    /// on every document promises more than it keeps there, as the documents left are the harder ones. So the replay
    /// supposes a person who types the settled value from its start: each document is asked about with none of it typed,
    /// against the field's <see cref="FieldDefinition.KeyThreshold"/> as it stands, then with one character, two, up to
    /// <paramref name="longest"/>, and the threshold for each length is chosen as <see cref="SelectKeyThreshold"/> chooses,
    /// on the documents not yet answered rightly and long enough to type that far. A document a shorter length trusted a
    /// wrong value on is left out of each length whose typed text still leads to that value: a suggestion keeps a value
    /// trusted with fewer characters typed, so the person is shown that same value there, not a new answer. Set
    /// <see cref="FieldDefinition.KeyThreshold"/> first: these thresholds follow it, and must be chosen again when it changes.
    /// </summary>
    /// <param name="memory">An empty field memory configured as the one in use; the replay fills it. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form that takes one value.</param>
    /// <param name="documents">Settled documents, in any order, as for <see cref="SelectKeyThreshold"/>.</param>
    /// <param name="targetPrecision">The share of answers that must match, in (0, 1].</param>
    /// <param name="minimumAnswered">The fewest answers a precision may rest on, for each length.</param>
    /// <param name="longest">The most characters typed a threshold is chosen for.</param>
    /// <exception cref="ArgumentException">The field takes several values.</exception>
    public static TypedKeyThresholds SelectTypedKeyThresholds(
        FieldMemory memory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        double targetPrecision,
        int minimumAnswered,
        int longest = 3)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        Check(form, field, targetPrecision, minimumAnswered);
        ArgumentOutOfRangeException.ThrowIfLessThan(longest, 1);
        var definition = form.Field(field);
        if (definition.Multiple)
        {
            throw new ArgumentException($"Field '{field}' takes several values; typed key thresholds are for a field that takes one.", nameof(field));
        }

        // Every document asked about: what the key layer's best value was with none of the settled value typed, then
        // with each length of it.
        var asked = new List<Asked>();
        var ordered = documents
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.DocumentId, StringComparer.Ordinal); // the order the field memory weighs settlements in
        foreach (var document in ordered)
        {
            if (!document.Values.TryGetValue(field, out var settled))
            {
                continue;
            }

            memory.Remove(form.Name, document.DocumentId); // a document settled again is not its own evidence
            if (memory.Count(form.Name) > 0)
            {
                var (before, beforeSets) = (Before(form, document, field), BeforeSets(form, document, field));
                asked.Add(new Asked(settled, memory.Typed(form, field, before, settled, longest, beforeSets)));
            }

            memory.Put(form, document);
        }

        // A value trusted with fewer characters typed is one claim, kept while the typed text still leads to it (see
        // FieldMemory.Rank). A document so answered rightly needs no more typing; one answered wrongly by a value that the
        // next characters still lead to is shown that same claim again, not asked anew, so it is not among the documents a
        // longer threshold is chosen on until the typing has left that value behind.
        foreach (var a in asked)
        {
            a.Claim(a.Best[0], definition.KeyThreshold);
        }

        var byLength = new List<ThresholdReplay>();
        for (var length = 1; length <= longest; length++)
        {
            var reaching = asked.Where(a => !a.Right && a.Best.Length > length && !a.Holds(length)).ToList();
            var matches = reaching
                .Where(a => a.Best[length] is not null)
                .Select(a => (a.Best[length]!.Value.Score, a.Best[length]!.Value.Value == a.Settled))
                .ToList();
            var replay = Fit(matches, reaching.Count, targetPrecision, minimumAnswered);
            byLength.Add(replay);
            foreach (var a in reaching)
            {
                a.Claim(a.Best[length], replay.Chosen?.Threshold);
            }
        }

        return new TypedKeyThresholds(byLength);
    }

    /// <summary>
    /// A set field's settled values, once each, in the order a replay supposes they were picked: fixed by the document id
    /// and the value, so it depends neither on the order the values are listed in nor on their ordinal order.
    /// </summary>
    private static List<string> PickOrder(SettledDocument document, string field) =>
        document.Sets.TryGetValue(field, out var values)
            ? [.. values.Distinct(StringComparer.Ordinal).OrderBy(v => Fnv1a($"{document.DocumentId}\u001f{v}")).ThenBy(v => v, StringComparer.Ordinal)]
            : [];

    /// <summary>A stable 64-bit FNV-1a hash of the text's UTF-16 code units — the same on every run and platform.</summary>
    private static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Chooses the fields a judged field should rest on (<see cref="FieldDefinition.DependsOn"/>) by how often its key
    /// layer then answers at <paramref name="targetPrecision"/>. A key that rarely decides the field still adds to every
    /// value's score, so where a few fields decide a field with many values and the rest only blur it, resting on those few
    /// answers more often at the same precision. Replays the key layer as <see cref="SelectKeyThreshold"/> does with each
    /// evidence field alone, ranks them by the answers their chosen threshold gives, then adds them in that order while
    /// each addition answers more often. The result names that set only if it answers more often than every evidence field
    /// together; otherwise <see cref="DependsOnChoice.DependsOn"/> is null. It costs one replay per evidence field and one
    /// per addition tried. Choose the field's thresholds again with the fields chosen: a narrower set changes the scores.
    /// A field that is typed into answers questions with typed text too, and the fields that decide it before typing may
    /// blur it once a prefix has narrowed its values: give <paramref name="typedLongest"/>, and each set is counted by the
    /// answers before typing and while typing up to that many characters together (<see cref="SelectTypedKeyThresholds"/>,
    /// with the set's key threshold) — the typed thresholds for the choice come with it.
    /// </summary>
    /// <param name="createMemory">Makes an empty field memory configured as the one in use, one per replay. Never the one serving suggestions.</param>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form; its own <see cref="FieldDefinition.DependsOn"/> is ignored.</param>
    /// <param name="documents">Settled documents, in any order, as for <see cref="SelectKeyThreshold"/>.</param>
    /// <param name="targetPrecision">The share of answers that must match, in (0, 1].</param>
    /// <param name="minimumAnswered">The fewest answers a precision may rest on; the application's call, as for similarity.</param>
    /// <param name="typedLongest">
    /// For a field typed into, the most characters typed to count answers for (as for
    /// <see cref="SelectTypedKeyThresholds"/>); 0, the default, counts answers before typing only.
    /// </param>
    /// <exception cref="ArgumentException">Typing is counted for a field that takes several values.</exception>
    public static DependsOnChoice SelectDependsOn(
        Func<FieldMemory> createMemory,
        FormDefinition form,
        string field,
        IEnumerable<SettledDocument> documents,
        double targetPrecision,
        int minimumAnswered,
        int typedLongest = 0)
    {
        ArgumentNullException.ThrowIfNull(createMemory);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(documents);
        Check(form, field, targetPrecision, minimumAnswered);
        ArgumentOutOfRangeException.ThrowIfNegative(typedLongest);
        if (typedLongest > 0 && form.Field(field).Multiple)
        {
            throw new ArgumentException($"Field '{field}' takes several values; typing is not counted for it.", nameof(typedLongest));
        }

        var saved = documents.ToList();
        var trials = new List<DependsOnTrial>();
        DependsOnTrial Replay(IReadOnlyList<string>? dependsOn)
        {
            var resting = RestingOn(form, field, dependsOn);
            var replay = SelectKeyThreshold(createMemory(), resting, field, saved, targetPrecision, minimumAnswered);
            var typed = typedLongest == 0
                ? null
                : SelectTypedKeyThresholds(
                    createMemory(), With(resting, field, f => f with { KeyThreshold = replay.Chosen?.Threshold }), field, saved, targetPrecision, minimumAnswered, typedLongest);
            return new DependsOnTrial(dependsOn ?? [], replay) { Typed = typed };
        }

        DependsOnTrial Try(IReadOnlyList<string> dependsOn)
        {
            var trial = Replay(dependsOn);
            trials.Add(trial);
            return trial;
        }

        static int Answers(DependsOnTrial trial) =>
            (trial.Replay.Chosen?.Answered ?? 0) + (trial.Typed?.ByLength.Sum(r => r.Chosen?.Answered ?? 0) ?? 0);

        var all = Replay(null);
        var evidence = form.Fields.Where(f => f.Name != field && f.UseAsEvidence).Select(f => f.Name).ToList();
        var ranked = evidence
            .Select(name => Try([name]))
            .OrderByDescending(Answers) // stable: on a tie, the form's order
            .ToList();
        var best = ranked.FirstOrDefault();
        var everyField = new DependsOnChoice(null, all.Replay, all.Replay, trials) { Typed = all.Typed };
        if (best is null || Answers(best) == 0)
        {
            return everyField;
        }

        foreach (var next in ranked.Skip(1))
        {
            if (best.DependsOn.Count + 1 == evidence.Count)
            {
                break; // every evidence field: the replay without DependsOn
            }

            var trial = Try([.. best.DependsOn, next.DependsOn[0]]);
            if (Answers(trial) <= Answers(best))
            {
                break;
            }

            best = trial;
        }

        return Answers(best) > Answers(all)
            ? new DependsOnChoice(best.DependsOn, best.Replay, all.Replay, trials) { Typed = best.Typed }
            : everyField;
    }

    /// <summary>The form with <paramref name="field"/> resting on <paramref name="dependsOn"/> (every evidence field when null).</summary>
    private static FormDefinition RestingOn(FormDefinition form, string field, IReadOnlyList<string>? dependsOn) =>
        With(form, field, f => f with { DependsOn = dependsOn });

    /// <summary>The form with <paramref name="field"/>'s definition changed.</summary>
    private static FormDefinition With(FormDefinition form, string field, Func<FieldDefinition, FieldDefinition> change) =>
        new(form.Name, [.. form.Fields.Select(f => f.Name == field ? change(f) : f)], form.Language);

    /// <summary>One document of a replay: what the field memory and the document memory held before it said about it.</summary>
    private readonly record struct Step(bool KeyLooked, (double Score, bool Matches)? Key, bool Looked, (double Score, bool Correct, double Nearest, bool Voted)? Match)
    {
        /// <summary>For a coarse replay: the level as the keys and the vote back it, and whether the field's value layers answered.</summary>
        public CoarseStep? Coarse { get; init; }
    }

    private sealed record CoarseStep(bool ValueAnswered, (double Score, bool Correct)? Keys, (double Score, bool Correct, double Nearest, bool Voted)? Vote);

    /// <summary>
    /// One document of a typed replay: its settled value, the key layer's best value with none of it typed and with each
    /// length typed (<see cref="FieldMemory.Typed"/>), and what the thresholds chosen so far claimed for it.
    /// </summary>
    private sealed class Asked(string settled, (double Score, string Value)?[] best)
    {
        private readonly List<string> _wrong = [];

        public string Settled { get; } = settled;

        public (double Score, string Value)?[] Best { get; } = best;

        /// <summary>Whether a threshold so far trusted the settled value.</summary>
        public bool Right { get; private set; }

        /// <summary>Records the claim a threshold makes on the best value, if its score reaches it.</summary>
        public void Claim((double Score, string Value)? best, double? threshold)
        {
            if (best is not { } b || !(b.Score >= threshold))
            {
                return;
            }

            if (b.Value == Settled)
            {
                Right = true;
            }
            else
            {
                _wrong.Add(b.Value);
            }
        }

        /// <summary>Whether a wrong claim is still led to with <paramref name="length"/> characters of the settled value typed.</summary>
        public bool Holds(int length) => _wrong.Any(v => FieldMemory.Begins(v, Settled[..length]));
    }

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
        CancellationToken cancellationToken,
        bool coarse = false)
    {
        var task = FormResolver.TaskName(form, field);
        var definition = form.Field(field);
        var traceId = Guid.NewGuid().ToString("N"); // the replay's cost is its own
        var steps = new List<Step>();
        var remembered = 0;
        var cases = new Dictionary<string, string>(StringComparer.Ordinal); // case → the document representing it
        var caseOf = new Dictionary<string, string>(StringComparer.Ordinal); // document → the case it represents
        var ordered = documents
            .OrderBy(d => d.SettledAt)
            .ThenBy(d => d.DocumentId, StringComparer.Ordinal); // the form resolver's order: later wins, then the larger id
        foreach (var document in ordered)
        {
            if (!document.Values.TryGetValue(field, out var settled))
            {
                continue;
            }

            // A document settled again is not its own evidence, as a suggestion for a saved document passes over it.
            fieldMemory?.Remove(form.Name, document.DocumentId);
            if (caseOf.Remove(document.DocumentId, out var own))
            {
                memory.Forget(task, document.DocumentId);
                cases.Remove(own);
                remembered--;
            }

            // Asked with what the field's last suggestion saw; remembered, below, with everything, as the resolver remembers.
            var (before, beforeSets) = (Before(form, document, field), BeforeSets(form, document, field));
            var keyLooked = fieldMemory is not null && fieldMemory.Count(form.Name) > 0;
            var first = keyLooked ? fieldMemory!.First(form, field, before, settled, beforeSets) : null;
            var evidence = FormResolver.Evidence(form, field, document.Values, document.Sets);
            (double, bool, double, bool)? match = null;
            IReadOnlyList<MemoryMatch> admitted = [];
            if (remembered > 0)
            {
                var asked = FormResolver.Evidence(form, field, before, beforeSets);
                var (found, _) = await memory.NearestAsync(task, asked, definition.SimilarDocumentVotes, traceId, cancellationToken).ConfigureAwait(false);
                // As a suggestion does: only documents whose value lies in the field's domain vote, scored as it scores them.
                admitted = [.. found.Where(m => definition.Admits(m.Answer, before, beforeSets))];
                match = SimilarVote.Decide(admitted, definition.SimilarDocumentVotes) is ({ } winner, var score) ? (score, winner.Answer == settled, winner.Similarity, SimilarVote.Voters(admitted, definition.SimilarDocumentVotes) > 1) : null;
            }

            var step = new Step(keyLooked, first, remembered > 0, match);
            if (coarse && definition.Coarse is { } level)
            {
                // As a suggestion: the value layers answer at the field's own thresholds; only where they do not is the level offered.
                var valueAnswered = first is { } f && f.Score >= definition.KeyThreshold
                    || match is { } m && m.Item1 >= definition.MemoryThreshold && !(m.Item4 && definition.MemorySimilarityFloor is { } floor && m.Item3 < floor);
                var cls = level.Of(settled);
                var keys = keyLooked ? CoarseGroups.Keys(fieldMemory!.KeyedValues(form, field, before, knownSets: beforeSets), level) : null;
                var vote = CoarseGroups.Vote(admitted, definition.SimilarDocumentVotes, level);
                step = step with
                {
                    Coarse = new CoarseStep(
                        valueAnswered,
                        keys is { } k ? (k.Score, k.Prefix == cls) : null,
                        vote is { } v ? (v.Score, v.Prefix == cls, v.Nearest.Similarity, v.Voted) : null),
                };
            }

            steps.Add(step);
            fieldMemory?.Put(form, document);
            var key = FormResolver.CaseKey(evidence);
            if (cases.TryGetValue(key, out var earlier))
            {
                memory.Forget(task, earlier);
                caseOf.Remove(earlier);
                remembered--;
            }

            cases[key] = document.DocumentId;
            caseOf[document.DocumentId] = key;
            await memory.RememberAsync(task, document.DocumentId, evidence, settled, traceId, cancellationToken).ConfigureAwait(false);
            remembered++;
        }

        return steps;
    }

    /// <summary>
    /// The document's values that had arrived before <paramref name="field"/> was settled — what its last suggestion was
    /// made from. <see cref="SettledDocument.Arrival"/> says how an unknown order and fields it leaves out are read.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Before(FormDefinition form, SettledDocument document, string field) =>
        Arrived(form, document, field, document.Values);

    /// <summary>The document's sets that had arrived before <paramref name="field"/> was settled, read as <see cref="Before"/> reads values.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> BeforeSets(FormDefinition form, SettledDocument document, string field) =>
        Arrived(form, document, field, document.Sets);

    private static Dictionary<string, T> Arrived<T>(FormDefinition form, SettledDocument document, string field, IReadOnlyDictionary<string, T> values)
    {
        int Rank(string name)
        {
            if (document.Arrival is { } arrival)
            {
                var at = IndexOf(arrival, name);
                return at >= 0 ? at : name == field ? int.MaxValue : -1;
            }

            var index = IndexOf(form.Fields, name); // a value of no field of the form is no evidence anyway
            return index < 0 || form.Fields[index].Role == FieldRole.Observed ? -1 : index;
        }

        var own = Rank(field);
        var arrived = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (name != field && Rank(name) < own)
            {
                arrived[name] = value;
            }
        }

        return arrived;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i] == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<FieldDefinition> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The similarity choice over the given lookups: every one counts, answered or not. With more than one voter each
    /// threshold carries the similarity floor of the answers it rests on (<see cref="ThresholdChoice.SimilarityFloor"/>).
    /// </summary>
    private static ThresholdReplay Similarity(IEnumerable<Step> looked, FieldDefinition definition, double targetPrecision, int minimumAnswered)
    {
        var steps = looked.ToList();
        var matches = steps.Where(s => s.Match is not null).Select(s => s.Match!.Value).ToList();
        var replay = Fit([.. matches.Select(m => (m.Score, m.Correct))], steps.Count, targetPrecision, minimumAnswered);
        if (definition.SimilarDocumentVotes == 1)
        {
            return replay;
        }

        // Only answers decided by a vote say how alike its voters were; a lone voter's score is its similarity already.
        var voted = matches.Where(m => m.Voted).Select(m => (m.Score, m.Nearest)).ToList();
        ThresholdChoice? Floored(ThresholdChoice? choice) => choice is null
            ? null
            : choice with { SimilarityFloor = SimilarityFloor(voted, choice.Threshold) };
        return replay with { Chosen = Floored(replay.Chosen), MostPrecise = Floored(replay.MostPrecise) };
    }

    /// <summary>
    /// The share of a threshold's answers whose nearest voter may fall below its similarity floor. In replays where drafts
    /// were reworded away from the settled documents' words, the lowest similarity alone was pulled down by a single odd
    /// answer and let reworded drafts through; a hundredth kept them below the floor, and a twentieth began to cost answers.
    /// </summary>
    private const double FloorShare = 0.01;

    /// <summary>
    /// Of the answers decided by a vote at or above <paramref name="threshold"/>, the nearest voter's similarity that only
    /// <see cref="FloorShare"/> of them fall below; null when there are none.
    /// </summary>
    internal static double? SimilarityFloor(IEnumerable<(double Score, double Nearest)> answers, double threshold)
    {
        var nearest = answers.Where(a => a.Score >= threshold).Select(a => a.Nearest).Order().ToList();
        return nearest.Count == 0 ? null : nearest[(int)(FloorShare * (nearest.Count - 1))];
    }

    /// <summary>The similar document layer does not suggest a field that takes several values, so it has no threshold to choose.</summary>
    private static void NotASet(FormDefinition form, string field)
    {
        if (form.Field(field).Multiple)
        {
            throw new ArgumentException($"'{field}' takes several values; the similar document layer does not suggest it — choose its key threshold with SelectKeyThreshold.", nameof(field));
        }
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
