using Gil.Memory;

namespace Gil.Forms;

/// <summary>
/// Suggests a judged field's value from how often each value was settled alongside the values the document's other fields
/// have — no model, no call, energy 0. Each other field's value is a key; a value's score is the sum over the document's
/// keys of its strength under that key: its weighted count there over the key's weighted total plus one. The same score
/// ranks the values and decides whether the first one is trusted. When no key has been seen, the field's most frequently
/// settled values stand in.
/// </summary>
/// <remarks>
/// A document contributes as a whole: <see cref="Put"/> replaces whatever it contributed before with what its current
/// values imply, so settling fields one by one and rebuilding from the saved documents reach the same state, in any
/// order and however often either is done. Recent settlements weigh more: under each key, a settlement counts
/// <c>recencyDecay</c> to the power of the number of settlements made under the same key after it, so a correction
/// overtakes the practice it corrects without first having to outnumber it. Ties go to the value settled most recently.
/// Both follow the documents' settlement times, not the order they arrived in. Every other field of the document is a key while learning; only the fields
/// known so far are keys while suggesting. Keys are compared after NFKC, lower-casing and collapsing whitespace, and
/// values longer than <c>maxKeyLength</c> are not keys — free text rarely recurs. Not thread-safe; one instance serves
/// one caller at a time.
/// </remarks>
public sealed class FieldMemory
{
    private readonly int _maxKeyLength;
    private readonly double _recencyDecay;
    private readonly Dictionary<string, FormIndex> _forms = new(StringComparer.Ordinal);

