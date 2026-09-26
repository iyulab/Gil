using Gil.Llm;

namespace Gil.Memory;

/// <summary>A request that received feedback, as the log holds it — the input memory is rebuilt from.</summary>
public sealed record FeedbackEntry(string TraceId, string State, string? Mode, string? Output, Recall? Recall, string Verdict, string? Correction);

/// <summary>
/// The default memory: cosine nearest neighbour over input embeddings, one index per task. It is derived from the
/// request log — only answers confirmed by feedback go in, and a remembered answer that proved wrong is forgotten — so it
/// can always be rebuilt from the log.
/// </summary>
public sealed class EmbeddingMemory(EmbeddingRecorder embedder, int pendingLimit = 10_000) : IMemory
{
    private readonly Dictionary<string, Index> _indexes = [];

    // A lookup's vector is kept until the request's feedback arrives, so remembering it costs no second embedding.
    private readonly Dictionary<string, float[]> _pending = [];
    private readonly Queue<string> _pendingOrder = new();

    public int Count(string task) => _indexes.TryGetValue(task, out var index) ? index.Count : 0;

    public async Task<(MemoryMatch? Match, double Energy)> LookupAsync(string task, string state, string traceId, CancellationToken cancellationToken = default)
    {
        var (vectors, call) = await embedder.EmbedAsync([state], traceId, cancellationToken).ConfigureAwait(false);
        var query = Unit(vectors[0]);
        Keep(traceId, query);
        if (!_indexes.TryGetValue(task, out var index) || index.Count == 0)
        {
            return (null, call.Energy);
        }

        var (key, similarity, answer) = index.Nearest(query);
        return (new MemoryMatch(key, similarity, answer), call.Energy);
    }

    public async Task<double> RememberAsync(string task, string traceId, string state, string answer, CancellationToken cancellationToken = default)
    {
        if (_pending.Remove(traceId, out var vector))
        {
            Put(task, traceId, vector, answer);
            return 0;
        }

        var (vectors, call) = await embedder.EmbedAsync([state], traceId, cancellationToken).ConfigureAwait(false);
        Put(task, traceId, Unit(vectors[0]), answer);
        return call.Energy;
    }

    public void Forget(string task, string traceId)
    {
        if (_indexes.TryGetValue(task, out var index))
        {
            index.Remove(traceId);
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
            var (vectors, _) = await embedder.EmbedAsync([.. chunk.Select(e => e.State)], traceId, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                Put(task, chunk[i].Key, Unit(vectors[i]), chunk[i].Answer);
            }
        }

        return replay.Remember.Count;
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
