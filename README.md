# Gil

Confirmed-answer memory and cheap one-token judgments in front of an LLM. Repeated requests are answered from
memory or from a small tree of single-token judgments; only what they cannot settle goes to full generation,
narrowed to the category the tree already confirmed. Every call is recorded and priced. Promoting answers that keep
coming back into habits is an experimental feature: off unless a task turns it on, and behind a review gate.

**Status: early.** The library is being built from a specification whose behaviour was measured first in a
research harness. Published on NuGet as `Gil` (with `Gil.Abstractions`), `Gil.IronHive` and `Gil.Sqlite`; the API may still change within 0.x —
[CHANGELOG.md](https://github.com/iyulab/Gil/blob/main/CHANGELOG.md) lists each change and what to do about it.

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
| `Gil` | The runtime. Currently: the tree YAML reader/writer, calibrated call pricing, the single-token judge, the greedy traverser, output contracts, the fallback generator, slot filling, the resolver (memory → tree → narrowed fallback → full fallback, or memory alone without models), embedding memory (over any Microsoft.Extensions.AI embedding generator too, through `EmbeddingGeneratorModel`), lexical memory (character n-grams, no model), a form resolver that suggests a document's judged fields one at a time from values settled alongside its other values and from similar settled documents (no model), with threshold selection by replay, habit statistics (visits per node, and per-judgment credit and blame from feedback, kept as raw counts), an optional exploration rate that cross-checks accepted answers against the full fallback, optional shadows (answers already known at a node but not yet habits, shown beside its habits so that picking one defers to the fallback instead of letting a similar sibling absorb the request), and promotion proposals (a confirmed fallback answer that keeps recurring at a node, proposed as a habit when it saves more than the judgment it adds, with the tree before and after for review), and deactivation proposals (unreliable, disputed or stale habits, each for its heaviest reason, with age counted in requests), and differentiation signals (a node at its label capacity, and answers repeating at a node with children, told apart as missed, belonging under one child, or needing a new category) |
| `Gil.IronHive` | Chat and embedding models through any IronHive generator, with ready-made ones for OpenAI-compatible servers (retry rules for busy shared servers, the response kept as received). Optional: implement `IChatModel` and `IEmbeddingModel` yourself and `Gil` needs nothing else |
| `Gil.Sqlite` | The telemetry store: every trace and model call, habit statistics, promotion and review logs in one SQLite file whose layout is the contract below. Optional: `Gil` takes any `ITelemetrySink`, and a consumer that keeps no telemetry ships no SQLite native library |
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
dotnet add package Gil.IronHive   # the models below; skip it if you bring your own IChatModel
dotnet add package Gil.Sqlite     # the telemetry store below; skip it if you keep no telemetry
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

using var embedder = IronHiveEmbeddingModel.OpenAICompatible(new OpenAICompatibleOptions
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

Memory can embed through any Microsoft.Extensions.AI `IEmbeddingGenerator<string, Embedding<float>>` instead:
`new EmbeddingGeneratorModel(generator)` takes the place of the IronHive embedder, with the provider's usage and model
recorded the same way.
That includes a generator running on the same machine (an ONNX model, for example): Gil takes the generator, not
the model files, so which model to ship stays your decision.

Where no model is reachable at all, `LexicalMemory` compares requests by their characters (n-gram TF-IDF), with no
tokenizer and no call, and `new Resolver(memory, sink)` resolves with memory alone — a request memory cannot answer
abstains instead of reaching a model. Its similarities are on their own scale: pick the task's `MemoryThreshold` by
replaying your feedback history rather than reusing an embedding memory's value.

```csharp
var lexical = new LexicalMemory();
await MemoryReplay.From(store.FeedbackHistory("support")).ApplyAsync(lexical, "support", traceId: "startup");
var offline = new Resolver(lexical, store);
var bare = OntologyYaml.Parse("id: root").Root;   // the tree is not used without models
var answered = await offline.ResolveAsync(new TaskDefinition("support", new TextContract(), bare,
    new TaskPolicy { Thresholds = new([1.0], 1.0), MemoryThreshold = 0.5 }, PromptLanguage.English), "I lost my card");
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

Memory is a port, `IMemory`, so it can live elsewhere — a vector store several instances share, for example. The
contract is three calls: `LookupAsync` returns the task's nearest remembered request (keyed by its trace id, with the
similarity — the task's `MemoryThreshold` decides whether it answers) and the energy the lookup cost; `RememberAsync`
stores a confirmed answer under the request's trace id; `Forget` removes one. The resolver calls them as verdicts
arrive, and a lookup that throws is a miss (or an error, per `MemoryFailure`), so `CircuitBreakingMemory` wraps any of
them. To fill a store from the log, `MemoryReplay` reads the same rule off the feedback history and applies it through
those calls. Tenants belong in the task name — one task per tenant and job — so nothing crosses between them:

```csharp
await MemoryReplay.From(store.FeedbackHistory(task.Name)).ApplyAsync(memory, task.Name, traceId: "rebuild");
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

Live, the same story goes to your tracing and metrics backend. Gil emits through the BCL under one name,
`GilDiagnostics.Name` (`"Gil"`): a `gil.resolve` span per request with the task, mode, memory outcome (`off`, `hit`,
`miss`, or `failed` — a lookup that failed and was treated as a miss), energy and confidence, a `gil.call` span under
it per model call with its role, node and tokens, and the metrics `gil.resolutions` (by task, mode and memory
outcome), `gil.resolution.energy`, `gil.resolution.duration` and `gil.call.energy`. With OpenTelemetry, add
`AddSource(GilDiagnostics.Name)` and `AddMeter(GilDiagnostics.Name)`. Provider spans and token metrics (`gen_ai.*`) come
from the model client, not from Gil, and nest under `gil.call`.

## Filling forms

A form is a different shape of task: a document with several fields, some typed by a person (observed) and some to
be suggested (judged), settled one at a time. `FormResolver` suggests the judged fields without a model. It learns
from settled documents only: which values were settled alongside which values of the other fields, and optionally
which settled document is most similar.

```csharp
using Gil;
using Gil.Forms;
using Gil.Memory;

var form = new FormDefinition("ticket",
[
    new FieldDefinition("reporter", FieldRole.Observed) { UseAsEvidence = false },
    new FieldDefinition("component", FieldRole.Observed),
    new FieldDefinition("summary", FieldRole.Observed),
    new FieldDefinition("team", FieldRole.Judged) { MemoryThreshold = 0.5, KeyThreshold = 0.6 },
    new FieldDefinition("severity", FieldRole.Judged) { Candidates = ["low", "medium", "high"], Policy = FieldPolicy.ConfirmRequired },
], PromptLanguage.English);

var forms = new FormResolver(new FieldMemory(), new LexicalMemory());
await forms.RebuildAsync(form, saved);
```

`UseAsEvidence = false` keeps a field's value out of every other field's suggestions. Use it for fields that identify a
person, so that "this person, therefore this outcome" never hardens into memory. `ConfirmRequired` tells the
application that a field's suggestion should never be filled in without a look. `Off` never suggests the field at all.

Open a document once and pass values as they arrive. Every event returns fresh suggestions for the open fields whose
evidence changed. Settling the fields in order therefore keeps the rest up to date, and accepting a suggestion then
correcting it later leaves only the correction in memory:

```csharp
var session = forms.Open(form, "tickets/0412");
await session.ObserveAsync("reporter", "Kim");
var suggestions = await session.ObserveAsync("summary", "VPN drops every ten minutes");
var updated = await session.SettleAsync("team", Settlement.Accept("network"));
var document = session.Snapshot();
```

Each field is tried in a fixed order, and `FieldSuggestion.Source` says which layer the first candidate came from:
1. Values settled alongside the values the document already has, when the keys backing them score high enough
   (`KeyThreshold`).
2. The value of a similar settled document, when the field sets `MemoryThreshold`, a document memory is given, and the
   document is similar enough.
3. A model, when one is given (`IFieldModel`; `ResolverFieldModel` puts the resolver above behind it, one task per
   field). It is asked only when neither memory had anything, so it never overrides a value the document supports.

A layer answers only at or above its threshold. What falls short is still offered, after the layers that answered, as
a guess: values under a key below `KeyThreshold`, then the value settled most often for the field, then the nearest
document below `MemoryThreshold`, then the field's other values by how often they were settled. Replaying public
streams of settled documents ranked them so: the nearest document below its threshold was right less often than a
weaker key or the most frequent value, but far more often than the next most frequent ones. A field without
`MemoryThreshold` makes no promise from similar documents, but with a document memory the nearest one is still looked
up and offered in the same place. `FieldCandidate.Trusted` marks which is which, and `FieldSuggestion.Answered` says whether
the first candidate is an answer at all. When it is not, leave the field to the person: do not fill a guess in, and do
not mark it as the suggestion. Listing guesses as unmarked choices the person may pick is another matter. Replaying a
public stream of settled documents, showing the first guess that way saved about a third of the typing, even after
charging for the time to read it, against about one percent for answers alone. The first guess was also wrong more often
than right, though, and a value already filled in or highlighted is easily accepted without a look. A value's score
adds up its strength under each of the document's keys — how pure the key is for it, discounted when the key was seen
only a few times — so several keys that agree outweigh a single one, and the same score both ranks the values and is
compared with `KeyThreshold`. Replaying a public stream of settled documents with thresholds chosen for a precision of
0.8, this let the key layer answer about three times as often as trusting the strongest key alone, at the same
precision. A choice among a handful of values that every document has rarely decides another field, and without
`KeyThreshold` its values are guesses, so it never outranks a similar document that meets its threshold.

To show what a similar document's candidate rests on, create the resolver with `similarDocumentCount`: each suggestion
then carries `SimilarDocuments`, the most similar settled documents with their similarity and settled value, the
candidate's own document first. They come from the same lookup, so a person sees the evidence the suggestion was made
from — and whether the neighbours agree — at no extra cost. A document being edited is never among them: the saved
version of it is passed over for the next most similar document.

On a machine without a large model, leave the model out: measured on requests a lexical memory did not answer on its
own, small local models (2B and 4B, quantised) were right less often than that memory's nearest document, and took
seconds per field on an office laptop.

When no layer has a candidate, a person decides. Only a model reports `Confidence`: a frequency or a similarity is not
a probability. Give `ResolverFieldModel` the same sink and name each task `form/field`, and a model's suggestion is
traced like any resolved request — `FeedbackAsync` on its trace id then reaches the tree.

Where saving a document is what settles it, a draft must not reach memory. Ask with the values on screen instead of
opening a session, and put the document once it is saved; the saved version of the same document is left out of its
own evidence:

```csharp
var onScreen = await forms.SuggestAsync(form, "tickets/0412", valuesOnScreen);
// … on save:
await forms.RebuildAsync(form, [savedDocument]);
```

Save the snapshot the way you save documents. At startup, or when documents change elsewhere, pass them to
`RebuildAsync`. A document's contribution is always what its current values imply, so live settling and rebuilding
can be combined without counting anything twice.

Every `SettledDocument` carries `SettledAt`, when it was last settled — a file's last write time serves. Where
documents disagree, the later settlement wins: `FieldMemory` weighs each settlement by how many came after it under
the same key (`recencyDecay`, 0.95 by default — a correction overtakes an older practice without first outnumbering
it), ties go to the latest settlement, and documents whose evidence reads the same are one case that a
similar-document lookup answers with its latest settlement. The result is the same whatever order documents arrive in. When reopening a saved document, pass its time
and put its judged values back with `Settlement.Restore`, which records no acceptance or correction and leaves the
time as it was; only accepting or correcting a field moves it on:

```csharp
var reopened = forms.Open(form, saved[0].DocumentId, saved[0].SettledAt);
await reopened.SettleAsync("team", Settlement.Restore(saved[0].Values["team"]));
```

The right thresholds move as memory grows. Choose them again from time to time — for instance when memory has grown
by a tenth — by replaying the saved documents. Both are chosen in one replay, in the order the resolver consults the
layers: a similar document answers only where no key did, so its threshold is chosen on those lookups alone. A layer
without a `Chosen` threshold should not answer the field on its own; its `MostPrecise` still says how close it came —
the best precision it reached on at least `minimumAnswered` answers — so the application can say by how much the field
falls short:

```csharp
var layers = await ThresholdSelection.SelectLayersAsync(new FieldMemory(), new LexicalMemory(), form, "team", saved, targetPrecision: 0.9, minimumAnswered: 30);
var team = form.Field("team") with { KeyThreshold = layers.Key.Chosen?.Threshold, MemoryThreshold = layers.Memory.Chosen?.Threshold };
```

A chosen threshold also holds only for the version of Gil that chose it. A threshold rests on how a layer scores its
candidates and on what the replay asks, and a release may change either: 0.9.0 changed the key layer's scale, 0.10.0
and 0.11.0 what the replay asks. When you store a threshold, store the Gil version with it and choose again when that
version changes:

```csharp
var gilVersion = typeof(FormResolver).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
```

The replay looks each field up with the values that had arrived before it was settled — what its last suggestion was
made from. `Snapshot()` records that order in `SettledDocument.Arrival`; save it with the document. Without it, observed
values count as there from the start and judged fields as settled in the form's order. Even so, the precision a replay
reports can be optimistic. `targetPrecision` is checked on the documents the threshold is chosen on. Where settled documents
come in batches — several at once from one source, alike in their observed values — whether a near-identical
document's value is right tends to hold or fail for the whole batch. The replay then sees fewer independent cases than
it counts. Replaying two public streams, thresholds chosen for a target of 0.8 answered right 0.63 to 0.74 of the time
on the documents that followed, and those chosen for 0.7 answered right 0.64 to 0.72. The one exception, at 0.8, was a key layer
backed by settled judged fields in one of the streams. Wider statistical margins did not close the gap. To know the
precision a field actually delivers, check it on your own documents: choose on those settled up to a date and count
the answers on those settled after it.

Settle a record that arrives again — a retransmission, a re-save — under the same document id. The memories keep one
version per id, and the replay asks a document settled again without its earlier version, as a suggestion for a saved
document does. Under a new id the copy is new evidence: copies answer each other in the replay, and the threshold
promises far more than other documents get. Replaying a public stream in which about half the documents arrived twice,
copies under new ids made the key layer answer six times as often with 0.67 right against a promise of 0.91; under
the same ids, it answered as if no copy had arrived.

The two layers answer different kinds of documents. The key layer answers where a value the document already has
decides the field. The similar document layer answers the rest, which are harder. A form whose judged fields rest only
on observed fields (`DependsOn` naming observed fields alone) gets most of its answers from similar documents. In a
replay of a public stream of settled documents, that layer's answers were right about seven times in ten at most, even
among the most similar documents: documents whose observed values read almost the same had different settled values.
Thresholds chosen there for a target of 0.8 delivered 0.66 to 0.72 on the documents that followed. Where settled
documents come in batches that share their observed values, expect the similar document layer to fall short of a high
target, and read even `MostPrecise` as optimistic: it is measured on the same documents it was chosen on.

## Build and test

Requires the .NET 10 SDK.

```
dotnet build Gil.slnx
dotnet test --solution Gil.slnx
```

## License

Apache-2.0
