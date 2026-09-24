using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gil.Llm;

/// <summary>Embeddings over the OpenAI-compatible HTTP API, with the same retry rules as chat completions.</summary>
public sealed class OpenAICompatibleEmbeddingModel : IEmbeddingModel
{
    private readonly OpenAICompatibleHttp _http;

    /// <param name="http">The client to send with.</param>
    /// <param name="options">Endpoint and retries; <see cref="OpenAICompatibleOptions.Model"/> is the embedding model.</param>
    /// <param name="delay">Backoff between retries; the real clock by default.</param>
    public OpenAICompatibleEmbeddingModel(HttpClient http, OpenAICompatibleOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = new OpenAICompatibleHttp(http, options, delay);
    }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var body = new JsonObject { ["model"] = _http.Options.Model, ["input"] = new JsonArray([.. texts.Select(t => JsonValue.Create(t))]) }.ToJsonString();
        var (text, latencyMs) = await _http.PostAsync("v1/embeddings", body, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var vectors = new float[texts.Count][];
        foreach (var row in root.GetProperty("data").EnumerateArray())
        {
            vectors[row.GetProperty("index").GetInt32()] = [.. row.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle())];
        }

        var tokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? _http.Options.Model : _http.Options.Model;
        // The vectors live in the index; the recorded response keeps everything but them.
        var recorded = new JsonObject { ["model"] = model, ["usage"] = new JsonObject { ["prompt_tokens"] = tokens } }.ToJsonString();
        return new EmbeddingResult(vectors, model, tokens, latencyMs, recorded);
    }
}
