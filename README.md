# Gil

A habit runtime for LLM decisions. Repeated requests are answered from memory or from a small tree of cheap
single-token judgments; only what the tree cannot settle goes to full generation, narrowed to the category the
tree already confirmed. Answers that keep coming back can be promoted into habits, behind a flag and a review gate.

**Status: early.** The library is being built from a specification whose behaviour was measured first in a
research harness. Nothing is published yet.

## Layout

| Project | Contents |
|---|---|
| `Gil.Abstractions` | Records and ports: the decision tree, model calls, traversal steps, telemetry sink |
| `Gil` | The runtime. Currently: the SQLite telemetry store, the tree YAML reader/writer, an OpenAI-compatible chat transport with calibrated call pricing, the single-token judge, the greedy traverser, output contracts and the fallback generator |
| `Gil.Tests` | Unit tests and the compatibility fixture writer |

The telemetry file layout is a contract: analysis tools read it directly, so columns may be added but never
renamed or repurposed. `Writes_the_compatibility_fixture` produces a small synthetic store other
implementations can open to check they read the same layout (set `GIL_COMPAT_FIXTURE` to choose where).

## Build and test

Requires the .NET 10 SDK.

```
dotnet build Gil.slnx
dotnet test --solution Gil.slnx
```

## License

Apache-2.0