    /// <param name="maxKeyLength">The longest normalised value that is used as a key.</param>
    /// <param name="recencyDecay">
    /// How much a settlement weighs for each later settlement under the same key, in (0, 1]; 1 counts every settlement
    /// alike. The default, 0.95, was chosen by replaying a public stream of about 50,000 settled fields in the order
    /// they were settled: it had fewer wrong first suggestions than plain counting, while 0.9 lost the gain by
    /// forgetting settled practice too fast.
    /// </param>
    public FieldMemory(int maxKeyLength = 60, double recencyDecay = 0.95)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeyLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(recencyDecay, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recencyDecay, 1);
        (_maxKeyLength, _recencyDecay) = (maxKeyLength, recencyDecay);
    }

    /// <summary>How many documents with at least one settled judged field the form's memory holds.</summary>
    public int Count(string form) => _forms.TryGetValue(form, out var index) ? index.Documents : 0;

    /// <summary>
    /// Replaces what the document contributed with what its current values imply. Values of fields the form does not have
    /// are ignored. A field that takes several values settles each of them: under the document's keys, and under each of
    /// its other values — the values of one document weigh alike, as one settlement, whatever order they are listed in.
    /// </summary>
    /// <exception cref="ArgumentException">A field of the form is kept where its kind does not belong (<see cref="SettledDocument.Validate"/>).</exception>
    public void Put(FormDefinition form, SettledDocument document)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(document);
        document.Validate(form);
        var index = Index(form.Name);
        index.Remove(document.DocumentId);

        var tallies = new List<Tally>();
        foreach (var field in form.Fields.Where(f => f.Role == FieldRole.Judged))
        {
            if (field.Multiple)
            {
                var elements = Elements(document.Sets, field.Name);
                var keys = Keys(form, field, document.Values, document.Sets).ToList();
                foreach (var element in elements)
                {
                    var settlement = new Settled(element, document.SettledAt, document.DocumentId);
                    tallies.Add(new Tally(field.Name, null, settlement));
                    tallies.AddRange(keys
                        .Concat(OwnKeys(field, elements.Where(e => e != element)))
                        .Distinct()
                        .Select(key => new Tally(field.Name, key, settlement)));
                }

                continue;
            }

            if (!document.Values.TryGetValue(field.Name, out var value))
            {
                continue;
            }

            var single = new Settled(value, document.SettledAt, document.DocumentId);
            tallies.Add(new Tally(field.Name, null, single));
            tallies.AddRange(Keys(form, field, document.Values, document.Sets).Select(key => new Tally(field.Name, key, single)));
        }

        if (tallies.Count > 0)
        {
            index.Add(document.DocumentId, tallies);
        }
    }

    /// <summary>Removes what the document contributed, if anything.</summary>
    public void Remove(string form, string documentId)
    {
        if (_forms.TryGetValue(form, out var index))
        {
            index.Remove(documentId);
        }
    }

    /// <summary>
    /// The <paramref name="count"/> values most likely for <paramref name="field"/> given the values known so far, best
    /// first: the values settled under the known keys by their score (with the key that backs each most strongly as its
    /// evidence), then, in the places left, the field's most frequently settled values by their share (without evidence).
    /// Ties go to the value settled most recently, then to the ordinally smaller value, in both parts, so the order
    /// documents arrived in never matters. The keyed values are trusted when the first one's score reaches the field's
    /// <see cref="FieldDefinition.KeyThreshold"/>; the most frequent values never are. Only values in the field's domain
    /// for this document (<see cref="FieldDefinition.Candidates"/>, narrowed by <see cref="FieldDefinition.CandidatesFrom"/>)
    /// are ranked. Empty when nothing was settled.
    /// </summary>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="known">The document's values so far; the field's own value, if present, is not evidence for itself.</param>
    /// <param name="count">At most this many.</param>
    /// <param name="excluding">A document whose settlements are left out, so that a saved document is not evidence for itself.</param>
    /// <param name="knownSets">
    /// The values chosen so far of fields that take several (<see cref="FieldDefinition.Multiple"/>). For such a field
    /// itself, its chosen values are evidence for the rest and are not offered again, and each value it is offered is
    /// trusted on its own score — several may be.
    /// </param>
    /// <param name="typed">
    /// The text a person has typed into the field so far, if any: only values that begin with it (ignoring case) are
    /// ranked, and the keyed values are held to <see cref="FieldDefinition.KeyThresholdFor"/> that many characters — but
    /// the best value stays trusted if it was trusted with fewer of them typed, as the text still leads to it.
    /// </param>
    public IReadOnlyList<FieldCandidate> Rank(
        FormDefinition form,
        string field,
        IReadOnlyDictionary<string, string> known,
        int count,
        string? excluding = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? knownSets = null,
        string? typed = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var definition = form.Field(field);
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return [];
        }

        var sets = knownSets ?? NoSets;
        var chosen = definition.Multiple ? new HashSet<string>(Elements(sets, field), StringComparer.Ordinal) : [];
        typed = string.IsNullOrEmpty(typed) ? null : typed;
        var ranked = Keyed(index, form, definition, known, sets, excluding, typed).Where(s => !chosen.Contains(s.Value)).ToList();
        var threshold = definition.KeyThresholdFor(typed?.Length ?? 0);
        var layerTrusted = ranked.Count > 0 && (ranked[0].Score >= threshold || (typed is not null && !definition.Multiple && Claimed(
            Keyed(index, form, definition, known, sets, excluding), definition, typed, ranked[0].Value)));
        var keyed = ranked
            .Take(count)
            .Select(s => new FieldCandidate(
                s.Value, s.Score, FieldSource.SettledFieldMemory, $"{s.Key.Field}: {s.Key.Value}",
                definition.Multiple ? s.Score >= threshold : layerTrusted));
        var overall = index.Values.TryGetValue(new Slot(field, null), out var totals)
            ? totals.Shares(excluding)
                .Where(s => definition.Admits(s.Value, known, sets) && Begins(s.Value, typed) && !chosen.Contains(s.Value))
                .Select(s => new FieldCandidate(s.Value, s.Share, FieldSource.SettledFieldMemory, null, Trusted: false))
            : [];
        return [.. keyed.Concat(overall).DistinctBy(c => c.Value, StringComparer.Ordinal).Take(count)];
    }

    /// <summary>
    /// Every value settled under the known keys with its score and the key that backs it most strongly, best first, as
    /// <see cref="Rank"/> ranks them before taking its count — what a field's coarse level adds up by prefix.
    /// </summary>
    internal IReadOnlyList<(string Value, double Score, string? Evidence)> KeyedValues(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> known, string? excluding = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? knownSets = null)
    {
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return [];
        }

        return [.. Keyed(index, form, form.Field(field), known, knownSets ?? NoSets, excluding).Select(s => (s.Value, s.Score, (string?)$"{s.Key.Field}: {s.Key.Value}"))];
    }

    /// <summary>
    /// The score of the best-ranked value settled under the known keys and whether it is <paramref name="value"/> — what
    /// <see cref="FieldDefinition.KeyThreshold"/> is compared with; null when no known key was seen.
    /// </summary>
    internal (double Score, bool Matches)? First(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> known, string value, IReadOnlyDictionary<string, IReadOnlyList<string>>? knownSets = null)
    {
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return null;
        }

        var ranked = Keyed(index, form, form.Field(field), known, knownSets ?? NoSets, excluding: null);
        return ranked.Count > 0 ? (ranked[0].Score, ranked[0].Value == value) : null;
    }

    /// <summary>
    /// As <see cref="First"/>, with none, one, … up to <paramref name="longest"/> characters of <paramref name="value"/>
    /// typed into the field: for each length, the best-ranked value that begins with that much of it — what
    /// <see cref="FieldDefinition.KeyThresholdFor"/> that many characters is compared with — or null when no value under
    /// the known keys does.
    /// </summary>
    internal (double Score, string Value)?[] Typed(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> known, string value, int longest, IReadOnlyDictionary<string, IReadOnlyList<string>>? knownSets = null)
    {
        var best = new (double Score, string Value)?[Math.Min(longest, value.Length) + 1];
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return best;
        }

        var ranked = Keyed(index, form, form.Field(field), known, knownSets ?? NoSets, excluding: null);
        for (var length = 0; length < best.Length; length++)
        {
            var typed = length == 0 ? null : value[..length];
            var found = ranked.FindIndex(s => Begins(s.Value, typed));
            best[length] = found < 0 ? null : (ranked[found].Score, ranked[found].Value);
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="value"/>, the best value that begins with the typed text, was already trusted with fewer of
    /// its characters typed — the best value there too, with a score that reached the threshold for that many. Typed text
    /// only narrows the values, and a value's score does not change with it, so a value trusted with fewer characters
    /// typed stays the best for as long as the text still leads to it: it is the same claim, not a new one to judge by the
    /// threshold for more characters, and withdrawing it would make a suggestion help less the more a person types.
    /// </summary>
    private static bool Claimed(List<(string Value, double Score, Key Key)> ranked, FieldDefinition field, string typed, string value)
    {
        for (var length = 0; length < typed.Length; length++)
        {
            var prefix = length == 0 ? null : typed[..length];
            var best = ranked.FindIndex(s => Begins(s.Value, prefix));
            if (best >= 0 && ranked[best].Value == value && ranked[best].Score >= field.KeyThresholdFor(length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="value"/> begins with the text typed so far (ignoring case); always when none is. Korean is
    /// typed a letter at a time and the last syllable may still be composing — "ㅂ", then "바", then "박" on the way to
    /// "박물관", and "박" may yet become "바가" — so text with Hangul begins a value when its keystrokes begin the
    /// value's (<see cref="Hangul.Keystrokes"/>). Thresholds still count the characters typed: a syllable being
    /// composed is one.
    /// </summary>
    internal static bool Begins(string value, string? typed) =>
        typed is null
        || value.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
        || (Hangul.Has(typed) && Hangul.Keystrokes(value).StartsWith(Hangul.Keystrokes(typed), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every value of a field that takes several settled under the known keys, with the score each is compared with
    /// <see cref="FieldDefinition.KeyThreshold"/> on — the chosen values left out, as <see cref="Rank"/> leaves them out.
    /// </summary>
    internal List<(string Value, double Score)> Scored(
        FormDefinition form, string field, IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, IReadOnlyList<string>> knownSets)
    {
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return [];
        }

        var chosen = new HashSet<string>(Elements(knownSets, field), StringComparer.Ordinal);
        return [.. Keyed(index, form, form.Field(field), known, knownSets, excluding: null).Where(s => !chosen.Contains(s.Value)).Select(s => (s.Value, s.Score))];
    }

    /// <summary>
    /// The values settled under the known keys, best first: by their score — the sum of their strengths under each key —
    /// then the latest settlement, then ordinally. Each carries the key it is strongest under. Values outside the field's
    /// domain — or not beginning with the text typed so far — are left out, so the best value in it is the one a threshold
    /// is compared with; they still count towards their keys' totals, which they were settled under.
    /// </summary>
    private List<(string Value, double Score, Key Key)> Keyed(
        FormIndex index,
        FormDefinition form,
        FieldDefinition field,
        IReadOnlyDictionary<string, string> known,
        IReadOnlyDictionary<string, IReadOnlyList<string>> knownSets,
        string? excluding,
        string? typed = null)
    {
        var scores = new Dictionary<string, (double Score, double Strongest, Key Key, DateTimeOffset Latest)>(StringComparer.Ordinal);
        var keys = Keys(form, field, known, knownSets);
        if (field.Multiple)
        {
            keys = keys.Concat(OwnKeys(field, Elements(knownSets, field.Name))).Distinct();
        }

        foreach (var key in keys)
        {
            if (!index.Values.TryGetValue(new Slot(field.Name, key), out var counts))
            {
                continue;
            }

            foreach (var (value, _, strength, latest) in counts.Shares(excluding).Where(s => field.Admits(s.Value, known, knownSets) && Begins(s.Value, typed)))
            {
                var (score, strongest, backing, last) = scores.GetValueOrDefault(value, (0, 0, default, DateTimeOffset.MinValue));
                (backing, strongest) = strength > strongest ? (key, strength) : (backing, strongest);
                scores[value] = (score + strength, strongest, backing, latest > last ? latest : last);
            }
        }

        return [.. scores
            .OrderByDescending(s => s.Value.Score)
            .ThenByDescending(s => s.Value.Latest)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => (s.Key, s.Value.Score, s.Value.Key))];
    }

    /// <summary>
    /// The keys the document's values give <paramref name="field"/>: every other field that supports it, with a short enough
    /// value — each value of a field that takes several being a key of its own.
    /// </summary>
    private IEnumerable<Key> Keys(
        FormDefinition form, FieldDefinition field, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, IReadOnlyList<string>> sets)
    {
        var keys = new List<Key>();
        foreach (var evidence in form.Fields.Where(e => form.Supports(e.Name, field.Name)))
        {
            if (evidence.Multiple)
            {
                keys.AddRange(OwnKeys(evidence, Elements(sets, evidence.Name)));
            }
            else if (values.TryGetValue(evidence.Name, out var raw) && KeyOf(evidence.Name, raw) is { } key)
            {
                keys.Add(key);
            }
        }

        return keys.Distinct();
    }

    /// <summary>The keys values of <paramref name="field"/> make — the field's own name with each short enough value.</summary>
    private IEnumerable<Key> OwnKeys(FieldDefinition field, IEnumerable<string> values) =>
        values.Select(v => KeyOf(field.Name, v)).OfType<Key>().Distinct();

    private Key? KeyOf(string field, string raw)
    {
        var normal = TextNormal.Collapse(raw);
        return normal.Length > 0 && normal.Length <= _maxKeyLength ? new Key(field, normal) : null;
    }

    /// <summary>A set field's values, once each and ordinally — the order a set is listed in carries no meaning.</summary>
    private static List<string> Elements(IReadOnlyDictionary<string, IReadOnlyList<string>> sets, string field) =>
        sets.TryGetValue(field, out var values) ? [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : [];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoSets =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    private FormIndex Index(string form)
    {
        if (!_forms.TryGetValue(form, out var index))
        {
            _forms[form] = index = new FormIndex(_recencyDecay);
        }

        return index;
    }

    /// <summary>Another field's normalised value.</summary>
    private readonly record struct Key(string Field, string Value);

    /// <summary>A field's counts under one key, or overall when the key is null.</summary>
    private readonly record struct Slot(string Field, Key? Key);

    /// <summary>A value a document settled, and when; the time and then the document id order settlements.</summary>
    private sealed record Settled(string Value, DateTimeOffset At, string DocumentId);

    /// <summary>One settlement a document made for a field, under a key or overall.</summary>
    private readonly record struct Tally(string Field, Key? Key, Settled Settlement);

    private sealed class FormIndex(double recencyDecay)
    {
        private readonly Dictionary<string, List<Tally>> _documents = new(StringComparer.Ordinal);

        public Dictionary<Slot, Counts> Values { get; } = [];

        public int Documents => _documents.Count;

        public void Add(string documentId, List<Tally> tallies)
        {
            _documents[documentId] = tallies;
            foreach (var tally in tallies)
            {
                var slot = new Slot(tally.Field, tally.Key);
                if (!Values.TryGetValue(slot, out var counts))
                {
                    Values[slot] = counts = new Counts(recencyDecay);
                }

                counts.Add(tally.Settlement);
            }
        }

        public void Remove(string documentId)
        {
            if (!_documents.Remove(documentId, out var tallies))
            {
                return;
            }

            foreach (var tally in tallies)
            {
                var slot = new Slot(tally.Field, tally.Key);
                var counts = Values[slot];
                counts.Subtract(tally.Settlement);
                if (counts.Total == 0)
                {
                    Values.Remove(slot);
                }
            }
        }
    }

    /// <summary>
    /// The settlements under one key (or overall), weighed by how many came after each. The weights are one fold over the
    /// settlements from the earliest: each settlement leaves every earlier weight one step lighter and adds its own. A
    /// settlement later than every one before it — what a stream of new documents brings — continues the fold, so a
    /// lookup costs as much as the key has values however long its history; one that arrives out of order, or one taken
    /// away, has the fold run again from the earliest. Either way the same settlements reach the same weights to the bit.
    /// A document settles at most once under a key (<see cref="FieldMemory.Put"/> replaces what it contributed) — with one
    /// value, or with several of a field that takes several, which weigh alike as one settlement.
    /// </summary>
    private sealed class Counts(double recencyDecay)
    {
        private readonly List<Settled> _settled = [];
        private readonly Dictionary<string, int> _documents = new(StringComparer.Ordinal); // settlements per document
        private Fold _fold = new(recencyDecay);
        private bool _stale;
        private (string Value, double Share, double Strength, DateTimeOffset Latest)[]? _shares;

        public int Total => _settled.Count;

        public void Add(Settled settled)
        {
            _settled.Add(settled);
            _documents[settled.DocumentId] = _documents.GetValueOrDefault(settled.DocumentId) + 1;
            _shares = null;
            if (_stale || (_fold.Newest is { } newest && !Later(settled, newest) && !Together(settled, newest)))
            {
                _stale = true; // out of order: its weight depends on how many came after it
                return;
            }

            _fold.Add(settled);
        }

        public void Subtract(Settled settled)
        {
            _settled.Remove(settled);
            if (--_documents[settled.DocumentId] == 0)
            {
                _documents.Remove(settled.DocumentId);
            }

            _shares = null;
            _stale = true;
        }

        /// <summary>
        /// Each value's share of the weighed total, its strength (its weight over the total plus one) and when it was last
        /// settled, largest share first; ties to the one settled most recently, then to the ordinally smaller value.
        /// Without caching when a document is left out.
        /// </summary>
        public (string Value, double Share, double Strength, DateTimeOffset Latest)[] Shares(string? excluding = null)
        {
            if (excluding is not null && _documents.ContainsKey(excluding))
            {
                return Weigh(_settled.Where(s => s.DocumentId != excluding)).Shares();
            }

            if (_stale)
            {
                _fold = Weigh(_settled);
                _stale = false;
            }

            return _shares ??= _fold.Shares();
        }

        /// <summary>Whether <paramref name="a"/> comes after <paramref name="b"/> in the order settlements are weighed in.</summary>
        private static bool Later(Settled a, Settled b) =>
            a.At > b.At || (a.At == b.At && string.CompareOrdinal(a.DocumentId, b.DocumentId) > 0);

        /// <summary>Whether two settlements are one document's values settled together — they weigh alike.</summary>
        internal static bool Together(Settled a, Settled b) => a.At == b.At && a.DocumentId == b.DocumentId;

        private Fold Weigh(IEnumerable<Settled> settlements)
        {
            var fold = new Fold(recencyDecay);
            foreach (var settled in settlements.OrderBy(s => s.At).ThenBy(s => s.DocumentId, StringComparer.Ordinal))
            {
                fold.Add(settled);
            }

            return fold;
        }
    }

    /// <summary>
    /// The weights of settlements added from the earliest. The latest settlement weighs exactly 1 and is held apart — all of
    /// it, when a document settled several values together; every earlier one is a raw amount times a common scale, so
    /// making them all one step lighter is one multiplication of the scale. The raw amounts are folded back into the scale
    /// before it gets too small to hold.
    /// </summary>
    private sealed class Fold(double recencyDecay)
    {
        private readonly Dictionary<string, (double Raw, DateTimeOffset Latest)> _raw = new(StringComparer.Ordinal);
        private readonly List<Settled> _newest = []; // the latest settlement: one document's values, settled together
        private double _rawTotal;
        private double _scale = 1;

        public Settled? Newest => _newest.Count > 0 ? _newest[^1] : null;

        public void Add(Settled settled)
        {
            if (Newest is { } last && Counts.Together(settled, last))
            {
                _newest.Add(settled); // the same settlement: no step lighter between its values
                return;
            }

            if (_newest.Count > 0)
            {
                var amount = 1 / _scale; // the previous latest joins the earlier ones at weight 1, then all grow lighter
                foreach (var previous in _newest)
                {
                    _raw[previous.Value] = (_raw.GetValueOrDefault(previous.Value).Raw + amount, previous.At);
                    _rawTotal += amount;
                }

                _newest.Clear();
                _scale *= recencyDecay;
                if (_scale < 1e-150)
                {
                    foreach (var value in _raw.Keys.ToList())
                    {
                        var (raw, latest) = _raw[value];
                        _raw[value] = (raw * _scale, latest);
                    }

                    (_rawTotal, _scale) = (_rawTotal * _scale, 1);
                }
            }

            _newest.Add(settled);
        }

        public (string Value, double Share, double Strength, DateTimeOffset Latest)[] Shares()
        {
            if (_newest.Count == 0)
            {
                return [];
            }

            var weights = _raw.ToDictionary(w => w.Key, w => (Weight: w.Value.Raw * _scale, w.Value.Latest), StringComparer.Ordinal);
            foreach (var newest in _newest)
            {
                var (own, _) = weights.GetValueOrDefault(newest.Value);
                weights[newest.Value] = (own + 1, newest.At);
            }

            var total = (_rawTotal * _scale) + _newest.Count;
            return [.. weights
                .Select(w => (Value: w.Key, Share: w.Value.Weight / total, Strength: w.Value.Weight / (total + 1), w.Value.Latest))
                .OrderByDescending(w => w.Share)
                .ThenByDescending(w => w.Latest)
                .ThenBy(w => w.Value, StringComparer.Ordinal)];
        }
    }
}
