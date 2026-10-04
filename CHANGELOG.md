# Changelog

Versions follow `0.x`: a minor release may change the public API. Each such change is listed under **Breaking**
with what to do.

## Unreleased

### Added

- **Judged fields can take several values.** `FieldDefinition.Multiple` marks a field whose value is a set; its values
  are settled in `SettledDocument.Sets` (`SettledDocument.Validate` checks that each field is kept where its kind
  belongs), with `Settlement.Set` and `Settlement.Restore(values)`. The settled field memory remembers each value on
  its own — one document's values weigh alike, as one settlement — and suggests each on its own, so several
  candidates can each be trusted; the values already chosen are evidence for the rest (`FieldMemory.Rank`'s
  `knownSets`) and are not offered again. A form session keeps the field open once settled, `FormResolver.SuggestAsync`
  has an overload that takes the chosen values, each value of a set is a key and an evidence line of its own for other
  fields, and `ThresholdSelection.SelectKeyThreshold` replays the values as picked one after another. The similar
  document layer does not suggest such a field, so it takes no `MemoryThreshold`. Nothing changes for forms without
  one.

- **`ThresholdSelection.SelectDependsOn` chooses the fields a judged field should rest on.** It replays the field's key
  layer with each evidence field alone, adds them in order of how often they answer at the target precision while each
  addition answers more often, and returns that set (`DependsOnChoice.DependsOn`) only if it answers more often than
  every evidence field together, with every replay it tried. Where a few fields decide a field with many values and
  the rest only blur it, resting on those few answers more often at the same precision.

### Changed

- **Threshold replays ask with the sets that had arrived before the field.** A single-valued field whose evidence
  includes a field with several values is now replayed with that field's values as keys, as it is suggested.
- **README: `DependsOn` for fields with many values, and filtering candidates by typed text.** Two measured
  recommendations: name the fields that decide a field with many values in `DependsOn` (chosen with
  `SelectDependsOn`), and, while a person types into a judged field, filter a longer candidate list by the typed text
  instead of dropping it.
- **A form's judged fields no longer embed the same evidence text once each.** `EmbeddingMemory` keeps the vectors of
  the texts it embedded recently, so the judged fields of one document — remembered or suggested by the same evidence,
  one task each — cost one embedding call instead of one per field. `FormResolver.RebuildAsync` has the document memory
  embed its documents' evidence texts ahead of the writes, in batches of up to 64 distinct texts, instead of one call
  per field per document. Memory holds the same entries in the same order as before (an embedding model whose vectors
  vary in the last digits with the batch they are computed in may differ there, as `EmbeddingMemory.RebuildAsync`
  already could). Where judged fields are evidence for each other, each field still reads a different text; those texts
  are now embedded in batches too.

### Dependencies

- `IronHive.Providers.OpenAI.Compatible` 0.45.1 → 0.50.0 (`Gil.IronHive`). Nothing `Gil.IronHive` uses changed shape;
  its connect timeout and retries stay Gil's own settings.

## 0.11.0

### Breaking

- **Threshold replays ask about each judged field with the values that had arrived before it, not the whole settled
  document.** Where judged fields are evidence for each other (a field without `DependsOn`), a field's last suggestion
  is made before the fields settled after it have values, yet the replays (`SelectKeyThreshold`, `SelectAsync`,
  `SelectLayersAsync`) looked it up with all of them — so the precision a chosen threshold promised was measured on more
  evidence than suggestions get, and suggestions fell short of it. `SettledDocument` gains `Arrival`, the order the
  document's values arrived in, and `FormSession.Snapshot()` fills it in; a replay looks each field up with the values
  ahead of it in that order and still remembers each document with all its values, as the form resolver does. Without
  an order, observed values count as there from the start and judged ones as settled in the form's order. What to do:
  save `Arrival` with a snapshot and pass it back when choosing thresholds, or leave it out where the form order is the
  order people fill in; then choose thresholds again. A form whose judged fields depend only on observed fields replays
  as before, unless an observed value changed after a field was settled. Code that constructs `SettledDocument` by position compiles unchanged; recompile against this version.

### Changed

