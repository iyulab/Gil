# Changelog

Versions follow `0.x`: a minor release may change the public API. Each such change is listed under **Breaking**
with what to do.

## Unreleased (0.2.0)

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
