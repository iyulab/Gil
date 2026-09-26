# Gil

A habit runtime for LLM decisions. Repeated requests are answered from memory or from a small tree of cheap
single-token judgments; only what the tree cannot settle goes to full generation, narrowed to the category the
tree already confirmed. Answers that keep coming back can be promoted into habits, behind a flag and a review gate.

**Status: early.** The library is being built from a specification whose behaviour was measured first in a
research harness. Published on NuGet as `Gil` (with `Gil.Abstractions`); the API may still change within 0.x.

## When to use it

Gil is for a task that keeps receiving requests of the same few kinds — support tickets, service requests,
recurring log messages — where each request needs one answer from a known set of categories and someone can say,
at least some of the time, whether the answer was right. It sits in front of a model you already call:

- **Memory** answers a request that means the same as one whose answer was confirmed. Like a semantic cache, but
  only confirmed answers enter it, and an answer later marked wrong is forgotten.
- **The tree** of one-token judgments settles what memory missed when the categories are clear enough to judge,
  and narrows generation to the confirmed category when they are not.
- **Every call is recorded and priced**, so whether the tree pays for itself is read off the log rather than assumed.

What was measured: wherever requests repeated, memory did most of the work. Behind memory, the tree with narrowed
generation beat full generation — one to three points more accurate at about half the cost — on an English benchmark
with a hosted model. With a mid-size open model on non-English service data it made no difference to accuracy, and
its cost ran from somewhat lower to nearly double, depending on how long the full generation prompt was. Promoting
answers into habits did not pay on the realistic datasets and stays off unless a task turns it on. Measure it on your
own log before relying on it.

It is not the right tool for free conversation (there is no answer to remember or category to judge), for choosing
between models (a router does that), or for a task that never gets feedback (memory then stays empty — see
"Memory lives in the process" below for filling it from answers you already have confirmed).

## Layout

| Project | Contents |
|---|---|
| `Gil.Abstractions` | Records and ports: the decision tree, model calls, traversal steps, telemetry sink, habit statistics |
| `Gil` | The runtime. Currently: the SQLite telemetry store, the tree YAML reader/writer, chat models through any IronHive message generator (a ready-made one for OpenAI-compatible servers, with retry rules for busy shared servers and the response kept as received) with calibrated call pricing, the single-token judge, the greedy traverser, output contracts, the fallback generator, slot filling, the resolver (memory → tree → narrowed fallback → full fallback), embedding memory, habit statistics (visits per node, and per-judgment credit and blame from feedback, kept as raw counts), an optional exploration rate that cross-checks accepted answers against the full fallback, optional shadows (answers already known at a node but not yet habits, shown beside its habits so that picking one defers to the fallback instead of letting a similar sibling absorb the request), and promotion proposals (a confirmed fallback answer that keeps recurring at a node, proposed as a habit when it saves more than the judgment it adds, with the tree before and after for review), and deactivation proposals (unreliable, disputed or stale habits, each for its heaviest reason, with age counted in requests), and differentiation signals (a node at its label capacity, and answers repeating at a node with children, told apart as missed, belonging under one child, or needing a new category) |
| `Gil.Tests` | Unit tests and the compatibility fixture writer |

The telemetry file layout is a contract: analysis tools read it directly, so columns may be added but never
renamed or repurposed. `Writes_the_compatibility_fixture` produces a small synthetic store other
implementations can open to check they read the same layout (set `GIL_COMPAT_FIXTURE` to choose where).

The same contract is checked the other way on every build: judgments, traversal, prompts, the resolver (with and
without memory, including memory failures), shadows, promotion, deactivation, differentiation and tree files are
replayed from synthetic fixtures under `tests/fixtures/conformance`, which the reference implementation produced from a
made-up support task and a deterministic model. Set the matching `GIL_COMPAT_*` variable to replay a recorded run instead.

## Usage

```sh
dotnet add package Gil
```

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
var remembered = new EmbeddingMemory(new EmbeddingRecorder(embedder, new EnergyModel(Fixed: 0, PerFreshPromptToken: 0.05, PerCachedToken: 0, PerOutputToken: 0), store));
var memory = new CircuitBreakingMemory(remembered, cooldown: TimeSpan.FromSeconds(30));

var resolver = new Resolver(
    new GreedyTraverser(new SingleTokenJudge(recorder)),
    new FallbackGenerator(recorder),
    new SlotFiller(recorder),
    sink: store,
    memory: memory,
    statistics: store,
    shadowEvidence: store);

var tree = OntologyYaml.Load("support.yaml").Root;
// The last argument is the language support.yaml is written in: every prompt the task sends is worded in it.
var task = new TaskDefinition("support", new TreeAnswerContract(tree), tree, new TaskPolicy
{
    // No defaults: a judgment's probability is not a calibrated accuracy, and the right threshold depends on the task.
    Thresholds = new Thresholds(PerLayer: [0.7], Leaf: 0.9),
    FallbackScope = FallbackScope.Path,
    // Similarity scales differ between embedding models, so this has no default either; without it memory is off.
    MemoryThreshold = 0.9,
}, PromptLanguage.English);