- **README: a chosen threshold holds only for the Gil version that chose it.** A release may change how a layer scores
  or what the replay asks, as 0.9.0, 0.10.0 and this release did. Store the Gil version with a threshold and choose
  again when it changes.

## 0.10.0

### Breaking

- **`SqliteTelemetryStore` moved to its own package, `Gil.Sqlite`; `Gil` no longer depends on SQLite.** A consumer that
  kept no telemetry — only memories and the form resolver, say — still received `Microsoft.Data.Sqlite` and its native
  library, which a self-contained or Native AOT publish then shipped beside the executable unused. The store keeps its
  namespace (`Gil.Telemetry`), its constructor and its file layout, so existing files open as before. What to do: if you
  use `SqliteTelemetryStore`, add `dotnet add package Gil.Sqlite`. If you excluded SQLite's native assets yourself to keep
  them out of a publish (a direct `SQLitePCLRaw` reference with `ExcludeAssets`), remove that reference.

### Changed

- **A field's `Candidates` bound every layer, not only the model.** A judged field with a closed list could still be
  suggested a value outside it — by the key layer, by a similar document or as a guess — whenever such a value had been
  settled, for instance one since dropped from the list; it could even come first and be trusted. Now no layer offers a
  value outside the list. The key layer ranks only the values in it, so a threshold is compared with the best of those;
  the similar-document layer offers nothing when the most similar document's value is outside it (rather than a less
  similar document's), and `SimilarDocuments` leaves such documents out; a model's values outside it are dropped.
  Threshold replays (`SelectKeyThreshold`, `SelectAsync`, `SelectLayersAsync`) apply the same rule, so what they promise
  matches what is suggested. Such settlements are still remembered, and are suggested again if the list takes the value
  back. `FieldDefinition.Admits` tells whether a value lies in the list. What to do: nothing for a field without
  `Candidates`, or one whose settled values all lie in its list — suggestions and chosen thresholds are unchanged. If
  settled values fall outside a field's list, choose its thresholds again.
- **A `FieldMemory` lookup costs as much as a key has values, however long its history.** Every settlement used to send
  its key's weights back to be taken afresh, sorting all of that key's settlements on the next lookup, and a suggestion
  for an unsaved document scanned them all to see whether it was among them. A key present on nearly every document
  therefore made each suggestion slower as settlements accumulated. Now a settlement later than every earlier one under
  the key carries the weights along, and only one that arrives out of order or is taken away has them taken afresh —
  through the same fold, so either way reaches the same weights to the bit. Suggestions and chosen thresholds are
  unchanged: replaying public streams of settled documents gave the same suggestions and answers, with scores and
  thresholds equal to within 1e-12. Replaying about 95,000 log lines whose host and process recur on nearly every line
  took 11 seconds, where the first 22,000 had taken over nine minutes, and the time per line no longer grows.

### Fixed

- **Threshold selection no longer lets a document settled again be its own evidence.** `SelectKeyThreshold`,
  `SelectAsync` and `SelectLayersAsync` replay every settlement they are given; a document given twice under the same
  id — saved, then saved again — was asked about with its earlier version still remembered, so it answered itself and
  the replay promised more than suggestions deliver, since a suggestion for a saved document passes over its saved
  version. The replay now drops a document's earlier version before asking about the next. Results are unchanged when
  every id appears once.

## 0.9.0

### Breaking

- **The key layer ranks and trusts values by one score: the sum of their strengths under the document's keys.** A
  value's strength under a key is still its weighted count there over the key's weighted total plus one, so a key settled
  once adds at most 0.5. Values were ranked by the sum of their plain shares, but trusted by the strength of their
  strongest key alone, so the value most likely right was not always the one the threshold looked at, and agreement
  between keys counted for nothing. Now the same score ranks the values, is compared with `KeyThreshold`, is what
  `SelectKeyThreshold` and `SelectLayersAsync` replay, and is the keyed candidates' `FieldCandidate.Score`; `Evidence`
  names the key the value is strongest under. Replaying a public stream of settled documents with thresholds chosen for a
  precision of 0.8, the key layer answered about three times as often (641 answers against 215) at the same precision
  (0.81 against 0.77), and the first candidate was right slightly more often. **What to do**: a `KeyThreshold` chosen
  before this release is on the old scale — the new score reaches up to the number of keys — so choose it again with
  `SelectKeyThreshold` (or `SelectLayersAsync`) right after upgrading; until then it is too low and the key layer
  answers too readily. A threshold set by hand keeps its meaning for a document with a single key.

