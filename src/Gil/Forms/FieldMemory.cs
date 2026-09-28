using Gil.Memory;

namespace Gil.Forms;

/// <summary>
/// Suggests a judged field's value from how often each value was settled alongside the values the document's other fields
/// have — no model, no call, energy 0. Each other field's value is a key; a value's score is the sum over the document's
/// keys of the share of documents with that key that settled it. When no key has been seen, the field's most frequently
/// settled values stand in.
/// </summary>
/// <remarks>
/// A document contributes as a whole: <see cref="Put"/> replaces whatever it contributed before with what its current
/// values imply, so settling fields one by one and rebuilding from the saved documents reach the same state, in any
/// order and however often either is done. Every other field of the document is a key while learning; only the fields
/// known so far are keys while suggesting. Keys are compared after NFKC, lower-casing and collapsing whitespace, and
/// values longer than <c>maxKeyLength</c> are not keys — free text rarely recurs. Not thread-safe; one instance serves
/// one caller at a time.
/// </remarks>
public sealed class FieldMemory
{
    private readonly int _maxKeyLength;
    private readonly Dictionary<string, FormIndex> _forms = new(StringComparer.Ordinal);

    /// <param name="maxKeyLength">The longest normalised value that is used as a key.</param>
    public FieldMemory(int maxKeyLength = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeyLength, 1);
        _maxKeyLength = maxKeyLength;
    }

    /// <summary>How many documents the form's memory holds.</summary>
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

            tallies.Add(new Tally(field.Name, null, value));
            tallies.AddRange(Keys(form, field, document.Values).Select(key => new Tally(field.Name, key, value)));
        }

        index.Add(document.DocumentId, tallies);
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
    /// first. Falls back to the field's most frequently settled values when none of the known values has been seen as a
    /// key. Ties go to the ordinally smaller value in both cases, so the order documents arrived in never matters. Empty
    /// when nothing was settled.
    /// </summary>
    /// <param name="form">The form.</param>
    /// <param name="field">A judged field of the form.</param>
    /// <param name="known">The document's values so far; the field's own value, if present, is not evidence for itself.</param>
    /// <param name="count">At most this many.</param>
    public IReadOnlyList<FieldCandidate> Rank(FormDefinition form, string field, IReadOnlyDictionary<string, string> known, int count)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var definition = form.Field(field);
        if (!_forms.TryGetValue(form.Name, out var index))
        {
            return [];
        }

        var scores = new Dictionary<string, (double Score, double Best, Key Key)>(StringComparer.Ordinal);
        foreach (var key in Keys(form, definition, known))
        {
            if (!index.Values.TryGetValue(new Slot(field, key), out var counts))
            {
                continue;
            }

            foreach (var (value, share) in counts.Shares())
            {
                var (score, best, strongest) = scores.GetValueOrDefault(value);
                scores[value] = share > best ? (score + share, share, key) : (score + share, best, strongest);
            }
        }

        if (scores.Count > 0)
        {
            return [.. scores
                .OrderByDescending(s => s.Value.Score)
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(count)
                .Select(s => new FieldCandidate(s.Key, s.Value.Score, FieldSource.SettledFieldMemory, $"{s.Value.Key.Field}: {s.Value.Key.Value}"))];
        }

        return index.Values.TryGetValue(new Slot(field, null), out var overall)
            ? [.. overall.Shares().Take(count).Select(s => new FieldCandidate(s.Value, s.Share, FieldSource.SettledFieldMemory, null))]
            : [];
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
            _forms[form] = index = new FormIndex();
        }

        return index;
    }

    /// <summary>Another field's normalised value.</summary>
    private readonly record struct Key(string Field, string Value);

    /// <summary>A field's counts under one key, or overall when the key is null.</summary>
    private readonly record struct Slot(string Field, Key? Key);

    /// <summary>One increment a document made: a value settled for a field, under a key or overall.</summary>
    private readonly record struct Tally(string Field, Key? Key, string Value);

    private sealed class FormIndex
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
                    Values[slot] = counts = new Counts();
                }

                counts.Add(tally.Value);
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
                counts.Subtract(tally.Value);
                if (counts.Total == 0)
                {
                    Values.Remove(slot);
                }
            }
        }
    }

    /// <summary>How often each value was settled.</summary>
    private sealed class Counts
    {
        private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

        public int Total { get; private set; }

        public void Add(string value)
        {
            _values[value] = _values.GetValueOrDefault(value) + 1;
            Total++;
        }

        public void Subtract(string value)
        {
            if (_values[value] == 1)
            {
                _values.Remove(value);
            }
            else
            {
                _values[value]--;
            }

            Total--;
        }

        /// <summary>Each value's share of the total, most frequent first; ties to the ordinally smaller value.</summary>
        public IEnumerable<(string Value, double Share)> Shares() =>
            _values
                .OrderByDescending(v => v.Value)
                .ThenBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => (v.Key, (double)v.Value / Total));
    }
}