var result = await resolver.ResolveAsync(task, "I lost my card");
Console.WriteLine($"{result.Mode}: {result.Output}");
await resolver.FeedbackAsync(task, result.TraceId, correct: true);
```

The tree file names the categories and, under each, the answers a request can get. Only `id` is required; a
field left out takes the value shown in the comment, and the writer leaves out any field equal to it, so a file
written back after a review diffs cleanly against the one a person wrote:

```yaml
id: root
children:
  - id: billing
    description: Charges, invoices, refunds, payment methods   # default: empty — but this is what the judge reads
    options:
      - id: billing-refund
        text: "Refunds reach the original payment method within 5 business days."
        # kind: answer (or template with slots, or procedure with steps) · label: the id · origin: seed
  - id: cards
    description: Lost, stolen or blocked cards
    # label_scheme: inherited from the parent, letters at the root (digits holds fewer candidates)
    options:
      - id: cards-block
        text: "Freeze the card in the app under Cards > Freeze; a replacement ships in 3 days."
```

A node has either children (categories) or options (answers), not both. Loading warns when siblings' descriptions
share distinctive words, since the judge may not tell them apart.

`TreeAnswerContract` makes the fallback pick one of the tree's answers (or "none of these"); it never writes a new
one. A request that fits no answer exactly gets the closest listed one, or none. For answers written case by case,
give the task a `TextContract` instead: the fallback then generates, and answers confirmed by feedback can be
remembered and later proposed as habits.

A task whose answers are free text may gain nothing from judgments; define it with a bare root (`id: root`) and
it goes from memory straight to the fallback without a judgment call.

Every task declares the language its tree is written in, and every prompt the task sends — judgments, the
fallback, slot filling, the contract's instructions and the violations fed back on a retry — is worded in it. There
is no default for the same reason as the thresholds: "none of these" and the tree's candidates must share a language,
or out-of-scope requests stop being rejected. Two languages are built in: `PromptLanguage.Korean`, the wording the
behaviour was measured with (kept as measured), and `PromptLanguage.English`. To change a piece of the wording, start
from one of them, for example `PromptLanguage.English with { NoneOfThese = "Not applicable" }`.

Memory lives in the process. It is an index over the feedback in the log, so a restarted process starts empty
until it is rebuilt from that log — the same call fills it from answers confirmed elsewhere, once they are in the
store:

```csharp
await remembered.RebuildAsync(task.Name, store.FeedbackHistory(task.Name), traceId: "startup");
```

Promotion never edits the tree by itself. Whoever operates the task runs a round, reviews the result against the
authored YAML and applies what they accept:

```csharp
var proposer = new RepeatedOutputProposer(
    new PromotionPolicy(MinSupport: 3),
    store.JudgeEnergyByNode(task.Name),
    JudgeCostModel.Fit(store.JudgeCostSamples(task.Name)));
var review = Promotion.Review(tree, proposer.Propose(tree, store.PromotionCandidates(task.Name)));
File.WriteAllText("support.proposed.yaml", review.After);
store.RecordPromotionRound(task.Name, atIndex: store.Usage(task.Name).Total, review.Applied);
```

Record the round once you apply it: `atIndex` is how many of the task's requests were resolved before the proposer ran,
and `Applied` holds what the tree took (an empty round is recorded too). A later review or a restart then sees exactly
what was proposed when, instead of running the proposer again over a log that has grown since.

By default a proposal comes only from fallback answers confirmed as correct. When the right answer is one only
your organisation knows, it arrives as a correction instead; set `FromCorrections = true` on the policy to count
corrections too. A round that proposes nothing says so only by an empty list.

`Deactivation.Propose` and `Differentiation.Capacity` / `Differentiation.Anchored` produce the other two review
lists: habits to retire, and nodes to split or categories to add. `Differentiation.UnservedAsync` adds the requests
the task keeps answering "none of these" to, grouped by how similar they are — each group is either a category to
add or out-of-scope input to confirm as such. Record what the reviewer decided on each item, with
the reason when there is one — the log then shows what was accepted and rejected, and what reviewing the task costs:

```csharp
store.RecordReview(task.Name, ReviewKind.Promotion, review.Proposals[0].Habit.Id, ReviewDecision.Rejected, "same as billing-refund");
```

Costs are only as real as the coefficients. A self-hosted server reports how long each call took; once a few hundred
calls are recorded, fit the coefficients to those times and price with them from then on (for an API, use its
published prices instead):

```csharp
var fitted = EnergyModel.Fit(store.ServerTimeSamples("my-model"));
```

Accuracy is reported, not promised. `Stats` reads it off the log — per mode, on the requests that got feedback, with a
95% interval — together with the share of requests habits and memory answered and the cost per request over time
(priced again with the fitted coefficients, so early and late requests compare in one unit). Given the tree, it also
counts misroutes: requests whose confirmed answer lies outside the category the tree sent them to.

```csharp
var stats = store.Stats(task.Name, window: 100, pricing: fitted, tree: tree);
```

## Build and test

Requires the .NET 10 SDK.

```
dotnet build Gil.slnx
dotnet test --solution Gil.slnx
```

## License

Apache-2.0
