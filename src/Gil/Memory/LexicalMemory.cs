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
/// remembered answers: the inverse document frequencies are taken afresh whenever the number of remembered answers has
/// moved by a tenth (or by one, while there are fewer than ten) since they were last taken, and every row and every
/// lookup is weighted with the same ones in between — so a similarity stays a cosine, 1 for the same text, and changes in
/// steps rather than on every answer remembered. Not thread-safe; one instance serves one caller at a time.
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

    /// <summary>
    /// The <paramref name="count"/> most similar remembered requests, most similar first; on a tie, the one remembered
    /// first. The first is the match <see cref="LookupAsync"/> returns. Empty when the task's memory is empty.
    /// </summary>
    /// <param name="task">The task whose memory to search.</param>
    /// <param name="state">The request.</param>
    /// <param name="count">At most this many; fewer when memory holds fewer.</param>
    public IReadOnlyList<MemoryMatch> Nearest(string task, string state, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (!_indexes.TryGetValue(task, out var index) || index.Count == 0)
        {
            return [];
        }

        return [.. index.Top(Grams(state), count).Select(m => new MemoryMatch(m.Key, m.Similarity, m.Answer))];
    }

    public Task<(IReadOnlyList<MemoryMatch> Matches, double Energy)> NearestAsync(string task, string state, int count, string traceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<(IReadOnlyList<MemoryMatch>, double)>((Nearest(task, state, count), 0));
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
        var normal = TextNormal.Collapse(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (normal.Length == 0)
        {
            return counts;
        }

        var padded = $" {normal} ";
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

    /// <summary>
    /// Rows in no particular order, each numbered in the order it was remembered — a key remembered again keeps its number —
    /// and ties go to the smaller number, so forgetting a row moves the last one into its place instead of shifting the
    /// rest. Each distinct n-gram gets an id once, and a row holds its ids sorted with their counts and weights.
    /// </summary>
    /// <remarks>
    /// A lookup touches only the n-grams a row shares with the request: every id lists, side by side, the slots of the rows
    /// that hold it and their weights for it, and the request's ids are walked in ascending order, so each row's products
    /// are added in the order a walk along both sorted lists would add them — the sums are the same to the bit. A slot is
    /// given to a row once and never moves, so the lists are read front to back and written into one array of sums. A row
    /// forgotten or replaced leaves its slot behind, summed but never read, until such entries outnumber the live ones and
    /// the lists are rebuilt; they are rebuilt too whenever the weights are taken afresh.
    /// </remarks>
    private sealed class Index
    {
        private readonly List<Row> _rows = [];
        private long _remembered;
        private readonly Dictionary<string, int> _position = [];
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<int> _frequency = [];
        private readonly List<Postings> _postings = [];
        private int _slots;
        private long _live;
        private long _gone;

        // The inverse document frequency of every id known when there were _takenAt rows; later ids had none.
        private double[] _idf = [];
        private int _takenAt;

        public int Count => _rows.Count;

        public void Put(string key, Dictionary<string, int> grams, string answer)
        {
            var (ids, counts) = Intern(grams);
            var existing = _position.TryGetValue(key, out var at);
            var row = new Row(key, answer, existing ? _rows[at].Order : _remembered++, ids, counts, new double[ids.Length]);
            row.Norm = Weigh(ids, counts, row.Weights);
            if (existing)
            {
                Tally(_rows[at].Ids, -1);
                Retire(_rows[at]);
                _rows[at] = row;
            }
            else
            {
                _position[key] = _rows.Count;
                _rows.Add(row);
            }

            Tally(ids, +1);
            if (!Refresh())
            {
                Post(row);
                Compact();
            }
        }

        public void Remove(string key)
        {
            if (!_position.Remove(key, out var at))
            {
                return;
            }

            Tally(_rows[at].Ids, -1);
            Retire(_rows[at]);
            var last = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            if (at < _rows.Count)
            {
                _rows[at] = last;
                _position[last.Key] = at;
            }

            if (!Refresh())
            {
                Compact();
            }
        }

        /// <summary>The most similar row; on a tie, the one remembered first.</summary>
        public (string Key, double Similarity, string Answer) Nearest(Dictionary<string, int> query)
        {
            var similarities = Similarities(query);
            var best = 0;
            for (var r = 1; r < similarities.Length; r++)
            {
                if (similarities[r] > similarities[best] || (similarities[r] == similarities[best] && _rows[r].Order < _rows[best].Order))
                {
                    best = r;
                }
            }

            return (_rows[best].Key, similarities[best], _rows[best].Answer);
        }

        /// <summary>The <paramref name="count"/> most similar rows, most similar first; ties in remembered order.</summary>
        public IEnumerable<(string Key, double Similarity, string Answer)> Top(Dictionary<string, int> query, int count)
        {
            var similarities = Similarities(query);
            return Enumerable.Range(0, similarities.Length)
                .OrderByDescending(r => similarities[r])
                .ThenBy(r => _rows[r].Order)
                .Take(count)
                .Select(r => (_rows[r].Key, similarities[r], _rows[r].Answer));
        }

        /// <summary>The cosine of the query with every row, in the rows' order.</summary>
        private double[] Similarities(Dictionary<string, int> query)
        {
            // An n-gram no row has ever held matches nothing, but it still weighs in the query's norm.
            var known = new List<(int Id, int Count)>(query.Count);
            var squares = 0.0;
            foreach (var (gram, count) in query)
            {
                if (_ids.TryGetValue(gram, out var id))
                {
                    known.Add((id, count));
                }
                else
                {
                    squares += Math.Pow(count * Smoothed(0), 2);
                }
            }

            known.Sort();
            var ids = known.Select(k => k.Id).ToArray();
            var weights = new double[ids.Length];
            var queryNorm = Math.Sqrt(squares + Math.Pow(Weigh(ids, [.. known.Select(k => k.Count)], weights), 2));

            var similarities = new double[_rows.Count];
            if (queryNorm == 0)
            {
                return similarities;
            }

            var sums = new double[_slots];
            for (var i = 0; i < ids.Length; i++)
            {
                var weight = weights[i];
                var postings = _postings[ids[i]];
                var slots = postings.Slots.AsSpan(0, postings.Count);
                var rowWeights = postings.Weights.AsSpan(0, postings.Count);
                for (var k = 0; k < slots.Length; k++)
                {
                    sums[slots[k]] += weight * rowWeights[k];
                }
            }

            for (var r = 0; r < _rows.Count; r++)
            {
                var row = _rows[r];
                similarities[r] = row.Norm > 0 ? sums[row.Slot] / (queryNorm * row.Norm) : 0;
            }

            return similarities;
        }

        private (int[] Ids, int[] Counts) Intern(Dictionary<string, int> grams)
        {
            var pairs = new (int Id, int Count)[grams.Count];
            var n = 0;
            foreach (var (gram, count) in grams)
            {
                if (!_ids.TryGetValue(gram, out var id))
                {
                    _ids[gram] = id = _ids.Count;
                    _frequency.Add(0);
                    _postings.Add(new Postings());
                }

                pairs[n++] = (id, count);
            }

            Array.Sort(pairs);
            return ([.. pairs.Select(p => p.Id)], [.. pairs.Select(p => p.Count)]);
        }

        /// <summary>
        /// Takes the frequencies afresh once the row count has moved by a tenth (at least one) since last time, reweighs
        /// every row and rebuilds the postings; false when it is not yet time.
        /// </summary>
        private bool Refresh()
        {
            if (Math.Abs(_rows.Count - _takenAt) < Math.Max(1, _takenAt / 10))
            {
                return false;
            }

            _takenAt = _rows.Count;
            _idf = [.. _frequency.Select(Smoothed)];
            foreach (var row in _rows)
            {
                row.Norm = Weigh(row.Ids, row.Counts, row.Weights);
            }

            Repost();
            return true;
        }

        /// <summary>Lists the row under each of its ids, in a slot of its own.</summary>
        private void Post(Row row)
        {
            row.Slot = _slots++;
            for (var j = 0; j < row.Ids.Length; j++)
            {
                _postings[row.Ids[j]].Add(row.Slot, row.Weights[j]);
            }

            _live += row.Ids.Length;
        }

        private void Retire(Row row)
        {
            _live -= row.Ids.Length;
            _gone += row.Ids.Length;
        }

        /// <summary>Rebuilds the postings once the entries of rows gone outnumber the live ones.</summary>
        private void Compact()
        {
            if (_gone > _live)
            {
                Repost();
            }
        }

        /// <summary>Lists the live rows afresh, in slots numbered from zero, with their current weights.</summary>
        private void Repost()
        {
            foreach (var postings in _postings)
            {
                postings.Count = 0;
            }

            (_slots, _live, _gone) = (0, 0, 0);
            foreach (var row in _rows)
            {
                Post(row);
            }
        }

        /// <summary>Fills <paramref name="weights"/> with count × idf and returns their norm.</summary>
        private double Weigh(int[] ids, int[] counts, double[] weights)
        {
            var squares = 0.0;
            for (var i = 0; i < ids.Length; i++)
            {
                weights[i] = counts[i] * (ids[i] < _idf.Length ? _idf[ids[i]] : Smoothed(0));
                squares += weights[i] * weights[i];
            }

            return Math.Sqrt(squares);
        }

        /// <summary>Smoothed inverse document frequency at the last refresh: never zero, highest for an n-gram no row had.</summary>
        private double Smoothed(int frequency) => Math.Log((1.0 + _takenAt) / (1.0 + frequency)) + 1;

        private void Tally(int[] ids, int delta)
        {
            foreach (var id in ids)
            {
                _frequency[id] += delta;
            }
        }

        private sealed class Row(string key, string answer, long order, int[] ids, int[] counts, double[] weights)
        {
            public string Key { get; } = key;

            public string Answer { get; } = answer;

            /// <summary>When the key was first remembered, among the rows of this index; breaks ties.</summary>
            public long Order { get; } = order;

            public int[] Ids { get; } = ids;

            public int[] Counts { get; } = counts;

            public double[] Weights { get; } = weights;

            public double Norm { get; set; }

            /// <summary>Where the row's sum is kept during a lookup.</summary>
            public int Slot { get; set; }
        }

        /// <summary>The slots of the rows holding one n-gram and their weights for it, side by side.</summary>
        private sealed class Postings
        {
            public int[] Slots { get; private set; } = [];

            public double[] Weights { get; private set; } = [];

            public int Count { get; set; }

            public void Add(int slot, double weight)
            {
                if (Count == Slots.Length)
                {
                    var capacity = Math.Max(4, Count * 2);
                    (Slots, Weights) = (Grow(Slots, capacity), Grow(Weights, capacity));
                }

                (Slots[Count], Weights[Count]) = (slot, weight);
                Count++;
            }

            private static T[] Grow<T>(T[] items, int capacity)
            {
                var grown = new T[capacity];
                items.CopyTo(grown, 0);
                return grown;
            }
        }
    }
}
