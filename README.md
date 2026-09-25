# Gil

A habit runtime for LLM decisions. Repeated requests are answered from memory or from a small tree of cheap
single-token judgments; only what the tree cannot settle goes to full generation, narrowed to the category the
tree already confirmed. Answers that keep coming back can be promoted into habits, behind a flag and a review gate.

**Status: early.** The library is being built from a specification whose behaviour was measured first in a
research harness. Nothing is published yet.

## Layout

| Project | Contents |
|---|---|
| `Gil.Abstractions` | Records and ports: the decision tree, model calls, traversal steps, telemetry sink, habit statistics |
| `Gil` | The runtime. Currently: the SQLite telemetry store, the tree YAML reader/writer, chat models through any IronHive message generator (a ready-made one for OpenAI-compatible servers, with retry rules for busy shared servers and the response kept as received) with calibrated call pricing, the single-token judge, the greedy traverser, output contracts, the fallback generator, slot filling, the resolver (memory → tree → narrowed fallback → full fallback), embedding memory, habit statistics (visits per node, and per-judgment credit and blame from feedback, kept as raw counts), an optional exploration rate that cross-checks accepted answers against the full fallback, optional shadows (answers already known at a node but not yet habits, shown beside its habits so that picking one defers to the fallback instead of letting a similar sibling absorb the request), and promotion proposals (a confirmed fallback answer that keeps recurring at a node, proposed as a habit when it saves more than the judgment it adds, with the tree before and after for review), and deactivation proposals (unreliable, disputed or stale habits, each for its heaviest reason, with age counted in requests), and differentiation signals (a node at its label capacity, and answers repeating at a node with children, told apart as missed, belonging under one child, or needing a new category) |
| `Gil.Tests` | Unit tests and the compatibility fixture writer |

The telemetry file layout is a contract: analysis tools read it directly, so columns may be added but never
renamed or repurposed. `Writes_the_compatibility_fixture` produces a small synthetic store other
implementations can open to check they read the same layout (set `GIL_COMPAT_FIXTURE` to choose where).

## Usage

A request goes through memory first, then the tree of one-token judgments, then a fallback narrowed to the
category the tree confirmed, then the full fallback. Every model call is recorded and priced in the telemetry
store, and feedback on an answer is what memory, habit statistics and promotion learn from.

```csharp
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
```

A task whose answers are free text may gain nothing from judgments; define it with a bare root (`id: root`) and
it goes from memory straight to the fallback without a judgment call.

Promotion never edits the tree by itself. Whoever operates the task runs a round, reviews the result against the
authored YAML and applies what they accept:

```csharp
var proposer = new RepeatedOutputProposer(
    new PromotionPolicy(MinSupport: 3),
    store.JudgeEnergyByNode(task.Name),
    JudgeCostModel.Fit(store.JudgeCostSamples(task.Name)));
var review = Promotion.Review(tree, proposer.Propose(tree, store.PromotionCandidates(task.Name)));
File.WriteAllText("support.proposed.yaml", review.After);
```

`Deactivation.Propose` and `Differentiation.Capacity` / `Differentiation.Anchored` produce the other two review
lists: habits to retire, and nodes to split or categories to add.

Costs are only as real as the coefficients. A self-hosted server reports how long each call took; once a few hundred
calls are recorded, fit the coefficients to those times and price with them from then on (for an API, use its
published prices instead):

```csharp
var fitted = EnergyModel.Fit(store.ServerTimeSamples("my-model"));
```

## Build and test

Requires the .NET 10 SDK.

```
dotnet build Gil.slnx
dotnet test --solution Gil.slnx
```

## License

Apache-2.0
