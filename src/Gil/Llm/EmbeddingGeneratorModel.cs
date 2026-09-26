using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Gil.Llm;

/// <summary>
/// Embeddings through a Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> — any provider
/// that library covers (OpenAI, Azure OpenAI, Ollama, ONNX and others), with its middleware.
/// </summary>
/// <remarks>
/// The model is the one the embeddings report, else the generator's default. Tokens are the reported input tokens,
/// else 0, so a generator that reports no usage prices its lookups at the energy model's fixed cost only.
/// </remarks>
/// <param name="generator">The generator to call; the caller keeps ownership.</param>
/// <param name="options">Sent with every request, for example the model or the vector dimensions.</param>
public sealed class EmbeddingGeneratorModel(IEmbeddingGenerator<string, Embedding<float>> generator, EmbeddingGenerationOptions? options = null)
    : IEmbeddingModel
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator = generator ?? throw new ArgumentNullException(nameof(generator));

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var started = Stopwatch.GetTimestamp();
        var embeddings = await _generator.GenerateAsync(texts, options, cancellationToken).ConfigureAwait(false);
        var latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException($"The generator returned {embeddings.Count} embeddings for {texts.Count} inputs.");
        }

        var model = embeddings.Select(e => e.ModelId).FirstOrDefault(m => !string.IsNullOrEmpty(m))
            ?? options?.ModelId
            ?? _generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId
            ?? "unknown";
        var tokens = (int)(embeddings.Usage?.InputTokenCount ?? 0);
        // The vectors live in the index; the recorded response keeps everything but them.
        var recorded = new JsonObject { ["model"] = model, ["usage"] = new JsonObject { ["prompt_tokens"] = tokens } }.ToJsonString();
        return new EmbeddingResult([.. embeddings.Select(e => e.Vector.ToArray())], model, tokens, latencyMs, recorded);
    }
}