### Changed

- **A `LexicalMemory` lookup reads only the n-grams a row shares with the request.** Every n-gram lists the rows that
  hold it with their weights, side by side, and a lookup adds those up instead of walking every row's n-grams against
  the request's. Results are unchanged to the bit — the products are added in the same order — and a lookup over a large
  memory is about ten times faster: replaying two public streams of settled documents, suggesting six judged fields
  against 50,000 remembered documents took 60 and 35 ms at the median instead of 590 and 343 ms. The lists hold a copy
  of each row's weights, so what a row holds per n-gram takes about three quarters more space, and they are rebuilt
  whenever the weights are taken afresh — remembering a large history takes up to twice as long.

## 0.8.0

### Breaking

- **A form field without `MemoryThreshold` still looks for similar documents.** Null used to skip the similar document
  layer; it now means no promise from it. With a document memory, the nearest document is looked up, reported in
  `SimilarDocuments` and offered as a guess, so a field whose threshold could not be chosen keeps its evidence instead
  of losing it. `Recall.Threshold` is `double?`, null for a lookup made without a threshold, and a stored trace then has
  no `threshold` in its `recall`. Every judged field is now remembered and looked up in the document memory, which costs
  what it costs for a field with a threshold — an embedding per field and document with an embedding memory. **What to
  do**: nothing to keep the first guess you had, since the new one comes after the most frequent value; to keep a field
  away from the document memory, resolve it with a resolver that has none.
- **Guesses come in a new order**: values under a key below `KeyThreshold`, then the field's most frequent value, then
  the nearest document below `MemoryThreshold` — which used to come first — then the field's other values. Replaying
  two public streams of settled documents, the nearest document below its threshold was right less often than a weaker
  key or the most frequent value, and about twice as often as the next most frequent one (first guess right 0.28 and
  0.27 of the time in the new order, against 0.26 and 0.22 with the nearest document first).
  **What to do**: nothing, unless you relied on a guess's position; answers (`Trusted` candidates) are unchanged.

### Fixed

- **Every package carries the license text.** The packages declared Apache-2.0 by expression only, so a consumer
  collecting third-party notices from the packages found the name and no text; `LICENSE` now ships next to
  `README.md` in each package.

## 0.7.0

### Breaking

- **`ThresholdSelection` returns what the replay found even when no threshold meets the target.** `SelectAsync` and
  `SelectKeyThreshold` return a `ThresholdReplay` instead of a nullable `ThresholdChoice`, and `LayerThresholds.Key`
  and `LayerThresholds.Memory` are `ThresholdReplay`s. A field that fell short used to come back as `null`, losing how
  close it came; `ThresholdReplay.MostPrecise` now gives the best precision the layer reached on at least
  `minimumAnswered` answers, with `Lookups` and `Candidates` counting the lookups made and those that found anything.
  **What to do**: read the threshold from `Chosen` — `layers.Key.Chosen?.Threshold`,
  `(await ThresholdSelection.SelectAsync(…)).Chosen` — where you read the choice itself before.

### Changed

- **Forgetting a `LexicalMemory` row takes constant time** instead of renumbering every row after it — a case replaced
  by a newer document, as the form resolver and `ThresholdSelection` do on every settlement, no longer costs a pass over
  memory. Results are unchanged: ties still go to the answer remembered first.
- **README guidance on guesses and on the similar document layer.** A guess should not be filled in or marked as the
  suggestion, but listing guesses as unmarked choices is fine — replaying settled documents, it saved about a third of
  the typing against one percent for answers alone. A form whose judged fields rest only on observed fields gets most
  answers from the similar document layer, which can fall short of a high target where documents come in batches that
  share their observed values.
- **README: what `targetPrecision` promises.** It is met on the documents the threshold is chosen on. Where settled
  documents come in batches alike in their observed values, the documents that follow can fall short of it — replaying
  two public streams, 0.63–0.74 for a target of 0.8.

