using Gil.Llm;

namespace Gil.Memory;

/// <summary>A request that received feedback, as the log holds it — the input memory is rebuilt from.</summary>
public sealed record FeedbackEntry(string TraceId, string State, string? Mode, string? Output, Recall? Recall, string Verdict, string? Correction);

/// <summary>
/// The default memory: cosine nearest neighbour over input embeddings, one index per task. It is derived from the
/// request log — only answers confirmed by feedback go in, and a remembered answer that proved wrong is forgotten — so it
/// can always be rebuilt from the log.
/// </summary>
public sealed class EmbeddingMemory(EmbeddingRecorder embedder, int pendingLimit = 10_000) : IMemory, IEmbeddingPrefetch
{
    /// <summary>How many texts one embedding call carries when several are embedded together.</summary>
    internal const int Batch = 64;

    /// <summary>How many recently embedded texts keep their vectors.</summary>
    internal const int RecentLimit = 1024;

    private readonly Dictionary<string, Index> _indexes = [];

    // A lookup's vector is kept until the request's feedback arrives, so remembering it costs no second embedding.
    private readonly Dictionary<string, float[]> _pending = [];
    private readonly Queue<string> _pendingOrder = new();

    // Recently embedded texts and their vectors: several tasks often look up or remember by the same text (a form's
    // judged fields read the same evidence), and a text embeds to the same vector whichever task asks.
    private readonly Dictionary<string, float[]> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();

    public int Count(string task) => _indexes.TryGetValue(task, out var index) ? index.Count : 0;

    public async Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
    {
        var (query, energy) = await EmbedAsync(state, traceId, cancellationToken).ConfigureAwait(false);
        Keep(traceId, query);
        if (!_indexes.TryGetValue(task, out var index) || index.Count == 0)
        {
            return (null, energy);
        }

        var (key, similarity, answer) = index.Nearest(query);
        return (new MemoryMatch(key, similarity, answer), energy);
    }

    public async Task<(IReadOnlyList<MemoryMatch> Matches, double Energy)> NearestAsync(string task, string state, int count, string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var (query, energy) = await EmbedAsync(state, traceId, cancellationToken).ConfigureAwait(false);
        Keep(traceId, query);
        if (!_indexes.TryGetValue(task, out var index) || index.Count == 0)
        {
            return ([], energy);
        }

        return ([.. index.Top(query, count).Select(m => new MemoryMatch(m.Key, m.Similarity, m.Answer))], energy);
    }

    public async Task<double> RememberAsync(string task, string key, string state, string answer, string traceId, CancellationToken cancellationToken = default)
    {
        // The vector its lookup computed, when the request went through this memory.
        if (_pending.Remove(key, out var vector))
        {
            Put(task, key, vector, answer);
            return 0;
        }

        var (unit, energy) = await EmbedAsync(state, traceId, cancellationToken).ConfigureAwait(false);
        Put(task, key, unit, answer);
        return energy;
    }

    public void Forget(string task, string key)
    {
        if (_indexes.TryGetValue(task, out var index))
        {
            index.Remove(key);
        }
    }

