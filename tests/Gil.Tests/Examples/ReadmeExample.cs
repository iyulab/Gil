// The code of README.md's Usage section, compiled here so that the README breaks together with the API.
// ReadmeTests checks that every line of the README's code blocks appears here, in the same order.
using System.Text.Json.Nodes;
using Gil;
using Gil.Fallback;
using Gil.Habits;
using Gil.Judge;
using Gil.Llm;
using Gil.Memory;
using Gil.Ontology;
using Gil.Telemetry;
using Gil.Traverse;

namespace Gil.Tests.Examples;

internal static class ReadmeExample
{
    public static async Task RunAsync()
    {
        using var store = new SqliteTelemetryStore("gil.sqlite");
        using var chat = IronHiveChatModel.OpenAICompatible(new OpenAICompatibleOptions
        {
            BaseUrl = new Uri("http://localhost:8080/"),
            ApiKey = "",
            Model = "my-model",
            // Sent with every call. A server whose chat template reasons by default must be told not to: otherwise a
            // one-token judgment returns no label, and generation and slot filling spend their budget on reasoning.
            ExtraBody = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } },
        });

        // Calls are priced in the unit you report (GPU milliseconds or dollars), with coefficients fitted for your server.
        var recorder = new CallRecorder(chat, new EnergyModel(Fixed: 120, PerFreshPromptToken: 0.7, PerCachedToken: 0, PerOutputToken: 15), store);

        using var embedder = new OpenAICompatibleEmbeddingModel(new OpenAICompatibleOptions
        {
            BaseUrl = new Uri("http://localhost:8081/"),
            ApiKey = "",
            Model = "my-embedding-model",
        });
        // Memory is a cache in front of the tree: if it fails, requests go on as misses (the trace keeps the error), and
        // the breaker stops a dead endpoint from adding its retries to every request.
        var memory = new CircuitBreakingMemory(
            new EmbeddingMemory(new EmbeddingRecorder(embedder, new EnergyModel(Fixed: 0, PerFreshPromptToken: 0.05, PerCachedToken: 0, PerOutputToken: 0), store)),
            cooldown: TimeSpan.FromSeconds(30));

        var resolver = new Resolver(
            new GreedyTraverser(new SingleTokenJudge(recorder)),
            new FallbackGenerator(recorder),
            new SlotFiller(recorder),
            sink: store,
            memory: memory,
            statistics: store,
            shadowEvidence: store);

        var tree = OntologyYaml.Load("support.yaml").Root;
        var task = new TaskDefinition("support", new TreeAnswerContract(tree), tree, new TaskPolicy
        {
            // No defaults: a judgment's probability is not a calibrated accuracy, and the right threshold depends on the task.
            Thresholds = new Thresholds(PerLayer: [0.7], Leaf: 0.9),
            FallbackScope = FallbackScope.Path,
            // Similarity scales differ between embedding models, so this has no default either; without it memory is off.
            MemoryThreshold = 0.9,
        });

        var result = await resolver.ResolveAsync(task, "I lost my card");
        Console.WriteLine($"{result.Mode}: {result.Output}");
        await resolver.FeedbackAsync(task, result.TraceId, correct: true);

        var proposer = new RepeatedOutputProposer(
            new PromotionPolicy(MinSupport: 3),
            store.JudgeEnergyByNode(task.Name),
            JudgeCostModel.Fit(store.JudgeCostSamples(task.Name)));
        var review = Promotion.Review(tree, proposer.Propose(tree, store.PromotionCandidates(task.Name)));
        File.WriteAllText("support.proposed.yaml", review.After);
    }
}
