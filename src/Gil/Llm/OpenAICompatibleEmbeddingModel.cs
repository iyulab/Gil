using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gil.Llm;

/// <summary>Embeddings over the OpenAI-compatible HTTP API, with the same retry rules as chat completions.</summary>
/// <remarks>
/// A transport of its own because IronHive's embedding abstraction does not yet return what the call is priced and
/// recorded with: the input tokens the provider reports and the model that answered.
/// </remarks>
// TODO(upstream): replace with an adapter over IronHive's IEmbeddingGenerator once it returns provider-reported usage
// and the served model; remove this transport then.
public sealed class OpenAICompatibleEmbeddingModel : IEmbeddingModel, IDisposable
{
    private readonly HttpClient _http;
    private readonly OpenAICompatibleOptions _options;

    /// <param name="options">Endpoint and retries; <see cref="OpenAICompatibleOptions.Model"/> is the embedding model.</param>
    /// <param name="transport">The handler that sends; a socket handler by default.</param>
    /// <param name="delay">Backoff between retries; the real clock by default.</param>
    public OpenAICompatibleEmbeddingModel(OpenAICompatibleOptions options, HttpMessageHandler? transport = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _http = OpenAICompatibleHttp.CreateClient(options, transport, delay);
    }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var capture = ResponseCapture.Begin();
        var started = Stopwatch.GetTimestamp();
        var request = (JsonObject?)_options.ExtraBody?.DeepClone() ?? [];
        request["model"] = _options.Model;
        request["input"] = new JsonArray([.. texts.Select(t => JsonValue.Create(t))]);
        var body = request.ToJsonString();
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUrl, "v1/embeddings"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"v1/embeddings failed: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }

        var text = capture.Body ?? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var vectors = new float[texts.Count][];
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            vectors[row.GetProperty("index").GetInt32()] = [.. row.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle())];
        }

        var tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? _options.Model : _options.Model;
        // The vectors live in the index; the recorded response keeps everything but them.
        var recorded = new JsonObject { ["model"] = model, ["usage"] = new JsonObject { ["prompt_tokens"] = tokens } }.ToJsonString();
        return new EmbeddingResult(vectors, model, tokens, capture.LatencyMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds, recorded);
    }

    public void Dispose() => _http.Dispose();
}
