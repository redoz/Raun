# Raun Scenario Graph Extension — Implementation Plan

> **For agentic workers:** This plan is executed **inline in the authoring session** with TDD,
> keeping the build green and committing each logical unit via `jj`. Steps use checkbox
> (`- [ ]`) syntax for tracking. The executor is the same agent that holds the full design in
> context, so tasks lock down **public contracts, file structure, and spec coverage** rather
> than re-typing every implementation line.

**Goal:** Implement the xUnit v3 Scenario Graph extension from
`docs/scenario-graph-extension-design.md`: Given/When/Then scenario tests where each business
step is reported as an individual xUnit test, with sequential-by-default execution, explicit
fork/join parallelism, typed state flow, and dependency-aware skip-after-failure.

**Architecture:** Three layers.
1. **`Raun` (core, net10.0, no xUnit dep)** — phase markers, attributes, parallel awaiters,
   the runner-neutral scenario graph model, the per-step `ScenarioContext`, and the DAG
   scheduler. Independently testable.
2. **`Raun.Generator` (netstandard2.0 Roslyn incremental generator + analyzer)** — lowers
   `[Scenario]` method bodies into a graph definition (manifest + invoke delegates) and reports
   diagnostics for unsupported syntax.
3. **`Raun.Xunit` (net10.0)** — xUnit v3 discoverer, scenario test case, one visible test per
   step, and a self-executing test case that drives the core scheduler and reports per-step
   pass/fail/skip/timeout.

**Tech stack:** .NET 10 / C# 14 (static extension members), Roslyn 5.3 incremental generators,
xUnit v3 3.2.2 extensibility, Verify for snapshot tests.

**Validated up front:** C# 14 `extension(Given) { public static async Task<T> ... }` members,
tuple awaiter extensions, array awaiter extensions, and LINQ `.ToArray()` awaiters all compile
and run on SDK 10.0.300 / Roslyn 5.6 — the primary authoring API is confirmed viable.

**De-risking note:** The xUnit v3 self-executing test-case integration (Phase 4) is the
highest-risk layer (intricate extensibility API, hard to iterate against a live runner). Phases
1–3 are landed solid and green first so partial completion still leaves real, tested software.

---

## File structure

### `src/Raun` (core)
- `Phases.cs` *(exists)* — `Given` / `When` / `Then` markers.
- `Attributes/ScenarioAttribute.cs` — `[Scenario(name)]`, optional `TimeoutMs`.
- `Attributes/StepNameAttribute.cs` — `[StepName("template {param}")]`.
- `Awaiters/ScenarioAwaiters.cs` — tuple (arity 2–8) + array `GetAwaiter` extensions.
- `ScenarioContext.cs` — per-step context: cancellation, logging, attachments, services.
- `Model/StepStatus.cs` — `Pending|Running|Passed|Failed|Skipped` enum.
- `Model/ScenarioNode.cs` — one graph node: ids, phase, operation, display template,
  source location, timeout, `DependsOn` indices, `GroupId`, and the invoke delegate.
- `Model/ScenarioDefinition.cs` — scenario metadata + ordered `ScenarioNode` list; builds a
  display name and validates the DAG (no cycles, valid indices).
- `Model/StepResult.cs` — outcome of one node (status, duration, exception, skip reason,
  formatted display name, logs, attachments).
- `Scheduling/IStepInputs.cs` — `T Get<T>(int producerIndex)` handed to invoke delegates.
- `Scheduling/IStepObserver.cs` — callbacks the scheduler raises per step (starting / finished)
  so the xUnit layer can report without the core depending on xUnit.
- `Scheduling/ScenarioScheduler.cs` — the DAG executor: readiness, max-parallelism gate,
  fail→skip-dependents, cancellation, skip-reason synthesis, output storage.