## 0.6.0

### Breaking

- **`FormResolver`'s constructor gained an optional `similarDocumentCount` parameter** (after `timeProvider`). Source
  compiles unchanged; a binary built against 0.5.x that constructs a `FormResolver` must be rebuilt.

### Added

- **`ThresholdSelection.SelectLayersAsync(fieldMemory, memory, form, field, documents, targetPrecision, minimumAnswered)`**
  chooses a field's `KeyThreshold` and `MemoryThreshold` together, in the order the form resolver consults the layers,
  and returns both as `LayerThresholds(Key, Memory)`. A similar document answers only where no key did, and those
  lookups are harder than the rest: a `MemoryThreshold` chosen on every lookup by `SelectAsync` promised more precision
  than the similar document layer then delivered on the documents that followed. Replaying a public stream of about
  10,000 settled documents, answers from a similar document were right 66–75% of the time under a threshold chosen for
  80%. `SelectAsync` on its own remains right for a field without a `KeyThreshold`.
- **`FieldSuggestion.SimilarDocuments`** — the most similar settled documents behind a suggestion's candidate from a
  similar document (document id, similarity, settled value), when the `FormResolver` is created with
  `similarDocumentCount`. They come from the lookup that made the candidate.
- **`IMemory.NearestAsync(task, state, count, traceId)`** ranks the nearest few remembered requests in one lookup.
  `LexicalMemory`, `EmbeddingMemory` and `CircuitBreakingMemory` implement it; the default, for other memories, returns
  only the nearest.

### Fixed

- **Editing a saved document no longer loses the similar document layer.** The saved version of the document was its
  own nearest document, and the lookup counted it as a miss, so the layer suggested nothing even when other documents
  were close. It is now passed over for the next most similar document, as the field memory already left the document's
  own settlements out.

## 0.5.2

### Fixed

- **`ThresholdSelection` no longer admits a band of answers whose own precision falls short of the target.** It
  chose the lowest score at which the answers above it, taken together, met the target — so many good answers high up
  could carry a band of weak ones below them. A form whose settled documents repeat one another was hit hardest: the
  repeats answered each other correctly by their own keys, and a key backing its value about one time in four then
  cleared `KeyThreshold` and answered. Precision is now fitted as a non-decreasing function of the score (isotonic
  regression), and the threshold is the lowest score down to which every band meets the target. Both
  `SelectAsync` and `SelectKeyThreshold` choose this way; thresholds may come out higher, answering less but as often
  right as asked. Choose thresholds again after upgrading.

## 0.5.1

### Changed

- **An open document gives a field model its evidence in the order the values arrived.** `IFieldModel.SuggestAsync`
  receives, from a `FormSession`, the supporting values oldest first, a changed value moving to the end — so the evidence
  only grows at its end as the document fills, and a model that sends it to an inference server keeps the cached prefix
  of its previous request. Memory lookups keep the form's order (they match content, whatever order it was entered in),
  and a stateless `FormResolver.SuggestAsync` has no history, so its model still sees the form's order.

### Fixed

- **`Gil.IronHive`: a completion no longer fails when an alternative's log-probability is `null`.** llama.cpp sends a
  zero-probability alternative's logprob as `null` (JSON has no −∞); the OpenAI-compatible provider threw on it and the
  whole completion was lost. It now reads as zero probability. Requires `IronHive.Providers.OpenAI.Compatible` 0.45.1.

## 0.5.0

### Breaking

- **Values under a key answer only above `FieldDefinition.KeyThreshold`.** Before, a value settled alongside any known
  value led a field's suggestion, so a key that rarely decides the field — a choice among a handful of values that every
  document has — outranked a similar document, and the field's suggestions were mostly wrong. A key's strength is its
  weighted count for the value over the key's weighted total plus one: purity, discounted for few settlements. Without
  a `KeyThreshold` those values are now guesses. What to do: choose one with `ThresholdSelection.SelectKeyThreshold`,
  like `MemoryThreshold`; a null choice means the keys should not answer the field. Where neither a document memory
  nor a model is configured, candidates keep their order and only `Trusted`/`Answered` change.
