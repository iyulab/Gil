using System.Text;

namespace Gil.Memory;

/// <summary>
/// Memory that needs no model: cosine nearest neighbour over character n-gram TF-IDF, one index per task. Text is
/// compared by its characters, so it works without a tokenizer in any script, and a lookup costs no call (its energy is
/// 0). Like <see cref="EmbeddingMemory"/> it holds only confirmed answers and is rebuilt from the feedback history —
/// through <see cref="MemoryReplay"/>.
/// </summary>
/// <remarks>
/// Similarities are on a different scale from embedding cosines — text that means the same but shares few characters
/// scores low — so a task's <see cref="TaskPolicy.MemoryThreshold"/> has to be chosen for this memory. Weights follow the
/// remembered answers: document frequencies change as rows are added or forgotten, and every lookup uses the current
/// ones. Not thread-safe; one instance serves one caller at a time.
/// </remarks>
public sealed class LexicalMemory : IMemory
{
    private readonly int _minGram;
    private readonly int _maxGram;
    private readonly Dictionary<string, Index> _indexes = [];

    /// <param name="minGram">The shortest character n-gram.</param>
    /// <param name="maxGram">The longest character n-gram.</param>
    public LexicalMemory(int minGram = 2, int maxGram = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minGram, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxGram, minGram);
        (_minGram, _maxGram) = (minGram, maxGram);
    }

    /// <summary>How many confirmed answers the task's memory holds.</summary>
    public int Count(string task) => _indexes.TryGetValue(task, out var index) ? index.Count : 0;

    public Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_indexes.TryGetValue(task, out var index) || index.Count == 0)
        {
            return Task.FromResult<(MemoryMatch?, double)>((null, 0));
        }

        var (key, similarity, answer) = index.Nearest(Grams(state));
        return Task.FromResult<(MemoryMatch?, double)>((new MemoryMatch(key, similarity, answer), 0));
    }

    public Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_indexes.TryGetValue(task, out var index))
        {
            _indexes[task] = index = new Index();
        }

        index.Put(key, Grams(state), answer);
        return Task.FromResult(0.0);
    }

    public void Forget(string task, string key)
    {
        if (_indexes.TryGetValue(task, out var index))
        {
            index.Remove(key);
        }
    }

    /// <summary>
    /// Counts of the character n-grams of the text after NFKC normalisation, lower-casing and collapsing whitespace, with
    /// a space at either end so that a word's first and last characters form n-grams of their own. Text shorter than
    /// the shortest n-gram is one n-gram.
    /// </summary>
    private Dictionary<string, int> Grams(string text)
    {
        var normal = new StringBuilder(" ");
        var space = true;
        foreach (var c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space)
                {
                    normal.Append(' ');
                }

                space = true;
                continue;
            }

            normal.Append(c);
            space = false;
        }

        if (!space)
        {
            normal.Append(' ');
        }

        var padded = normal.ToString();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (padded.Trim().Length == 0)
        {
            return counts;
        }

        if (padded.Length < _minGram)
        {
            counts[padded] = 1;
            return counts;
        }

        for (var n = _minGram; n <= _maxGram && n <= padded.Length; n++)
        {
            for (var i = 0; i + n <= padded.Length; i++)
            {
                var gram = padded.Substring(i, n);
                counts[gram] = counts.GetValueOrDefault(gram) + 1;
            }
        }

        return counts;
    }

    /// <summary>Rows kept in the order they were remembered; ties go to the earlier row.</summary>
    private sealed class Index
    {
        private readonly List<string> _keys = [];
        private readonly List<string> _answers = [];
        private readonly List<Dictionary<string, int>> _grams = [];
        private readonly Dictionary<string, int> _position = [];
        private readonly Dictionary<string, int> _frequency = new(StringComparer.Ordinal);

        // Row norms under the current document frequencies; cleared whenever a row is added or removed.
        private double[]? _norms;

        public int Count => _keys.Count;

        public void Put(string key, Dictionary<string, int> grams, string answer)
        {
            if (_position.TryGetValue(key, out var at))
            {
                Tally(_grams[at], -1);
                _grams[at] = grams;
                _answers[at] = answer;
            }
            else
            {
                _position[key] = _keys.Count;
                _keys.Add(key);
                _answers.Add(answer);
                _grams.Add(grams);
            }

            Tally(grams, +1);
            _norms = null;
        }

        public void Remove(string key)
        {
            if (!_position.Remove(key, out var at))
            {
                return;
            }

            Tally(_grams[at], -1);
            _keys.RemoveAt(at);
            _answers.RemoveAt(at);
            _grams.RemoveAt(at);
            for (var i = at; i < _keys.Count; i++)
            {
                _position[_keys[i]] = i;
            }

            _norms = null;
        }

        /// <summary>The most similar row; on a tie, the one remembered first.</summary>
        public (string Key, double Similarity, string Answer) Nearest(Dictionary<string, int> query)
        {
            var norms = _norms ??= [.. _grams.Select(Norm)];
            var weights = query.ToDictionary(g => g.Key, g => g.Value * Idf(g.Key), StringComparer.Ordinal);
            var queryNorm = Math.Sqrt(weights.Values.Sum(w => w * w));
            var best = 0;
            var bestSimilarity = double.NegativeInfinity;
            for (var i = 0; i < _grams.Count; i++)
            {
                var similarity = 0.0;
                if (queryNorm > 0 && norms[i] > 0)
                {
                    var row = _grams[i];
                    foreach (var (gram, weight) in weights)
                    {
                        if (row.TryGetValue(gram, out var count))
                        {
                            similarity += weight * count * Idf(gram);
                        }
                    }

                    similarity /= queryNorm * norms[i];
                }

                if (similarity > bestSimilarity)
                {
                    (best, bestSimilarity) = (i, similarity);
                }
            }

            return (_keys[best], bestSimilarity, _answers[best]);
        }

        /// <summary>Smoothed inverse document frequency: never zero, highest for an n-gram no row has.</summary>
        private double Idf(string gram) => Math.Log((1.0 + _keys.Count) / (1.0 + _frequency.GetValueOrDefault(gram))) + 1;

        private double Norm(Dictionary<string, int> grams) => Math.Sqrt(grams.Sum(g => Math.Pow(g.Value * Idf(g.Key), 2)));

        private void Tally(Dictionary<string, int> grams, int delta)
        {
            foreach (var gram in grams.Keys)
            {
                var count = _frequency.GetValueOrDefault(gram) + delta;
                if (count == 0)
                {
                    _frequency.Remove(gram);
                }
                else
                {
                    _frequency[gram] = count;
                }
            }
        }
    }
}
