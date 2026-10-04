namespace Gil.Memory;

/// <summary>
/// A memory that embeds texts ahead of the lookups and writes that will use them, in batches. A form's rebuild knows every
/// text its documents' judged fields will be remembered by, and tells the memory up front.
/// </summary>
internal interface IEmbeddingPrefetch
{
    /// <summary>Embeds the texts not embedded recently; returns the energy it cost.</summary>
    Task<double> PrefetchAsync(IEnumerable<string> states, string traceId, CancellationToken cancellationToken = default);
}