- **`FieldCandidate` has a fifth parameter, `Trusted`** (default `true`). Code that deconstructs it positionally needs
  the extra element; `IFieldModel` implementations can keep constructing it with four.
- **A suggestion whose first candidate is a guess is traced as an abstention** (`Mode = "abstain"`, no output). Before,
  it was traced with the guessing layer's mode and value.

### Added

- **`FieldSuggestion.Answered`** — whether the first candidate is an answer rather than a guess.
- **Guesses after the answers**: the nearest similar document below `MemoryThreshold`, then values under a key below
  `KeyThreshold`, then the field's most frequent values. The nearest document was dropped before.
- **`ThresholdSelection.SelectKeyThreshold(fieldMemory, form, field, documents, targetPrecision, minimumAnswered)`**
  replays settled documents in the order they were settled and returns the lowest key strength that meets the target.
- **`FormResolver.SuggestAsync(form, documentId, values)`** suggests every open judged field from the values given and
  writes nothing to memory — for applications where saving is settling. The saved version of the same document is not
  evidence for itself. `FieldMemory.Rank` takes the document to leave out as `excluding`.

### Fixed

- The telemetry store no longer throws on a zero-probability alternative. A `TokenLogprob` whose `Logprob` is
  `-Infinity` is written as `"logprob": null` (JSON has no `-Infinity`; llama.cpp sends the same form) and read back
  as `double.NegativeInfinity`.

### Changed

- `Gil.IronHive` builds on IronHive 0.45.0.

## 0.4.0

### Added

- **`LexicalMemory.Nearest(task, state, count)`** lists the most similar remembered requests, most similar first
  (ties keep the order they were remembered in); the first is the match `LookupAsync` returns. Use it to offer several
  remembered answers as candidates rather than only the nearest one.
- **Form definitions** (`FormDefinition`, `FieldDefinition`, `FieldRole`, `FieldPolicy`) describe a form whose judged
  fields are suggested and settled one by one, with `SettledDocument`, `FieldCandidate`, `FieldSource` and
  `Settlement` for the values around them. A field can be kept out of every other field's evidence
  (`UseAsEvidence = false`) — for fields that identify a person, so that "this person, therefore this outcome" never
  hardens into memory. Declarations that contradict each other are refused when the form is defined.
- **`FieldMemory`** suggests a judged field's value from how often each value was settled alongside the values the
  document's other fields have, with no model and no call. A document contributes as a whole and putting it again
  replaces its contribution, so settling field by field and rebuilding from saved documents reach the same state in
  any order. Recent settlements weigh more (`recencyDecay`, 0.95 by default: a settlement counts 0.95 to the power of
  the number of later settlements under the same key), so a correction overtakes an older practice without first
  outnumbering it; ties go to the latest settlement. Pass `recencyDecay: 1` to count every settlement alike.
- **`SettledDocument.SettledAt`** — when a document was last settled. Where documents disagree the later settlement
  wins, so memory is the same whatever order documents arrive in, including a partial rebuild after live settling.
  Documents whose evidence for a field reads the same are one case, and a similar-document lookup answers with the
  case's latest settlement. In a session only accepting or correcting a field moves the time on; pass a reopened
  document's saved time to `Open` so that restoring it does not make its values new. `FormResolver` takes a
  `TimeProvider`.
- **`FormResolver` and `FormSession`** suggest a form's judged fields one document at a time. Open a document once,
  then pass values as they arrive: `ObserveAsync` for observed fields, `SettleAsync` with `Settlement.Accept`,
  `Correct`, `Reject`, `Revert` or `Restore` for judged ones. Each event returns fresh suggestions for the open fields
  whose evidence changed. A field is suggested from values settled alongside the known ones, then from a similar
  settled document (an optional `IMemory`, per field `MemoryThreshold`), then from its overall frequency; with none,
  a person decides. `Snapshot()` is the document to save and `RebuildAsync` puts saved documents back into memory;
  both replace a document's earlier contribution, so they can be combined freely. One trace per suggestion, under
  the task `form/field`.
