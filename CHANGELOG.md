# Changelog

Versions follow `0.x`: a minor release may change the public API. Each such change is listed under **Breaking**
with what to do.

## Unreleased (0.2.0)

### Breaking

- **Every task declares its prompt language.** `TaskDefinition` takes a fifth argument, `PromptLanguage`, with no
  default. Pass `PromptLanguage.English` or `PromptLanguage.Korean` (the wording 0.1.0 used, unchanged), or change a
  piece of either with `with { ... }`:
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
- **Components take the language per call**, just before `traceId`: `IJudge.JudgeAsync`,
  `GreedyTraverser.TraverseAsync`, `FallbackGenerator.GenerateAsync`, `SlotFiller.FillAsync`, and
  `Differentiation.Anchored`. Code that only uses `Resolver` needs no change beyond the task's language.
- **`OpenAICompatibleEmbeddingModel` is replaced by `IronHiveEmbeddingModel`**, the embedding counterpart of
  `IronHiveChatModel`: `IronHiveEmbeddingModel.OpenAICompatible(options)` takes the same `OpenAICompatibleOptions`,
  and the constructor takes any IronHive `IEmbeddingGenerator`. Embedding requests follow the chat rule for
  `ExtraBody`: its fields are merged over the ones Gil sets (objects merge, other values replace), where 0.1.0 let
  `model` and `input` win.
- **`PromotionReview` gains `Applied`**, the proposals the tree actually took; record those
  (`RecordPromotionRound`), not every proposal.

### Added

- `PromptLanguage` with `Korean` and `English` built in.
- Promotion rounds are recorded and read back: `RecordPromotionRound`, `PromotionRound`, `PromotionHistory`.
- Review decisions: `RecordReview` and `Reviews` for promotion, deactivation and differentiation items.
- `Resolution.Failure` and a `failure` column on traces: why a request ended without output.
- `Stats(..., tree:)` counts misroutes: confirmed answers outside the category a request was sent to.
- `Differentiation.UnservedAsync`: similar requests the task keeps answering "none of these" to.
- README sections on when to use Gil, the tree file, memory rebuilds after a restart, and corrections in promotion.

### Changed

- IronHive 0.41.0: embeddings go through its OpenAI-compatible embedding generator, which reports the server's usage
  and model. The default transport is IronHive's connection-racing handler, so a `localhost` server that listens on
  IPv4 only is reached without first waiting out the IPv6 attempt.
- Trees are written without fields equal to their defaults, so a reviewed file diffs cleanly against a hand-written
  one. Files written by 0.1.0 still load.
- `EnergyModel.Fit` returns the best non-negative fit; a fit that was already non-negative is unchanged.
- A path step prints its probabilities.
- The English tree contract asks for a line of the list; a listed answer without a description has no empty example.

### Fixed

- Disposing a telemetry store no longer clears the connections of other stores in the same process.
- Opening a store written by an older version adds the columns it lacks.

## 0.1.0

First release: memory, the tree of one-token judgments, narrowed and full fallback, slot filling, the telemetry store
with priced calls, habit statistics, shadows, and promotion, deactivation and differentiation proposals.
