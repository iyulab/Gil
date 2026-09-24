using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Gil.Llm;

namespace Gil.Tests.Llm;

public sealed class OpenAICompatibleChatModelTests
{
    private const string Judged = """
        {"model":"served-model","choices":[{"message":{"content":"B"},"logprobs":{"content":[{"token":"B","logprob":-0.05,
         "top_logprobs":[{"token":"B","logprob":-0.05},{"token":"A","logprob":-3.1}]}]}}],
         "usage":{"prompt_tokens":320,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":64}},
         "timings":{"prompt_ms":41.5,"predicted_ms":12.0}}
        """;

    private static readonly ChatRequest Judge = new()
    {
        Messages = [new ChatMessage("user", "Which one?")],
        MaxTokens = 1,
        TopLogprobs = 20,
        ExtraBody = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } },
    };

    [Fact]
    public async Task Reads_first_token_alternatives_cached_tokens_and_server_timings()
    {
        var (model, _) = Model(Respond(HttpStatusCode.OK, Judged));

        var result = await model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        result.Model.Should().Be("served-model");
        result.Content.Should().Be("B");
        result.FirstToken.Should().Be("B");
        result.TopLogprobs.Should().Equal(new TokenLogprob("B", -0.05), new TokenLogprob("A", -3.1));
        (result.PromptTokens, result.CachedTokens, result.CompletionTokens).Should().Be((320, 64, 1));
        (result.GpuPromptMs, result.GpuPredictedMs).Should().Be((41.5, 12.0));
        result.RawResponse.Should().Contain("served-model");
    }

    [Fact]
    public async Task Sends_logprob_settings_and_merges_the_extra_body()
    {
        var (model, handler) = Model(Respond(HttpStatusCode.OK, Judged));

        await model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        var sent = JsonNode.Parse(handler.Bodies.Single())!;
        sent["model"]!.GetValue<string>().Should().Be("configured-model");
        sent["logprobs"]!.GetValue<bool>().Should().BeTrue();
        sent["top_logprobs"]!.GetValue<int>().Should().Be(20);
        sent["max_tokens"]!.GetValue<int>().Should().Be(1);
        sent["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        handler.Paths.Single().Should().Be("/v1/chat/completions");
    }

    [Fact]
    public async Task A_response_without_logprobs_or_timings_is_still_read()
    {
        var (model, _) = Model(Respond(HttpStatusCode.OK, """{"choices":[{"message":{"content":"hello"}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""));

        var result = await model.CompleteAsync(Judge with { TopLogprobs = null }, TestContext.Current.CancellationToken);

        result.Content.Should().Be("hello");
        result.FirstToken.Should().BeNull();
        result.TopLogprobs.Should().BeEmpty();
        (result.GpuPromptMs, result.CachedTokens).Should().Be((null, 0));
        result.Model.Should().Be("configured-model");
    }

    [Fact]
    public async Task Server_errors_and_rate_limits_are_retried()
    {
        var (model, handler) = Model(
            Respond(HttpStatusCode.ServiceUnavailable, "busy"),
            Respond(HttpStatusCode.TooManyRequests, "slow down"),
            Respond(HttpStatusCode.OK, Judged));

        var result = await model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        result.Content.Should().Be("B");
        handler.Bodies.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var (model, handler) = Model(Respond(HttpStatusCode.BadRequest, "bad"), Respond(HttpStatusCode.OK, Judged));

        var call = () => model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        (await call.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task A_timed_out_request_is_resent_only_as_often_as_allowed()
    {
        // The server may already be working on a timed-out request; resending freely would queue it twice.
        var (model, handler) = Model(Hang, Hang, Respond(HttpStatusCode.OK, Judged));

        var call = () => model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<OperationCanceledException>();
        handler.Bodies.Should().HaveCount(2); // the first attempt plus one resend
    }

    [Fact]
    public async Task A_recorded_call_is_priced_with_the_calibrated_model_and_annotated_before_it_is_written()
    {
        var (model, _) = Model(Respond(HttpStatusCode.OK, Judged));
        var sink = new ListSink();
        var recorder = new CallRecorder(model, new EnergyModel(Fixed: 125, PerFreshPromptToken: 0.74, PerCachedToken: 0, PerOutputToken: 15), sink);

        var record = await recorder.CompleteAsync(
            Judge, "judge", "t1", nodeId: "root", layer: 1, annotate: r => r with { Outcome = "accept" },
            cancellationToken: TestContext.Current.CancellationToken);

        record.Energy.Should().BeApproximately(125 + (0.74 * 256) + (15 * 1), 1e-9);
        sink.Calls.Should().ContainSingle().Which.Should().BeSameAs(record);
        record.Outcome.Should().Be("accept");
        (record.Role, record.TraceId, record.NodeId, record.Layer).Should().Be(("judge", "t1", "root", 1));
    }

    [Fact]
    public async Task A_live_server_returns_first_token_alternatives()
    {
        // Opt-in: set GIL_LIVE_BASE_URL, GIL_LIVE_API_KEY and GIL_LIVE_MODEL to run against a real endpoint.
        var baseUrl = Environment.GetEnvironmentVariable("GIL_LIVE_BASE_URL");
        if (baseUrl is null)
        {
            Assert.Skip("GIL_LIVE_BASE_URL is not set");
        }

        using var http = new HttpClient();
        var model = new OpenAICompatibleChatModel(http, new OpenAICompatibleOptions
        {
            BaseUrl = new Uri(baseUrl.TrimEnd('/') + "/"),
            ApiKey = Environment.GetEnvironmentVariable("GIL_LIVE_API_KEY") ?? "",
            Model = Environment.GetEnvironmentVariable("GIL_LIVE_MODEL") ?? "",
            Timeout = TimeSpan.FromMinutes(2),
        });

        var result = await model.CompleteAsync(
            Judge with { Messages = [new ChatMessage("user", "Answer with one letter. A: cat, B: dog. Which barks?")] },
            TestContext.Current.CancellationToken);

        result.TopLogprobs.Should().NotBeEmpty();
        result.PromptTokens.Should().BePositive();
        result.CompletionTokens.Should().Be(1);
    }

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hang =
        async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static (OpenAICompatibleChatModel Model, ScriptedHandler Handler) Model(
        params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script)
    {
        var handler = new ScriptedHandler(script);
        var options = new OpenAICompatibleOptions
        {
            BaseUrl = new Uri("http://model.test/"),
            ApiKey = "key",
            Model = "configured-model",
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        return (new OpenAICompatibleChatModel(new HttpClient(handler), options, (_, _) => Task.CompletedTask), handler);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script) : HttpMessageHandler
    {
        private int _next;

        public List<string> Bodies { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Paths.Add(request.RequestUri!.AbsolutePath);
            return await script[_next++](request, cancellationToken);
        }
    }
}