- **`IFieldModel`** lets `FormResolver` ask a model for a field that neither memory had evidence for (never
  overriding one that did); its candidate goes ahead of the field's overall frequency and it alone reports a
  confidence. **`ResolverFieldModel`** puts the resolver behind it, one task per field. With the form resolver's sink
  and tasks named `form/field`, the resolution closes the suggestion's trace itself, so feedback reaches the tree.
- **`ThresholdSelection.SelectAsync`** chooses a field's `MemoryThreshold` by replaying settled documents in the order
  they were settled into an empty memory, one document per case as the form resolver keeps it: the lowest threshold whose answers reach a target precision, on at least a given number
  of answers, with its answer rate — or null when none does. The right threshold moves as memory grows, so choose it
  again as it grows rather than fixing it once.

## 0.3.0

### Added

- **`LexicalMemory`**, memory that needs no model: nearest neighbour over character n-gram TF-IDF (2- and
  3-grams by default), so it works in any script without a tokenizer and a lookup costs no call. Fill it from the
  feedback history with `MemoryReplay`. Its similarities are on their own scale, so choose the task's
  `MemoryThreshold` for it rather than reusing an embedding memory's. Weights are taken afresh as the memory grows by
  a tenth, so a similarity changes in steps rather than with every answer remembered.
- **`new Resolver(memory, sink)`**, a resolver without models: memory answers what it can and every other request
  abstains (`abstain`, empty path, null confidence, the miss in `Recall`) without a call. The task's tree,
  thresholds, contract and language are not used — a bare root will do. Memory learns from feedback only through a
  sink, as before.

## 0.2.0

### Breaking

- **The IronHive models are a package of their own, `Gil.IronHive`.** `IronHiveChatModel`, `IronHiveEmbeddingModel`
  and `OpenAICompatibleOptions` move there (namespace unchanged, `Gil.Llm`), and `Gil` no longer depends on IronHive.
  Add `dotnet add package Gil.IronHive`. A consumer that implements `IChatModel` and `IEmbeddingModel` itself needs
  only `Gil`.
- **Every task declares its prompt language.** `TaskDefinition` takes a fifth argument, `PromptLanguage`, with no
  default. Pass `PromptLanguage.English` or `PromptLanguage.Korean` — the wording 0.1.0 used, except that an answer
  without a description no longer gets an empty example in the tree contract — or change a piece of either with
  `with { ... }`:
  ```csharp
  new TaskDefinition("support", new TreeAnswerContract(tree), tree, policy, PromptLanguage.English)
  ```
- **Wording moved into `PromptLanguage`.** `JudgePromptTemplate`, `SingleTokenJudgeOptions.Prompt`,
  `FallbackPromptTemplate` and `FallbackGenerator`'s template argument are gone; set the same text on the language
  instead (`JudgeSystem`, `JudgeQuestion`, `NoneOfThese`, `FallbackSystem`, `FallbackPath`, `FallbackExamples`,
  `FallbackRetry`, …).
