using System.Net;
using AwesomeAssertions;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible.ChatCompletion;
using IronHive.Providers.OpenAI.Compatible.Embedding;

namespace Gil.Tests.Llm;

/// <summary>
/// The OpenAI-compatible adapters hand IronHive a client they own and dispose themselves. That is only sound if
/// IronHive uses an injected client as given: it may be already used, keeps its settings, and outlives the generator.
/// These tests pin that contract on the generators directly, so an IronHive upgrade that changes it fails here.
/// </summary>
public sealed class InjectedHttpClientTests
{
    private const string Chat = """{"model":"m","choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";
    private const string Embedding = """{"model":"m","data":[{"index":0,"embedding":[0.5,0.25]}],"usage":{"prompt_tokens":2}}""";

    [Fact]
    public async Task Chat_generator_uses_an_already_used_client_as_given_and_leaves_it_to_its_owner()
    {
        var (http, handler) = await UsedClient(Chat);

        using (var generator = new ChatCompletionMessageGenerator(Config(http)))
        {
            var response = await generator.GenerateMessageAsync(
                new MessageGenerationRequest
                {
                    Model = "m",
                    Messages = [new Message { Role = MessageRole.User, Content = [new TextMessageContent { Value = "hi" }] }],
                },
                TestContext.Current.CancellationToken);
            response.Message!.Content.OfType<TextMessageContent>().Single().Value.Should().Be("ok");
        }

        await ShouldBeUntouched(http, handler);
    }

    [Fact]
    public async Task Embedding_generator_uses_an_already_used_client_as_given_and_leaves_it_to_its_owner()
    {
        var (http, handler) = await UsedClient(Embedding);

        using (var generator = new OpenAICompatibleEmbeddingGenerator(Config(http)))
        {
            var response = await generator.EmbedBatchAsync("m", ["hi"], null, TestContext.Current.CancellationToken);
            response.Results.Single().Embedding.Should().Equal(0.5f, 0.25f);
        }

        await ShouldBeUntouched(http, handler);
    }

    private static OpenAIConfig Config(HttpClient http) => new()
    {
        BaseUrl = "http://model.test/v1/",
        ApiKey = "key",
        HttpClient = http,
    };

    /// <summary>A client with its own settings, used once before any generator sees it.</summary>
    private static async Task<(HttpClient Http, Recorder Handler)> UsedClient(string body)
    {
        var handler = new Recorder(body);
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(37) };
        http.DefaultRequestHeaders.Add("X-Owner", "caller");
        (await http.GetAsync("http://model.test/warm-up", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        return (http, handler);
    }

    private static async Task ShouldBeUntouched(HttpClient http, Recorder handler)
    {
        http.Timeout.Should().Be(TimeSpan.FromSeconds(37), "the generator must not overwrite the owner's timeout");
        http.BaseAddress.Should().BeNull("the endpoint comes from the config, not from the shared client");
        http.DefaultRequestHeaders.GetValues("X-Owner").Should().Equal("caller");
        http.DefaultRequestHeaders.Contains("Authorization").Should().BeFalse("credentials go on the request, not on the shared client");
        handler.Disposed.Should().BeFalse("disposing the generator must leave the injected client alive");
        handler.Authorizations.Skip(1).Should().OnlyContain(a => a == "Bearer key", "the generator's requests still carry its key");

        (await http.GetAsync("http://model.test/after", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        handler.Authorizations.Last().Should().BeNull("the key did not leak into the client's later requests");
        http.Dispose();
        handler.Disposed.Should().BeTrue();
    }

    private sealed class Recorder(string body) : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
