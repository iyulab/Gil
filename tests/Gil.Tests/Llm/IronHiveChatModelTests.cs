using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Gil.Llm;
using IronHive.Abstractions.Messages;
using IronHive.Abstractions.Messages.Content;

namespace Gil.Tests.Llm;

public sealed class IronHiveChatModelTests
{
    private const string Judged = """
        {"model":"served-model","choices":[{"message":{"content":"B"},"logprobs":{"content":[{"token":"B","logprob":-0.05,
         "top_logprobs":[{"token":"B","logprob":-0.05},{"token":"A","logprob":-3.1}]}]}}],
         "usage":{"prompt_tokens":320,"completion_tokens":1,"prompt_tokens_details":{"cached_tokens":64}},
         "timings":{"prompt_ms":41.5,"predicted_ms":12.0}}
        """;

    private static readonly ChatRequest Judge = new()
    {
        Messages = [new ChatMessage("system", "Answer with one label."), new ChatMessage("user", "Which one?")],
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
        result.RawResponse.Should().Be(Judged, "the body is kept as received");
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
        sent["temperature"]!.GetValue<double>().Should().Be(0);
        var messages = sent["messages"]!.AsArray();
        (messages[0]!["role"]!.GetValue<string>(), messages[1]!["role"]!.GetValue<string>()).Should().Be(("system", "user"));
        Text(messages[0]!).Should().Be("Answer with one label.");
        Text(messages[1]!).Should().Be("Which one?");
        sent["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        handler.Paths.Single().Should().Be("/v1/chat/completions");
    }

    [Fact]
    public async Task Fields_set_on_the_endpoint_go_with_every_request_and_a_request_field_replaces_one_of_the_same_name()
    {
        var endpoint = new JsonObject
        {
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["cache_prompt"] = true,
        };
        var (model, handler) = Model(endpoint, Respond(HttpStatusCode.OK, Judged), Respond(HttpStatusCode.OK, Judged));
        var plain = Judge with { ExtraBody = null };
        var own = Judge with { ExtraBody = new JsonObject { ["cache_prompt"] = false } };

        await model.CompleteAsync(plain, TestContext.Current.CancellationToken);
        await model.CompleteAsync(own, TestContext.Current.CancellationToken);

        var (first, second) = (JsonNode.Parse(handler.Bodies[0])!, JsonNode.Parse(handler.Bodies[1])!);
        first["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        first["cache_prompt"]!.GetValue<bool>().Should().BeTrue();
        second["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        second["cache_prompt"]!.GetValue<bool>().Should().BeFalse("the request's field replaces the endpoint's");
        endpoint["cache_prompt"]!.GetValue<bool>().Should().BeTrue("the configured fields are not changed by a request");
        own.ExtraBody!.Should().ContainSingle();
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

        await call.Should().ThrowAsync<Exception>();
        handler.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task A_timed_out_request_is_resent_only_as_often_as_allowed()
    {
        // The server may already be working on a timed-out request; resending freely would queue it twice.
        var (model, handler) = Model(Hang, Hang, Respond(HttpStatusCode.OK, Judged));

        var call = () => model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<TimeoutException>();
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

        using var model = IronHiveChatModel.OpenAICompatible(new OpenAICompatibleOptions
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

    [Fact]
    public async Task Any_generator_is_read_through_the_abstraction_and_its_system_prompt_is_the_leading_system_message()
    {
        var generator = new FakeGenerator(new MessageResponse
        {
            Model = "reported-model",
            Message = new Message { Role = MessageRole.Assistant, Content = [new TextMessageContent { Value = "B" }] },
            TokenUsage = new MessageTokenUsage { InputTokens = 30, CachedInputTokens = null, OutputTokens = 1 },
            LogProbabilities = [new TokenLogProbability("B", -0.1, [new TokenAlternative("B", -0.1), new TokenAlternative("A", -2.4)])],
            ExtraBody = new JsonObject { ["timings"] = new JsonObject { ["prompt_ms"] = 3.5 } },
        });
        using var model = new IronHiveChatModel(generator, "asked-model", new JsonObject { ["cache_prompt"] = true });

        var result = await model.CompleteAsync(Judge, TestContext.Current.CancellationToken);

        var sent = generator.Requests.Single();
        sent.ExtraBody!["cache_prompt"]!.GetValue<bool>().Should().BeTrue("the model's own fields go with requests to any generator");
        (sent.Model, sent.System, sent.MaxTokens, sent.LogProbabilities!.TopAlternatives).Should().Be(("asked-model", "Answer with one label.", 1, 20));
        sent.Messages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        sent.ExtraBody!["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().Should().BeFalse();
        (result.Model, result.Content, result.FirstToken).Should().Be(("reported-model", "B", "B"));
        result.TopLogprobs.Should().Equal(new TokenLogprob("B", -0.1), new TokenLogprob("A", -2.4));
        (result.PromptTokens, result.CachedTokens, result.GpuPromptMs, result.GpuPredictedMs).Should().Be((30, 0, 3.5, (double?)null));
        JsonNode.Parse(result.RawResponse)!["extra"]!["timings"]!["prompt_ms"]!.GetValue<double>().Should().Be(3.5, "without a Gil transport the raw response is what IronHive reports");
    }

    [Fact]
    public async Task A_system_message_after_the_conversation_starts_is_refused()
    {
        using var model = new IronHiveChatModel(new FakeGenerator(new MessageResponse()), "m");

        var call = () => model.CompleteAsync(
            Judge with { Messages = [new ChatMessage("user", "hi"), new ChatMessage("system", "late")] },
            TestContext.Current.CancellationToken);

        await call.Should().ThrowAsync<ArgumentException>();
    }

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hang =
        async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body) =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static string Text(JsonNode message) =>
        message["content"] is JsonValue text ? text.GetValue<string>() : string.Concat(message["content"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()));

    private static (IronHiveChatModel Model, ScriptedHandler Handler) Model(
        params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script) => Model(null, script);

    private static (IronHiveChatModel Model, ScriptedHandler Handler) Model(
        JsonObject? extraBody, params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] script)
    {
        var handler = new ScriptedHandler(script);
        var options = new OpenAICompatibleOptions
        {
            BaseUrl = new Uri("http://model.test/"),
            ApiKey = "key",
            Model = "configured-model",
            Timeout = TimeSpan.FromMilliseconds(200),
            ExtraBody = extraBody,
        };
        return (IronHiveChatModel.OpenAICompatible(options, handler, (_, _) => Task.CompletedTask), handler);
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

    private sealed class FakeGenerator(MessageResponse response) : IMessageGenerator
    {
        public List<MessageGenerationRequest> Requests { get; } = [];

        public Task<MessageResponse> GenerateMessageAsync(MessageGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<StreamingMessageResponse> GenerateStreamingMessageAsync(MessageGenerationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountTokensAsync(MessageGenerationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