- **Contracts take the language.** `IOutputContract.Instruction()` and `Validate(text)` become
  `Instruction(PromptLanguage)` and `Validate(text, PromptLanguage)`. `TreeAnswerContract` no longer takes `None` or
  `Escape` (they are the language's `NoneOfThese` and `OutOfCategory`), and `TextContract` no longer takes `Language`
  (answers are in the task's language).
- **Narrowing returns a contract.** `IScopableContract.Scoped` returns `IOutputContract?`; `ScopedContract` is gone.
  A narrowed answer that escapes is `language.OutOfCategory`.
- **Components take the language per call**: just before `traceId` in `IJudge.JudgeAsync`,
  `GreedyTraverser.TraverseAsync`, `FallbackGenerator.GenerateAsync` and `SlotFiller.FillAsync`, and after `contract`
  in `Differentiation.Anchored`. Code that only uses `Resolver` needs no change beyond the task's language.
- **`OpenAICompatibleEmbeddingModel` is replaced by `IronHiveEmbeddingModel`**, the embedding counterpart of
  `IronHiveChatModel`: `IronHiveEmbeddingModel.OpenAICompatible(options)` takes the same `OpenAICompatibleOptions`,
  and `new IronHiveEmbeddingModel(generator, model, extraBody)` takes any IronHive `IEmbeddingGenerator`. Embedding
  requests follow the chat rule for `ExtraBody`: its fields are merged over the ones Gil sets (objects merge, other
  values replace), where 0.1.0 let `model` and `input` win.
- **`IMemory` separates the key from the trace**: `RememberAsync(task, key, state, answer, traceId)` and
  `Forget(task, key)`. `key` is the request the answer belongs to (what a lookup reports as the source); `traceId` is
  what the call's cost is recorded against. The resolver passes the request's trace id for both. An implementation
  that took `traceId` as the key should now use `key`.
- **Records gain positional members**, so constructing or deconstructing them by position changes:
  `Resolution` gains `Failure` (8th), and `TaskStats` gains `Misroutes` (9th). Both default to null.
- **`SqliteTelemetryStore.Stats` gains an optional `tree`** — source compatible, but code compiled against 0.1.0 must
  be rebuilt.
- **`DifferentiationKind` gains `Unserved`**: a `switch` over it needs the new case.
- **`PromotionReview` gains `Applied`**, the proposals the tree actually took; record those
  (`RecordPromotionRound`), not every proposal.

### Added

- `PromptLanguage` with `Korean` and `English` built in.
- `Gil.IronHive` package (see Breaking) and `EmbeddingGeneratorModel` in `Gil`: memory embeds through any
  Microsoft.Extensions.AI `IEmbeddingGenerator` (`Gil` now references `Microsoft.Extensions.AI.Abstractions`, which
  has no dependencies on .NET 10).
- Traces and metrics through `ActivitySource` and `Meter` named `GilDiagnostics.Name`: `gil.resolve` and `gil.call`
  spans, and `gil.resolutions`, `gil.resolution.energy`, `gil.resolution.duration` and `gil.call.energy`.
- `MemoryReplay` (`From`, `ApplyAsync`) and `ConfirmedAnswer`: the confirmed answers and overturned keys in a feedback
  history, applied to any `IMemory` — rebuild a memory store of your own from the log.
- Promotion rounds are recorded and read back: `IPromotionLog` (`RecordPromotionRound`, `PromotionRound`,
  `PromotionHistory`).
- Review decisions: `IReviewLog` (`RecordReview`, `Reviews`) with `ReviewKind`, `ReviewDecision` and `ReviewRecord`.
  `SqliteTelemetryStore` implements both logs.
- `Resolution.Failure`, `TraceOutcome.Failure` and a `failure` column on traces: why a request ended without output.
- `Stats(..., tree:)` counts misroutes (`TaskStats.Misroutes`, `MisrouteStats`): confirmed answers outside the
  category a request was sent to.
- `Differentiation.UnservedAsync` and `DifferentiationKind.Unserved`: similar requests the task keeps answering
  "none of these" to.
- README sections on when to use Gil, the tree file, memory rebuilds after a restart, bringing your own memory store,
  corrections in promotion, and traces and metrics.

### Changed

- The README and package descriptions lead with the parts whose effect was measured — confirmed-answer memory,
  one-token judgments and narrowed generation, with every call priced — and describe habit promotion as the
  experimental, off-by-default feature it already was. No behaviour change.
- IronHive 0.41.0: embeddings go through its OpenAI-compatible embedding generator, which reports the server's usage
  and model. The default transport is IronHive's connection-racing handler, so a `localhost` server that listens on
  IPv4 only is reached without first waiting out the IPv6 attempt.
- Trees are written without fields equal to their defaults, so a reviewed file diffs cleanly against a hand-written
  one. Files written by 0.1.0 still load.
- `EnergyModel.Fit` returns the best non-negative fit; a fit that was already non-negative is unchanged.
- A path step prints its probabilities.
- The tree contract gives no empty example for an answer without a description, in either language; in English it
  asks for a line of the list.

### Fixed

- A rate's 95% interval is exactly 0 or 1 at 0% or 100%, not off by rounding.
- Disposing a telemetry store no longer clears the connections of other stores in the same process.
- Opening a store written by an older version adds the columns it lacks.

## 0.1.0

First release: memory, the tree of one-token judgments, narrowed and full fallback, slot filling, the telemetry store
with priced calls, habit statistics, shadows, and promotion, deactivation and differentiation proposals.
