using System.ClientModel;
using AwesomeAssertions;
using Gil.Llm;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Gil.Tests.Llm;

public sealed class EmbeddingGeneratorModelTests
{
    [Fact]
    public async Task Reads_the_vectors_in_order_with_the_reported_model_and_input_tokens()
    {
        var generator = new Scripted(
            [new Embedding<float>(new float[] { 1, 0 }) { ModelId = "served" }, new Embedding<float>(new float[] { 0, 1 }) { ModelId = "served" }],
            new UsageDetails { InputTokenCount = 9 });
        var options = new EmbeddingGenerationOptions { Dimensions = 2 };

        var result = await new EmbeddingGeneratorModel(generator, options).EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        result.Vectors.Should().HaveCount(2);
        result.Vectors[0].Should().Equal(1f, 0f);
        result.Vectors[1].Should().Equal(0f, 1f);
        (result.Model, result.PromptTokens).Should().Be(("served", 9));
        result.RawResponse.Should().Be("""{"model":"served","usage":{"prompt_tokens":9}}""");
        generator.Inputs.Should().Equal("a", "b");
        generator.Options.Should().BeSameAs(options);
    }

    [Fact]
    public async Task Without_a_reported_model_or_usage_it_names_the_generator_default_and_counts_no_tokens()
    {
        var generator = new Scripted([new Embedding<float>(new float[] { 1 })], usage: null, defaultModel: "configured");

        var result = await new EmbeddingGeneratorModel(generator).EmbedAsync(["a"], TestContext.Current.CancellationToken);

        (result.Model, result.PromptTokens).Should().Be(("configured", 0));
    }

    [Fact]
    public async Task A_generator_that_returns_a_different_number_of_embeddings_is_an_error()
    {
        var generator = new Scripted([new Embedding<float>(new float[] { 1 })], usage: null);

        var embedding = () => new EmbeddingGeneratorModel(generator).EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        await embedding.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1 embeddings for 2 inputs*");
    }

    [Fact]
    public async Task A_live_openai_compatible_server_through_microsoft_extensions_ai()
    {
        // Opt-in: GIL_LIVE_BASE_URL, GIL_LIVE_API_KEY and GIL_LIVE_EMBEDDING_MODEL.
        var baseUrl = Environment.GetEnvironmentVariable("GIL_LIVE_BASE_URL");
        var modelName = Environment.GetEnvironmentVariable("GIL_LIVE_EMBEDDING_MODEL");
        if (baseUrl is null || modelName is null)
        {
            Assert.Skip("GIL_LIVE_BASE_URL or GIL_LIVE_EMBEDDING_MODEL is not set");
        }

        var client = new OpenAIClient(
            new ApiKeyCredential(Environment.GetEnvironmentVariable("GIL_LIVE_API_KEY") ?? "none"),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/v1") });
        using var generator = client.GetEmbeddingClient(modelName).AsIEmbeddingGenerator();

        var result = await new EmbeddingGeneratorModel(generator).EmbedAsync(["my parcel has not arrived", "where is my package"], TestContext.Current.CancellationToken);

        result.Vectors.Should().HaveCount(2);
        result.Vectors[0].Length.Should().BeGreaterThan(0);
        (result.PromptTokens > 0, result.Model.Length > 0).Should().Be((true, true));
    }

    private sealed class Scripted(IReadOnlyList<Embedding<float>> embeddings, UsageDetails? usage, string? defaultModel = null)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public IReadOnlyList<string> Inputs { get; private set; } = [];
        public EmbeddingGenerationOptions? Options { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            (Inputs, Options) = ([.. values], options);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings) { Usage = usage });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("scripted", defaultModelId: defaultModel) : null;

        public void Dispose()
        {
        }
    }
}