    /// <summary>Imports already-embedded confirmed answers (e.g. labelled history) without model calls.</summary>
    public int Seed(string task, IEnumerable<(string Key, float[] Vector, string Answer)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var count = 0;
        foreach (var (key, vector, answer) in items)
        {
            Put(task, key, Unit(vector), answer);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Rebuilds the task's index from its feedback history: confirmed answers only (a correct output, or the correction of
    /// a wrong one), minus remembered answers later overturned. With <paramref name="clear"/> false the history is applied on
    /// top of the current index — seed first, then rebuild, so an overturned seed stays forgotten. The rule is
    /// <see cref="MemoryReplay"/>'s; this applies it in embedding batches.
    /// </summary>
    public async Task<int> RebuildAsync(string task, IReadOnlyList<FeedbackEntry> history, string traceId, bool clear = true, int batch = 64, CancellationToken cancellationToken = default)
    {
        var replay = MemoryReplay.From(history);
        if (clear)
        {
            _indexes.Remove(task);
        }

        foreach (var source in replay.Forget)
        {
            Forget(task, source);
        }

        foreach (var chunk in replay.Remember.Chunk(batch))
        {
            var (vectors, _) = await EmbedAllAsync(chunk.Select(e => e.State), traceId, batch, cancellationToken).ConfigureAwait(false);
            foreach (var entry in chunk)
            {
                Put(task, entry.Key, vectors[entry.State], entry.Answer);
            }
        }

        return replay.Remember.Count;
    }

    /// <summary>
    /// Embeds, in batches, the texts not embedded recently, so the lookups and writes by those texts that follow cost no
    /// further call: a rebuild embeds each distinct text once, however many tasks remember by it.
    /// </summary>
    internal async Task<double> PrefetchAsync(IEnumerable<string> states, string traceId, CancellationToken cancellationToken = default)
    {
        var (_, energy) = await EmbedAllAsync(states, traceId, Batch, cancellationToken).ConfigureAwait(false);
        return energy;
    }

    Task<double> IEmbeddingPrefetch.PrefetchAsync(IEnumerable<string> states, string traceId, CancellationToken cancellationToken) =>
        PrefetchAsync(states, traceId, cancellationToken);

    /// <summary>The text's unit vector — embedded unless it was recently — and the energy that cost.</summary>
    private async Task<(float[] Unit, double Energy)> EmbedAsync(string state, string traceId, CancellationToken cancellationToken)
    {
        if (_recent.TryGetValue(state, out var known))
        {
            return (known, 0);
        }

        var (vectors, call) = await embedder.EmbedAsync([state], traceId, cancellationToken).ConfigureAwait(false);
        var unit = Unit(vectors[0]);
        Recall(state, unit);
        return (unit, call.Energy);
    }

    /// <summary>Unit vectors for the texts: each distinct text not embedded recently is embedded once, in batches.</summary>
    private async Task<(Dictionary<string, float[]> Vectors, double Energy)> EmbedAllAsync(IEnumerable<string> states, string traceId, int batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var state in states)
        {
            if (vectors.ContainsKey(state))
            {
                continue;
            }

            if (_recent.TryGetValue(state, out var known))
            {
                vectors[state] = known;
            }
            else
            {
                vectors[state] = [];
                missing.Add(state);
            }
        }

        var energy = 0.0;
        foreach (var chunk in missing.Chunk(batch))
        {
            var (embedded, call) = await embedder.EmbedAsync(chunk, traceId, cancellationToken).ConfigureAwait(false);
            energy += call.Energy;
            for (var i = 0; i < chunk.Length; i++)
            {
                var unit = Unit(embedded[i]);
                vectors[chunk[i]] = unit;
                Recall(chunk[i], unit);
            }
        }

        return (vectors, energy);
    }

    private void Recall(string state, float[] unit)
    {
        if (_recent.TryAdd(state, unit))
        {
            _recentOrder.Enqueue(state);
        }

        while (_recentOrder.Count > RecentLimit)
        {
            _recent.Remove(_recentOrder.Dequeue());
        }
    }

    private void Keep(string traceId, float[] vector)
    {
        if (_pending.TryAdd(traceId, vector))
        {
            _pendingOrder.Enqueue(traceId);
        }

        while (_pendingOrder.Count > pendingLimit)
        {
            _pending.Remove(_pendingOrder.Dequeue());
        }
    }

    private void Put(string task, string key, float[] vector, string answer)
    {
        if (!_indexes.TryGetValue(task, out var index))
        {
            _indexes[task] = index = new Index();
        }

        index.Put(key, vector, answer);
    }

    private static float[] Unit(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return norm == 0 ? vector : [.. vector.Select(v => v / norm)];
    }

    /// <summary>Rows kept in insertion order; ties go to the earlier row.</summary>
    private sealed class Index
    {
        private readonly List<string> _keys = [];
        private readonly List<string> _answers = [];
        private readonly List<float[]> _vectors = [];
        private readonly Dictionary<string, int> _position = [];

        public int Count => _keys.Count;

        public void Put(string key, float[] vector, string answer)
        {
            if (_position.TryGetValue(key, out var at))
            {
                _vectors[at] = vector;
                _answers[at] = answer;
                return;
            }

            _position[key] = _keys.Count;
            _keys.Add(key);
            _answers.Add(answer);
            _vectors.Add(vector);
        }

        public void Remove(string key)
        {
            if (!_position.Remove(key, out var at))
            {
                return;
            }

            _keys.RemoveAt(at);
            _answers.RemoveAt(at);
            _vectors.RemoveAt(at);
            for (var i = at; i < _keys.Count; i++)
            {
                _position[_keys[i]] = i;
            }
        }

        /// <summary>The <paramref name="count"/> most similar rows, most similar first; on a tie, the one remembered first.</summary>
        public IEnumerable<(string Key, double Similarity, string Answer)> Top(float[] query, int count) =>
            Enumerable.Range(0, _vectors.Count)
                .Select(i => (Row: i, Similarity: Dot(_vectors[i], query)))
                .OrderByDescending(r => r.Similarity)
                .ThenBy(r => r.Row) // stable: the one remembered first
                .Take(count)
                .Select(r => (_keys[r.Row], r.Similarity, _answers[r.Row]));

        private static double Dot(float[] row, float[] query)
        {
            var similarity = 0.0;
            for (var d = 0; d < row.Length; d++)
            {
                similarity += row[d] * query[d];
            }

            return similarity;
        }

        /// <summary>The most similar row; on a tie, the one remembered first (rows stay in the order they were remembered).</summary>
        public (string Key, double Similarity, string Answer) Nearest(float[] query)
        {
            var best = 0;
            var bestSimilarity = double.NegativeInfinity;
            for (var i = 0; i < _vectors.Count; i++)
            {
                var similarity = 0.0;
                var row = _vectors[i];
                for (var d = 0; d < row.Length; d++)
                {
                    similarity += row[d] * query[d];
                }

                if (similarity > bestSimilarity)
                {
                    (best, bestSimilarity) = (i, similarity);
                }
            }

            return (_keys[best], bestSimilarity, _answers[best]);
        }
    }
}
