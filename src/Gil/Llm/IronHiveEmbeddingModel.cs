using System.Diagnostics;
using System.Text.Json.Nodes;
using IronHive.Abstractions.Embedding;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible.Embedding;

namespace Gil.Llm;

/// <summary>
/// Embeddings through an IronHive <see cref="IEmbeddingGenerator"/>, reading the vectors in input order together with
/// the tokens and model the server reports.
/// </summary>
/// <remarks>
/// <see cref="EmbeddingResult.RawResponse"/> keeps the reported model and usage but not the vectors, which live in the
/// memory index.
/// </remarks>
public sealed class IronHiveEmbeddingModel : IEmbeddingModel, IDisposable
{
    private readonly IEmbeddingGenerator _generator;
    private readonly string _model;
    private readonly HttpClient? _ownedHttp;
    private readonly bool _ownsGenerator;
    private readonly EmbeddingRequestOptions? _options;

    /// <param name="generator">The provider to call; the caller keeps ownership.</param>
    /// <param name="model">The embedding model to ask for.</param>
    /// <param name="extraBody">Provider fields sent with every request (for example a server's pooling option), merged
    /// over the fields IronHive sets.</param>
    public IronHiveEmbeddingModel(IEmbeddingGenerator generator, string model, JsonObject? extraBody = null)
        : this(generator, model, extraBody, ownsGenerator: false, ownedHttp: null)
    {
    }

    private IronHiveEmbeddingModel(IEmbeddingGenerator generator, string model, JsonObject? extraBody, bool ownsGenerator, HttpClient? ownedHttp)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrEmpty(model);
        (_generator, _model, _ownsGenerator, _ownedHttp) = (generator, model, ownsGenerator, ownedHttp);
        _options = extraBody is null ? null : new EmbeddingRequestOptions { ExtraBody = (JsonObject)extraBody.DeepClone() };
    }

    /// <summary>
    /// An embedding model on an OpenAI-compatible server (OpenAI, and self-hosted servers such as llama.cpp and vLLM),
    /// sent with the shared retry rules of <see cref="OpenAICompatibleOptions"/>.
    /// </summary>
    /// <param name="options">Endpoint, embedding model, retries and the fields every request carries.</param>
    /// <param name="transport">The handler that sends; a socket handler by default.</param>
    /// <param name="delay">Backoff between retries; the real clock by default.</param>
    public static IronHiveEmbeddingModel OpenAICompatible(
        OpenAICompatibleOptions options, HttpMessageHandler? transport = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // IronHive uses an injected client as given and leaves it to its owner, which is this model.
        var http = OpenAICompatibleHttp.CreateClient(options, transport, delay);
        var generator = new OpenAICompatibleEmbeddingGenerator(new OpenAIConfig
        {
            BaseUrl = new Uri(options.BaseUrl, "v1/").ToString(),
            ApiKey = options.ApiKey,
            HttpClient = http,
        });
        return new IronHiveEmbeddingModel(generator, options.Model, options.ExtraBody, ownsGenerator: true, ownedHttp: http);
    }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var capture = ResponseCapture.Begin();
        var started = Stopwatch.GetTimestamp();
        var response = await _generator.EmbedBatchAsync(_model, texts, _options, cancellationToken).ConfigureAwait(false);
        var latencyMs = capture.LatencyMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var vectors = new float[texts.Count][];
        // A result without an index is in input order.
        foreach (var (result, position) in response.Results.Select((r, i) => (r, i)))
        {
            var index = result.Index ?? position;
            vectors[index] = result.Embedding ?? throw new InvalidOperationException($"The provider returned no vector for input {index}.");
        }

        if (Array.FindIndex(vectors, v => v is null) is var missing and >= 0)
        {
            throw new InvalidOperationException($"The provider returned no vector for input {missing}.");
        }

        var model = response.Model ?? _model;
        var tokens = response.InputTokens ?? 0;
        var recorded = new JsonObject { ["model"] = model, ["usage"] = new JsonObject { ["prompt_tokens"] = tokens } }.ToJsonString();
        return new EmbeddingResult(vectors, model, tokens, latencyMs, recorded);
    }

    public void Dispose()
    {
        if (_ownsGenerator)
        {
            (_generator as IDisposable)?.Dispose();
        }

        _ownedHttp?.Dispose();
    }
}
