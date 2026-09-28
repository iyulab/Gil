using Gil.Memory;

namespace Gil.Forms;

/// <summary>
/// Suggests a judged field's value from how often each value was settled alongside the values the document's other fields
/// have — no model, no call, energy 0. Each other field's value is a key; a value's score is the sum over the document's
/// keys of its share of the settlements made under that key. When no key has been seen, the field's most frequently
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

    /// <summary>Replaces what the document contributed with what its current values imply. Values of fields the form does not have are ignored.</summary>
    public void Put(FormDefinition form, SettledDocument document)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(document);
        var index = Index(form.Name);
        index.Remove(document.DocumentId);

        var tallies = new List<Tally>();
        foreach (var field in form.Fields)
        {
            if (field.Role != FieldRole.Judged || !document.Values.TryGetValue(field.Name, out var value))
            {
                continue;
            }

            var settlement = new Settled(value, document.SettledAt, document.DocumentId);
            tallies.Add(new Tally(field.Name, null, settlement));
            tallies.AddRange(Keys(form, field, document.Values).Select(key => new Tally(field.Name, key, settlement)));
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
    /// first: the values settled under the known keys (with the key that backs each most as its evidence), then, in the
    /// places left, the field's most frequently settled values (without evidence). Ties go to the value settled most
    /// recently, then to the ordinally smaller value, in both parts, so the order documents arrived in never matters.
    /// The keyed values are trusted when the first one's strength reaches the field's
    /// <see cref="FieldDefinition.KeyThreshold"/>; the most frequent values never are. Empty when nothing was settled.
    /// </summary>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="known">The document's values so far; the field's own value, if present, is not evidence for itself.</param>
    /// <param name="count">At most this many.</param>
    /// <param name="excluding">A document whose settlements are left out, so that a saved document is not evidence for itself.</param>
    public IReadOnlyList<FieldCandidate> Rank(FormDefinition form, string field, IReadOnlyDictionary<string, string> known, int count, string? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var definition = form.Field(field);
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return [];
        }

        var ranked = Keyed(index, form, definition, known, excluding);
        var trusted = ranked.Count > 0 && definition.KeyThreshold is double threshold && ranked[0].Strength >= threshold;
        var keyed = ranked
            .Take(count)
            .Select(s => new FieldCandidate(s.Value, s.Score, FieldSource.SettledFieldMemory, $"{s.Key.Field}: {s.Key.Value}", trusted));
        var overall = index.Values.TryGetValue(new Slot(field, null), out var totals)
            ? totals.Shares(excluding).Select(s => new FieldCandidate(s.Value, s.Share, FieldSource.SettledFieldMemory, null, Trusted: false))
            : [];
        return [.. keyed.Concat(overall).DistinctBy(c => c.Value, StringComparer.Ordinal).Take(count)];
    }

    /// <summary>
    /// The strength of the best-ranked value settled under the known keys and whether it is <paramref name="value"/> — what
    /// <see cref="FieldDefinition.KeyThreshold"/> is compared with; null when no known key was seen.
    /// </summary>
    internal (double Strength, bool Matches)? First(FormDefinition form, string field, IReadOnlyDictionary<string, string> known, string value)
    {
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return null;
        }

        var ranked = Keyed(index, form, form.Field(field), known, excluding: null);
        return ranked.Count > 0 ? (ranked[0].Strength, ranked[0].Value == value) : null;
    }

    /// <summary>
    /// The values settled under the known keys, best first: by the sum of their shares under each key, then the latest
    /// settlement, then ordinally. Each carries the key it has its largest share under and its greatest strength.
    /// </summary>
    private List<(string Value, double Score, double Strength, Key Key)> Keyed(
        FormIndex index, FormDefinition form, FieldDefinition field, IReadOnlyDictionary<string, string> known, string? excluding)
    {
        var scores = new Dictionary<string, (double Score, double Best, Key Key, double Strength, DateTimeOffset Latest)>(StringComparer.Ordinal);
        foreach (var key in Keys(form, field, known))
        {
            if (!index.Values.TryGetValue(new Slot(field.Name, key), out var counts))
            {
                continue;
            }

            foreach (var (value, share, strength, latest) in counts.Shares(excluding))
            {
                var (score, best, strongest, most, last) = scores.GetValueOrDefault(value, (0, 0, default, 0, DateTimeOffset.MinValue));
                (last, most) = (latest > last ? latest : last, Math.Max(most, strength));
                scores[value] = share > best ? (score + share, share, key, most, last) : (score + share, best, strongest, most, last);
            }
        }

        return [.. scores
            .OrderByDescending(s => s.Value.Score)
            .ThenByDescending(s => s.Value.Latest)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => (s.Key, s.Value.Score, s.Value.Strength, s.Value.Key))];
    }

    /// <summary>The keys the document's values give <paramref name="field"/>: every other field that supports it, with a short enough value.</summary>
    private IEnumerable<Key> Keys(FormDefinition form, FieldDefinition field, IReadOnlyDictionary<string, string> values)
    {
        foreach (var evidence in form.Fields)
        {
            if (!form.Supports(evidence.Name, field.Name) || !values.TryGetValue(evidence.Name, out var raw))
            {
                continue;
            }

            var normal = TextNormal.Collapse(raw);
            if (normal.Length > 0 && normal.Length <= _maxKeyLength)
            {
                yield return new Key(evidence.Name, normal);
            }
        }
    }

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

    /// <summary>The settlements under one key (or overall), weighed by how many came after each.</summary>
    private sealed class Counts(double recencyDecay)
    {
        private readonly List<Settled> _settled = [];
        private (string Value, double Share, double Strength, DateTimeOffset Latest)[]? _shares;

        public int Total => _settled.Count;

        public void Add(Settled settled)
        {
            _settled.Add(settled);
            _shares = null;
        }

        public void Subtract(Settled settled)
        {
            _settled.Remove(settled);
            _shares = null;
        }

        /// <summary>
        /// Each value's share of the weighed total, its strength (its weight over the total plus one) and when it was last
        /// settled, largest share first; ties to the one settled most recently, then to the ordinally smaller value.
        /// Weighed afresh after a change, since a settlement that arrives late may belong anywhere in the order, and
        /// without caching when a document is left out.
        /// </summary>
        public (string Value, double Share, double Strength, DateTimeOffset Latest)[] Shares(string? excluding = null) =>
            excluding is not null && _settled.Exists(s => s.DocumentId == excluding)
                ? Weigh(_settled.Where(s => s.DocumentId != excluding))
                : _shares ??= Weigh(_settled);

        private (string Value, double Share, double Strength, DateTimeOffset Latest)[] Weigh(IEnumerable<Settled> settlements)
        {
            var weights = new Dictionary<string, (double Weight, DateTimeOffset Latest)>(StringComparer.Ordinal);
            var (weight, total) = (1.0, 0.0);
            foreach (var settled in settlements.OrderByDescending(s => s.At).ThenByDescending(s => s.DocumentId, StringComparer.Ordinal))
            {
                var (sum, latest) = weights.GetValueOrDefault(settled.Value, (0, settled.At));
                weights[settled.Value] = (sum + weight, latest); // newest first, so the first time seen is the latest
                total += weight;
                weight *= recencyDecay;
            }

            return [.. weights
                .Select(w => (Value: w.Key, Share: w.Value.Weight / total, Strength: w.Value.Weight / (total + 1), w.Value.Latest))
                .OrderByDescending(w => w.Share)
                .ThenByDescending(w => w.Latest)
                .ThenBy(w => w.Value, StringComparer.Ordinal)];
        }
    }
}