- `Identity/StableId.cs` — deterministic hash for scenario/step ids (FQN + key, never line #).

### `src/Raun.Generator`
- `ScenarioGenerator.cs` — `IIncrementalGenerator` entry point.
- `Lowering/ScenarioParser.cs` — body → intermediate graph (steps, deps, groups).
- `Lowering/StepKind.cs` + `Lowering/ParsedStep.cs` — IR types.
- `Lowering/SymbolHelpers.cs` — DSL-call recognition, return-type unwrap, phase detection.
- `Emit/ScenarioEmitter.cs` — IR → C# source (ScenarioDefinition + invoke delegates).
- `Diagnostics/Descriptors.cs` — all `DiagnosticDescriptor`s (RAUN0xx).
- `Analysis/ScenarioAnalyzer.cs` — `DiagnosticAnalyzer` for the supported subset.

### `src/Raun.Xunit`
- `ScenarioDiscoverer.cs` — `IXunitTestCaseDiscoverer`.
- `ScenarioTestCase.cs` — `IXunitTestCase` + `ISelfExecutingXunitTestCase`.
- `ScenarioStepReporter.cs` — bridges `IStepObserver` → xUnit message bus.
- `ScenarioRegistry.cs` — maps `[Scenario]` method → generated `ScenarioDefinition`.

### Tests
- `test/Raun.Test` — awaiters, scheduler, model (xUnit-free logic).
- `test/Raun.Generator.Test` — Verify snapshots of generated output + diagnostics.
- `test/Raun.Xunit.Test` — acceptance tests through the real runner.
- `samples/AppointmentTests` *(new)* — end-to-end DSL sample.

---

## Public contracts (locked)

```csharp
namespace Raun;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ScenarioAttribute(string? displayName = null) : Attribute
{
    public string? DisplayName { get; } = displayName;
    public int TimeoutMs { get; init; }          // 0 = none
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class StepNameAttribute(string template) : Attribute
{
    public string Template { get; } = template;  // "patient {name} exists"
    public int TimeoutMs { get; init; }
}

public sealed class ScenarioContext
{
    public CancellationToken CancellationToken { get; }
    public string StepId { get; }
    public string StepDisplayName { get; }
    public IServiceProvider? Services { get; }
    public void Log(string message);
    public void AddAttachment(string name, string value);
    public IReadOnlyList<string> Logs { get; }
    public IReadOnlyDictionary<string, string> Attachments { get; }
}
```

```csharp
namespace Raun.Model;

public enum StepStatus { Pending, Running, Passed, Failed, Skipped }

public sealed class ScenarioNode
{
    public required int Index { get; init; }
    public required string StepId { get; init; }
    public required string Phase { get; init; }            // "Given"/"When"/"Then"
    public required string OperationName { get; init; }    // method name
    public required string DisplayNameTemplate { get; init; }
    public string? SourceFile { get; init; }
    public int SourceLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    public required IReadOnlyList<int> DependsOn { get; init; }
    public string? GroupId { get; init; }                  // tuple/array group label
    public required Func<IStepInputs, ScenarioContext, Task<object?>> Invoke { get; init; }
    public Func<IStepInputs, string>? FormatDisplayName { get; init; } // runtime arg formatting
}

public sealed class ScenarioDefinition
{
    public required string ScenarioId { get; init; }
    public required string DisplayName { get; init; }
    public required string MethodName { get; init; }       // FQN
    public string? SourceFile { get; init; }
    public int SourceLine { get; init; }
    public TimeSpan? Timeout { get; init; }
    public required IReadOnlyList<ScenarioNode> Nodes { get; init; }
    public void Validate();                                 // cycle + index checks
}

public sealed class StepResult
{
    public required ScenarioNode Node { get; init; }
    public required string DisplayName { get; init; }
    public required StepStatus Status { get; init; }
    public TimeSpan Duration { get; init; }
    public Exception? Exception { get; init; }
    public string? SkipReason { get; init; }
    public IReadOnlyList<string> Logs { get; init; }
    public IReadOnlyDictionary<string, string> Attachments { get; init; }
}
```

```csharp
namespace Raun.Scheduling;

public interface IStepInputs { T Get<T>(int producerIndex); }

public interface IStepObserver
{
    void OnStepStarting(ScenarioNode node, string displayName);
    void OnStepFinished(StepResult result);
}

public sealed class ScenarioScheduler
{
    public ScenarioScheduler(int maxParallelism = 0 /*0 = unbounded*/);
    public Task<IReadOnlyList<StepResult>> RunAsync(
        ScenarioDefinition definition,
        IServiceProvider? services = null,
        IStepObserver? observer = null,
        CancellationToken cancellationToken = default);
}
```

---

## Phase 1 — Raun core runtime (TDD, no xUnit)

### Task 1.1 — Attributes
- [ ] `ScenarioAttribute`, `StepNameAttribute` per contracts above. No test (pure data); covered
  indirectly by generator + scheduler tests.
- [ ] Commit.

### Task 1.2 — Parallel awaiters
- **Files:** `src/Raun/Awaiters/ScenarioAwaiters.cs`, `test/Raun.Test/AwaiterTests.cs`.
- [ ] Failing test: `await (Task.FromResult(1), Task.FromResult("a"))` yields `(1,"a")`; array
  `await new[]{ Task.FromResult(1), Task.FromResult(2) }` yields `int[]{1,2}`; both run
  concurrently (use two TCS + assert both observed before completion).
- [ ] Implement tuple `GetAwaiter` arity 2–8 + array `GetAwaiter` (via `Task.WhenAll`).
- [ ] Run → pass. Commit.

### Task 1.3 — ScenarioContext
- **Files:** `src/Raun/ScenarioContext.cs`, `test/Raun.Test/ScenarioContextTests.cs`.
- [ ] Failing test: `Log` accumulates; `AddAttachment` stores; cancellation token surfaces;
  `StepId`/`StepDisplayName` reflect ctor args.
- [ ] Implement (thread-safe log/attachment collections). Run → pass. Commit.

### Task 1.4 — Model: nodes/definition/result + StableId
- **Files:** `Model/*.cs`, `Identity/StableId.cs`, `test/Raun.Test/ModelTests.cs`.
- [ ] Failing test: `ScenarioDefinition.Validate()` throws on cycle and on out-of-range
  `DependsOn`; passes on a valid linear graph. `StableId.For("Ns.M","step:0")` is deterministic
  and independent of any line number.
- [ ] Implement model + FNV-1a/SHA-based stable id (hex). Run → pass. Commit.

### Task 1.5 — DAG scheduler (the core)
- **Files:** `Scheduling/ScenarioScheduler.cs` (+ `IStepInputs`, `IStepObserver`),
  `test/Raun.Test/SchedulerTests.cs`.
- [ ] Failing tests (one behavior each):
  - **Source-order sequencing:** three nodes chained by `DependsOn` run in written order
    (record start order).
  - **Parallel ready nodes:** two nodes sharing a dependency, no inter-dep, both start before
    either finishes (TCS gate); join node waits for both.
  - **Max parallelism:** with `maxParallelism: 1`, two "parallel" nodes do not overlap.
  - **Dataflow:** consumer reads producer output via `IStepInputs.Get<T>(idx)`.
  - **Failure → skip dependents:** node 1 throws; its transitive dependents are `Skipped` with
    reason `dependency failed: <op>`; independent ready branch still `Passed`.
  - **Multiple dependency failures summarized** in the skip reason.
  - **Cancellation:** cancelling the token transitions not-yet-started nodes to `Skipped`/
    canceled and stops scheduling.
  - **Observer:** `OnStepStarting`/`OnStepFinished` fire once per node with correct status.
  - **Per-step timeout:** a node exceeding its `Timeout` is `Failed` with a `TimeoutException`.
- [ ] Implement scheduler: topological readiness, `SemaphoreSlim` parallelism gate, output bag
  (`ConcurrentDictionary<int, object?>`), per-step `ScenarioContext`, failure propagation by
  walking dependents, skip-reason synthesis, linked CTS for timeout/cancel.
- [ ] Run → all pass. Commit.

---

## Phase 2 — Source generator (TDD with Verify)

### Task 2.1 — Generator skeleton + registry
- **Files:** `ScenarioGenerator.cs`, `Emit/ScenarioEmitter.cs`, `Raun/ScenarioRegistry`
  (core side: a registration hook), `test/Raun.Generator.Test/GeneratorTestBase.cs`.
- [ ] Verify test harness: compile input source + `Raun` refs through `CSharpGeneratorDriver`,
  snapshot generated trees. (Use `Verify.SourceGenerators`.)
- [ ] Generator finds `[Scenario]` methods via `ForAttributeWithMetadataName` and emits a stub
  registration file. Snapshot. Commit.

### Task 2.2 — Linear lowering
- **Files:** `Lowering/ScenarioParser.cs`, `Lowering/ParsedStep.cs`, `Lowering/SymbolHelpers.cs`,
  `Emit/ScenarioEmitter.cs`.
- [ ] Verify test: the booking linear scenario → a `ScenarioDefinition` with 4 nodes, correct
  `DependsOn` (source-order + dataflow), invoke delegates that read prior outputs and call the
  DSL, and a registration that maps the method to the definition. Snapshot the generated source.
- [ ] Implement: walk `await`ed expression statements / local declarations; recognize DSL
  invocations on `Given/When/Then`; map assigned local → producing node index; resolve args to
  prior outputs or constants; emit nodes. Source-order edge = previous top-level step. Commit.

### Task 2.3 — Tuple parallel groups
- [ ] Verify test: tuple booking scenario → two sibling nodes sharing the predecessor, same
  `GroupId`, deconstructed locals mapped to each node's output, join on next step. Snapshot.
- [ ] Implement tuple deconstruction lowering. Commit.

### Task 2.4 — Explicit array groups
- [ ] Verify test: `await new[]{ Given.UserExists("alice"), Given.UserExists("bob") }` → two
  sibling nodes; consumer rebuilds the array from both outputs. Snapshot.
- [ ] Implement array-initializer lowering + array reconstruction in the consumer invoke. Commit.

### Task 2.5 — LINQ `.ToArray()` groups (statically sized)
- [ ] Verify test: `Enumerable.Range(1,10).Select(i => Given.UserExists($"user-{i}")).ToArray()`
  → 10 sibling nodes (unrolled by the constant range), each invoke binding its constant `i`.
  Snapshot.
- [ ] Implement constant-range unrolling; non-constant shapes fall through to an analyzer
  diagnostic (Task 3.x). Commit.

### Task 2.6 — Display-name formatting
- [ ] Verify test: `[StepName("patient {name} exists")]` with a literal arg → name formatted at
  generation; with a runtime arg → `FormatDisplayName` delegate emitted. Snapshot.
- [ ] Implement placeholder binding to parameter names. Commit.

---

## Phase 3 — Analyzer diagnostics (TDD with Verify)

**Descriptors (`Diagnostics/Descriptors.cs`):**
- `RAUN001` `[Scenario]` method must be `async Task`/`async ValueTask`.
- `RAUN002` Step statement must be an awaited DSL call / tuple / array of DSL calls.
- `RAUN003` Unsupported control flow in scenario body (v1).
- `RAUN004` DSL call must resolve to a static extension member on `Given`/`When`/`Then`.
- `RAUN005` DSL method must return `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`.
- `RAUN006` Tuple/array group must contain only lowerable DSL calls.
- `RAUN007` DSL argument must be a prior step output or an allowed constant/parameter.
- `RAUN008` Display-name placeholder must bind to a parameter (no unsafe object dumping).

### Tasks 3.1–3.8 — one diagnostic per task
- [ ] For each: Verify test asserting the diagnostic id + location on offending input, and that
  valid input produces none; then implement the rule in `ScenarioAnalyzer`. Commit each.

---

## Phase 4 — xUnit v3 adapter (highest risk)

> Built against the API confirmed by the background research doc. If a seam can't be nailed
> blind, implement the runtime-agnostic parts fully and document the gap — never fake a green
> acceptance test.

### Task 4.1 — Discovery
- **Files:** `ScenarioDiscoverer.cs`, `ScenarioAttribute` gains the xUnit discoverer wiring.
- [ ] Acceptance test: a project with one `[Scenario]` yields exactly one scenario test case.
  Implement `IXunitTestCaseDiscoverer` returning one `ScenarioTestCase`. Commit.

### Task 4.2 — Self-executing test case + per-step tests
- **Files:** `ScenarioTestCase.cs`, `ScenarioStepReporter.cs`.
- [ ] Acceptance test: each step shows as its own visible test; pass/fail/skip statuses match a
  scenario with a deliberate failing middle step (dependent skipped, independent passes).
- [ ] Implement `ISelfExecutingXunitTestCase` driving `ScenarioScheduler`, with
  `ScenarioStepReporter : IStepObserver` queueing `TestStarting`/`TestPassed`/`TestFailed`/
  `TestSkipped`/`TestFinished` per step. Serialize the test case (`IXunitSerializable`). Commit.

### Task 4.3 — Timeout / cancellation reporting
- [ ] Acceptance test: a step exceeding its timeout reports as failed/timed-out; canceling the
  run reports remaining steps appropriately. Commit.

---

## Phase 5 — End-to-end sample

### Task 5.1 — AppointmentDsl sample
- **Files:** `samples/AppointmentTests/*` (AppointmentDsl + Patient/Slot/Appointment + scenarios:
  linear, tuple-parallel, array-import). Reference `Raun.Xunit` + generator.
- [ ] `dotnet test` on the sample: scenarios discovered, steps reported individually, parallel
  groups run concurrently and the following step waits for all. Commit.
- [ ] Add `samples/` to the solution.

---

## Spec coverage check

| Spec section | Covered by |
| --- | --- |
| Goal / primary authoring API | Validated experiment; Phases 1–5 |
| Two packages | `Raun`+`Raun.Generator` / `Raun.Xunit` |
| xUnit v3 seams (discoverer, test case, self-executing) | Phase 4 |
| Phase types + domain DSL | `Phases.cs`; sample DSL Phase 5 |
| Source generation model (nodes, outputs, deps, groups) | Phase 2 |
| Sequential default + tuple/array/LINQ parallel | Tasks 1.2, 2.2–2.5 |
| Runtime awaiters | Task 1.2 |
| Execution semantics (DAG, max-parallel, fail→skip, ctx, cancel) | Task 1.5 |
| Analyzer rules | Phase 3 |
| Reporting & errors (stable ids, names, skip reasons, timeouts) | Tasks 1.4, 1.5, 2.6, 4.x |
| Testing strategy (Verify gen/diag, scheduler tests, acceptance) | Tests across phases |
| End-to-end sample | Phase 5 |
| Rejected baseline / open design space | N/A (design notes) |
