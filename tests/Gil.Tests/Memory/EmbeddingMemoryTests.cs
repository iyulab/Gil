using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Gil.Fallback;
using Gil.Llm;
using Gil.Memory;
using Gil.Ontology;
using Gil.Telemetry;
using Gil.Traverse;
using Microsoft.Data.Sqlite;

namespace Gil.Tests.Memory;

public sealed class EmbeddingMemoryTests
{
    private static readonly float[] Refund = [1, 0, 0];
    private static readonly float[] RefundNear = [0.95f, 0.31f, 0];
    private static readonly float[] Other = [0, 0, 1];

    [Fact]
    public async Task The_transport_reads_vectors_in_input_order_and_the_recorder_prices_them_with_its_own_model()
    {
        using var handler = new Respond("""{"model":"embedder","data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}],"usage":{"prompt_tokens":7}}""");
        var model = new OpenAICompatibleEmbeddingModel(
            new OpenAICompatibleOptions { BaseUrl = new Uri("http://model.test/"), ApiKey = "k", Model = "e", ExtraBody = new JsonObject { ["dimensions"] = 2 } }, handler);
        var sink = new ListSink();

        var (vectors, call) = await new EmbeddingRecorder(model, new EnergyModel(0, 0.01, 0, 0), sink).EmbedAsync(["a", "b"], "t", TestContext.Current.CancellationToken);

        vectors.Should().HaveCount(2);
        vectors[0].Should().Equal(1f, 0f);
        (call.Role, call.PromptTokens, call.Energy, call.Model).Should().Be(("embed", 7, 0.07, "embedder"));
        var sent = JsonNode.Parse(handler.Body!)!;
        sent["input"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("a", "b");
        (sent["model"]!.GetValue<string>(), sent["dimensions"]!.GetValue<int>()).Should().Be(("e", 2));
        call.RawResponse.Should().NotContain("embedding");
        sink.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task A_lookup_finds_the_nearest_remembered_answer_and_remembering_reuses_its_vector()
    {
        var model = new Vectors(Refund, RefundNear);
        var memory = new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(1, 0, 0, 0)));

        var (empty, cost) = await memory.LookupAsync("task", "refund please", "t1", TestContext.Current.CancellationToken);
        var remembered = await memory.RememberAsync("task", "t1", "refund please", "refund policy", TestContext.Current.CancellationToken);
        var (match, _) = await memory.LookupAsync("task", "can I get a refund", "t2", TestContext.Current.CancellationToken);

        (empty, cost, remembered).Should().Be((null, 1.0, 0.0)); // the second embedding was never needed
        match!.Source.Should().Be("t1");
        match.Answer.Should().Be("refund policy");
        match.Similarity.Should().BeApproximately(0.95, 0.01);
        model.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Forgetting_removes_the_answer_and_other_tasks_are_separate()
    {
        var memory = new EmbeddingMemory(new EmbeddingRecorder(new Vectors(Refund), new EnergyModel(0, 0, 0, 0)));
        memory.Seed("task", [("a", Refund, "x"), ("b", Other, "y")]);

        memory.Forget("task", "a");
        var (match, _) = await memory.LookupAsync("task", "q", "t", TestContext.Current.CancellationToken);

        match!.Source.Should().Be("b");
        memory.Count("other-task").Should().Be(0);
    }

    [Fact]
    public async Task Rebuilding_keeps_confirmed_answers_and_drops_overturned_ones_even_when_they_were_seeds()
    {
        var memory = new EmbeddingMemory(new EmbeddingRecorder(new Vectors(RefundNear), new EnergyModel(0, 0, 0, 0)));
        memory.Seed("task", [("seed:1", Refund, "wrong answer"), ("seed:2", Other, "other answer")]);
        FeedbackEntry[] history =
        [
            new("t1", "refund?", "memory", "wrong answer", new Recall("seed:1", 0.95, 0.9, true), "wrong", "right answer"),
            new("t2", "something", "fallback", "unconfirmed", null, "wrong", null),
        ];

        var count = await memory.RebuildAsync("task", history, "rebuild", clear: false, cancellationToken: TestContext.Current.CancellationToken);
        var (match, _) = await memory.LookupAsync("task", "refund?", "probe", TestContext.Current.CancellationToken);

        count.Should().Be(1);
        memory.Count("task").Should().Be(2); // seed:2 and t1
        (match!.Source, match.Answer).Should().Be(("t1", "right answer"));
    }

    [Fact]
    public async Task With_the_real_store_a_confirmed_answer_is_recalled_on_the_next_similar_request()
    {
        var directory = Directory.CreateTempSubdirectory("gil-memory-").FullName;
        try
        {
            var path = Path.Combine(directory, "m.sqlite");
            var tree = OntologyYaml.Parse("id: root\noptions: [{id: refund, kind: answer, label: refund, description: refunds, text: refund_policy}]").Root;
            var task = new TaskDefinition("support", new TreeAnswerContract(tree), tree, new TaskPolicy { Thresholds = new([0.5], 0.5), MemoryThreshold = 0.9 });
            using (var store = new SqliteTelemetryStore(path))
            {
                var memory = new EmbeddingMemory(new EmbeddingRecorder(new Vectors(Refund, RefundNear), new EnergyModel(1, 0, 0, 0), store));
                var resolver = new Resolver(
                    new GreedyTraverser(new AlwaysAccept()),
                    new FallbackGenerator(new CallRecorder(new NoModel(), new EnergyModel(0, 0, 0, 0))),
                    new SlotFiller(new CallRecorder(new NoModel(), new EnergyModel(0, 0, 0, 0))),
                    store,
                    memory);

                var first = await resolver.ResolveAsync(task, "refund please", "t1", TestContext.Current.CancellationToken);
                await resolver.FeedbackAsync(task, "t1", correct: true, cancellationToken: TestContext.Current.CancellationToken);
                var second = await resolver.ResolveAsync(task, "can I get a refund", "t2", TestContext.Current.CancellationToken);

                (first.Mode, second.Mode, second.Output).Should().Be(("habit/answer", "memory", "refund_policy"));
                store.FeedbackHistory("support").Should().ContainSingle().Which.TraceId.Should().Be("t1");

                var rebuilt = new EmbeddingMemory(new EmbeddingRecorder(new Vectors(Refund), new EnergyModel(0, 0, 0, 0)));
                (await rebuilt.RebuildAsync("support", store.FeedbackHistory("support"), "rebuild", cancellationToken: TestContext.Current.CancellationToken)).Should().Be(1);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_live_embedding_server_separates_related_from_unrelated_text()
    {
        // Opt-in: GIL_LIVE_BASE_URL, GIL_LIVE_API_KEY and GIL_LIVE_EMBEDDING_MODEL.
        var baseUrl = Environment.GetEnvironmentVariable("GIL_LIVE_BASE_URL");
        var modelName = Environment.GetEnvironmentVariable("GIL_LIVE_EMBEDDING_MODEL");
        if (baseUrl is null || modelName is null)
        {
            Assert.Skip("GIL_LIVE_BASE_URL or GIL_LIVE_EMBEDDING_MODEL is not set");
        }

        using var model = new OpenAICompatibleEmbeddingModel(new OpenAICompatibleOptions
        {
            BaseUrl = new Uri(baseUrl.TrimEnd('/') + "/"),
            ApiKey = Environment.GetEnvironmentVariable("GIL_LIVE_API_KEY") ?? "",
            Model = modelName,
        });
        var memory = new EmbeddingMemory(new EmbeddingRecorder(model, new EnergyModel(0, 1, 0, 0)));

        await memory.LookupAsync("live", "my parcel has not arrived yet", "t1", TestContext.Current.CancellationToken);
        await memory.RememberAsync("live", "t1", "my parcel has not arrived yet", "track_order", TestContext.Current.CancellationToken);
        var (near, _) = await memory.LookupAsync("live", "the package I ordered is still not here", "t2", TestContext.Current.CancellationToken);
        var (far, _) = await memory.LookupAsync("live", "I want to cancel my insurance policy", "t3", TestContext.Current.CancellationToken);

        near!.Similarity.Should().BeGreaterThan(far!.Similarity);
    }

    private sealed class Vectors(params float[][] script) : IEmbeddingModel
    {
        public int Calls { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            var vectors = texts.Select(_ => script[Math.Min(Calls, script.Length - 1)]).ToList();
            Calls++;
            return Task.FromResult(new EmbeddingResult(vectors, "e", 5, 1, "{}"));
        }
    }

    private sealed class AlwaysAccept : IJudge
    {
        public Task<Judgment> JudgeAsync(string state, IReadOnlyList<Candidate> candidates, string traceId, string? nodeId = null, int? layer = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Judgment
            {
                Probs = new Dictionary<string, double> { [candidates[0].Id] = 0.99 },
                Choice = candidates[0].Id,
                Confidence = 0.99,
                NoneProb = 0.01,
                LabelMass = 0.99,
                Trusted = true,
                Call = new CallRecord { CallId = "c", TraceId = traceId, CreatedAt = DateTimeOffset.UnixEpoch, Role = "judge", Model = "m", PromptTokens = 0, CachedTokens = 0, CompletionTokens = 0, LatencyMs = 0, Energy = 1 },
            });
    }

    private sealed class NoModel : IChatModel
    {
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no generation expected");
    }

    private sealed class Respond(string body) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
