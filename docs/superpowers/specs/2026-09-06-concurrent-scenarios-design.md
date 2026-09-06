# Concurrent scenarios and contended resources — Design

- **Date:** 2026-09-06
- **Status:** Built 2026-09-06 (plan: docs/superpowers/plans/2026-09-06-concurrent-scenarios.md).
- **Supersedes:** the "Tier 3 — deferred: type-level admission across scenarios" section of
  `2026-09-05-resource-conflict-detection-design.md`. Admission is now *declared* by the suite, not
  derived from lifecycle roles. Lifecycle roles (`[Created]`, `[Edited]`, `[Read]`, …), the
  per-scenario `ResourceLedger`, RAUN013 and lineage stay exactly what they are: trace, audit, and
  within-scenario defect detection. Nothing cross-scenario is derived from them.
- **Scope:** `src/Raun` (contended-resource surface, `ContentionGate`, definition model, run-event
  records, telemetry attributes), `src/Raun.Generator` (uses lowering, RAUN015), `src/Raun.Mtp`
  (launcher in `RaunRunLoop`, `--max-parallel-scenarios`, bus serialization, report model and
  template), `src/Raun.Aspire` (`MaxParallelScenarios` pass-through), the Aspire sample, README,
  AGENTS.md.
- **Non-goals:** Raun constructing, injecting, or scoping the resource token; slot assignment
  ("which of the three SMTP servers do I hold"); step-scoped holds; a global cap on steps across
  scenarios; any change to the lifecycle-role machinery.
- **Deviations recorded at review:**
  - `RunStarted` kept its `int ScenarioCount` (selected scenarios only) and gained an optional
    `Scenarios` list that includes preflight, so existing call sites kept compiling.
  - RAUN015 uses one message covering both the kind-count and capacity cases.

## Summary

Scenarios run concurrently by default, bounded to the processor count, with the degree adjustable in
code and on the command line. Scenarios are independent by contract: the author owns the lifetime
and isolation of the data a scenario touches, and Raun gives each scenario its own DI scope,
context, ledger, and trace so it can operate isolated.

Where scenarios genuinely contend for something in the system under test — one database that a
few scenarios must have to themselves, a pool of three SMTP servers, a serial port — the suite says
so. A **contended resource** is an empty token type carrying one of three kind attributes;
`[Uses<T>]` on a step, scenario, class, or assembly declares the need. The run loop admits a
scenario only when its whole set of uses is compatible with everything currently running, holds the
set for the scenario's whole duration including teardown, and releases it on completion. Waiting
time is recorded on the scenario span and in the report.

## Contended resources

### Declaring

```csharp
public interface IContendedResource;      // marker; enables the compile-time constraint on Uses<T>

[ExclusiveResource] public sealed class SerialPort : IContendedResource;   // one holder at a time
[SharedResource]    public sealed class Database   : IContendedResource;   // any number; Exclusive uses serialize
[PooledResource(3)] public sealed class Smtp       : IContendedResource;   // up to three holders
```

Three kinds because they are three different things — a mutex, a reader/writer lock, a counting
semaphore — and one attribute with a magic capacity (`0` = unbounded) hid that. A type implementing
`IContendedResource` must carry **exactly one** kind attribute (RAUN015; the gate throws the same
at runtime for definitions built without the generator). `PooledResource` requires a capacity ≥ 1.

The token is a name, not an object. Raun never constructs, injects, or scopes it. If the same type
is also a real service, the suite registers it in DI itself. (MS DI scopes do not nest — a scope
created inside a scope is a sibling under the root — so a "held" lifetime between run and scenario
would have to be owned by Raun; see Deferred.)

### Using

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly,
    AllowMultiple = true, Inherited = false)]
public sealed class UsesAttribute<T> : Attribute where T : IContendedResource
{
    public UsesAttribute() { }                        // Shared: one slot
    public UsesAttribute(LockMode mode) => Mode = mode;
    public LockMode Mode { get; } = LockMode.Shared;
}
```

Allowed on a DSL step method (the step knows what it touches), a `[Scenario]` method, a class
containing scenarios (walked outward through nesting), and the assembly. All sites are additive.
On the DSL side only the **method** is consulted; a `[Uses]` on a DSL container class is not
collected (class-level uses mean "the scenarios declared in this class").
`where T : IContendedResource` makes `[Uses<Patient>]` a compiler error, so there is no analyzer
rule for that. The existing `LockMode` enum (`Shared`, `Exclusive`) is reused as-is.

### Semantics

| | Shared use (default) | Exclusive use |
|---|---|---|
| `[ExclusiveResource]` | the one slot | the one slot |
| `[SharedResource]` | free, never waits | waits for zero holders, blocks new ones |
| `[PooledResource(N)]` | one of N | all N |

A scenario's use set is the union over all sites, reduced per type with `Exclusive` winning. The
whole set is acquired **atomically at scenario start** and released **after the scenario returns**,
i.e. after teardown and result publication. Nothing is ever acquired mid-scenario, so there is no
hold-and-wait and therefore no deadlock, no wound-wait, no re-execution — the same reasoning that
rejected lock-based C2. Scenario-wide holding is also what gives a scenario isolation *across its
steps*: create in step 1, assert in step 3, nobody clears the table in between.

### Runtime model

```csharp
public readonly record struct ContendedResourceUse(Type Resource, LockMode Mode);

// ScenarioDefinition gains:
public IReadOnlyList<ContendedResourceUse> Uses { get; init; } = [];
```

Kind and capacity are read from the token type's attributes by reflection, once per type, cached in
the gate. The generator emits only `(Type, Mode)` pairs.

### Generator

`ScenarioParser` collects `[Uses<T>]` from four places: every step's resolved DSL method symbol, the
scenario method, its containing types, and the compilation's assembly attributes. Steps inside a
not-taken `if` arm count: the set is static and conservative. The result is reduced per type and
stored on `ParsedScenario.Uses` as `(TypeFqn, Mode)`.

`ScenarioEmitter` adds
`Uses = new global::Raun.ContendedResourceUse[] { new(typeof(global::X), global::Raun.LockMode.Shared), … }`
to the definition initializer **only when the set is non-empty**, so the seven existing Verify
snapshots stay byte-identical.

### Analyzer

- **RAUN015 `ContendedResourceKind`** (Error). A type implementing `IContendedResource` carries zero
  or more than one of `[ExclusiveResource]`, `[SharedResource]`, `[PooledResource]`; or
  `[PooledResource]` has a capacity below 1. Reported on the type declaration. Message:
  `'{0}' implements IContendedResource and must carry exactly one of [ExclusiveResource], [SharedResource], [PooledResource]`
  (second message for the capacity case). Row added to `AnalyzerReleases.Unshipped.md`.

## Admission gate

`ContentionGate` — new, `src/Raun/Scheduling/`, public sealed, runner-neutral, synchronous, never
waits. Waiting is the run loop's job.

- State per resource type: kind (with capacity), `holders` (shared holders), `exclusive` (flag).
- `bool TryAcquire(IReadOnlyList<ContendedResourceUse> uses, out Type? refusedBy)`: under one lock,
  check every use, then apply every use — or apply none. A shared use is admissible when there is no
  exclusive holder and, for a pooled resource, `holders < capacity`. An exclusive use is admissible
  when `holders == 0` and there is no exclusive holder. On refusal `refusedBy` names the first
  refusing type (for wait accounting).
- `void Release(IReadOnlyList<ContendedResourceUse> uses)`.
- Reduces duplicate types in the input defensively (`Exclusive` wins). Throws
  `InvalidOperationException` on a type with zero or several kind attributes, or a pooled capacity
  below 1.

## Run loop

### Launcher

The `foreach` in `RaunRunLoop.RunAsync` becomes a launcher:

```
pending  = selected, in registration order
running  = { task → (definition, uses) }
gate     = new ContentionGate()
started  = false

while pending or running is non-empty:
    if not (started and cancellation requested):
        for each definition in pending, in order, while running.Count < degree:
            if gate.TryAcquire(definition.Uses):
                remove from pending; launch RunOneAsync; started = true
    if running is empty: break            // only reachable after cancellation
    finished = await Task.WhenAny(running.Keys)
    gate.Release(uses of finished); remove finished
publish RunFinished
```

Nothing admissible while slots are free means every pending scenario waits on a running holder,
which finishes in finite time; a finite run therefore has no starvation and a blocked scenario is
admitted at the latest when everything else has drained. Nothing running *and* nothing admissible
cannot happen: an empty gate admits anything.

Launch is a direct call to `RunOneAsync` with the task kept; no `Task.Run`. Step bodies are async by
contract, and `AsyncTaskMethodBuilder` restores the caller's `ExecutionContext` after the
synchronous prefix, so a scenario's `Activity.Current` and `ScenarioContext.Current` never leak into
the launcher or into a sibling. With degree 1 the launcher reproduces today's sequential loop
exactly.

- Ctor gains `int maxParallelScenarios = 0`. `0` means `Environment.ProcessorCount`; negative throws
  `ArgumentOutOfRangeException`.
- Degree counts **scenarios**. Steps inside a scenario stay unbounded, as today.

### Cancellation

The existing invariants are kept verbatim: the first selected scenario always launches (so an
up-front cancellation still reports its steps as skipped through the scheduler); after any launch,
an observed cancellation stops further launches; in-flight scenarios cancel through their own
linked `CancellationTokenSource`; running scenarios drain; `RunFinished` always publishes.

### Preflight

Unchanged. Preflight runs alone before the launcher. When it fails, every selected scenario is
skip-published sequentially as today; the gate is never consulted.

### Wait accounting

When a slot is free and the gate refuses a scenario, a stopwatch starts for it (once) and the
refusing type is remembered. On admission the elapsed time becomes `Waited` and the type
`WaitedFor`, carried on `ScenarioStarted` and the scenario span. A scenario that only ever waited
for a slot reports zero: that is the degree, not contention.

## Configuration

Code sets the suite default; the command line overrides per run.

- `RaunTestApplication.RunAsync(args, configure, simulateTime, services, preflight, int maxParallelScenarios = 0)`.
- `RaunTestFramework` gains a ctor overload with the degree; `OnExecuteAsync` reads the CLI override.
- `AspireRunOptions.MaxParallelScenarios { get; set; }` (default 0), passed through by `RaunAspire.RunAsync`.
- New `RunOptionsProvider : ICommandLineOptionsProvider` in `src/Raun.Mtp`, registered next to the
  HTML-report provider, exposing `--max-parallel-scenarios <n>` with `ArgumentArity.ExactlyOne`.
  Validation: an integer ≥ 0. `0` is the processor count, `1` is sequential.

## Event stream and sinks

- `RunEventBus.PublishAsync` serializes the fan-out through a `SemaphoreSlim(1, 1)`. Sinks remain
  single-threaded; failure isolation is unchanged. Order **within** a scenario is intact (the
  scheduler raises callbacks serially); events of different scenarios interleave, and every event
  already carries its definition. The THREADING comment is rewritten to state this.
- `RunStarted` carries the ordered selection: `RunStarted(IReadOnlyList<ScenarioDefinition> Scenarios)`
  with `ScenarioCount => Scenarios.Count`.
- `ScenarioStarted(ScenarioDefinition Definition, TimeSpan Waited = default, Type? WaitedFor = null)`.
- `MtpReportSink` is unchanged: it is keyed by definition and MTP's message bus is thread-safe.
- The preflight-failure skip path publishes as before.

## Report

- `HtmlReportModelBuilder` pre-creates one accumulator per scenario in `RunStarted` order, so the
  report lists scenarios in registration order whatever order admission produced. It still creates
  an accumulator on `ScenarioStarted` for a scenario it has not seen (sinks driven without a run
  start, as in the existing tests). The unused `_current` field is deleted.
- `ReportSummary.TotalMs` becomes the wall span: latest scenario end minus earliest scenario start
  over the scenarios that ran. The template already labels it "wall clock"; the builder was
  summing, which double-counts once scenarios overlap. The Verify snapshot updates.
- `ReportScenario` gains `Uses` (a list of `"Type:Mode"` strings), `WaitedMs`, and `WaitedFor` (the
  refusing type's name, for the header line). The template shows one line in the scenario header
  when `WaitedMs > 0`: `waited 1.2 s for Database`. Nothing else in the template changes.

## Tracing (amendment to `2026-09-06-tracing-design.md`)

New scenario-span attributes, added to `RaunTelemetry.Attributes`:

| Attribute | Value |
|---|---|
| `raun.scenario.uses` | `Database:Shared,Smtp:Exclusive` (attribute omitted when empty) |
| `raun.scenario.waited_ms` | only when > 0 |
| `raun.scenario.waited_for` | the refusing type's name, only with `waited_ms` |

Scenario spans stay roots linked to the run span; the existing isolation test is rerun at degree 3.

## Samples

- **AspireAppointments.** `[SharedResource] public sealed class Schedule : IContendedResource;`
  `[Uses<Schedule>]` on the `Scenarios` class; a new scenario "an admin clears the schedule" with
  `[Uses<Schedule>(LockMode.Exclusive)]` (Given the API is reachable, When an admin clears the
  schedule, Then the schedule is empty). The mock API gains an admin-only `DELETE /appointments`.
  This is the real hazard the feature exists for: clearing while another scenario books would break
  "the patient can read appointment". Expected totals move from 9 to 13 (three new step nodes plus
  the new scenario's teardown).
- **AppointmentTests.** Unchanged: simulated time, and its static `Outbox` has a single user.

## Docs

- README: a "Running scenarios in parallel" section — default degree, the knob, the three kinds,
  the use × kind table, and the isolation contract in one sentence.
- AGENTS.md "Things that look like bugs but are not": console/step output order across scenarios
  is nondeterministic; the HTML report order is registration order; a "waited … for …" line is
  contention, not slowness.
- `2026-09-05-resource-conflict-detection-design.md`: the Tier 3 section gets a one-line pointer to
  this document.
- `AnalyzerReleases.Unshipped.md`: RAUN015 row.

## Testing

Behavioral test first, then the smallest change, per AGENTS.md.

- **`ContentionGateTests`** (`test/Raun.Test`): every cell of the use × kind table; all-or-nothing
  on a multi-use set; release restores admissibility; refusing type reported; zero or two kind
  attributes throw; pooled capacity below 1 throws.
- **`RunLoopTests`** (`test/Raun.Mtp.Test`): max in flight equals the degree (six scenarios, degree
  3, bodies rendezvous on a countdown with a timeout, so "== 3" is proven, not sampled); degree 1 is
  sequential (the v1 test renamed); two exclusive users of one resource never overlap while an
  unrelated scenario overlaps both; shared users overlap and an exclusive user waits for them;
  pooled cap holds; admission prefers registration order; cancellation mid-run stops launching at
  degree 3; preflight failure at degree 3 skip-publishes everything; `Waited`/`WaitedFor` on the
  event and the span; trace isolation at degree 3.
- **Bus**: concurrent publishers are delivered one at a time to a sink that asserts non-reentrancy;
  a throwing sink is still isolated.
- **Builder**: interleaved events from two scenarios build the same model as sequential ones;
  order follows `RunStarted`; `TotalMs` is the wall span.
- **Generator**: uses lowered from all four sites and unioned; `Exclusive` wins; no uses ⇒ no
  `Uses` property emitted (snapshots unchanged); one new snapshot for a scenario with uses;
  RAUN015 for missing kind, two kinds, and capacity 0.
- **Framework**: `--max-parallel-scenarios` parsed; negative and non-integer rejected; CLI
  overrides the code default.
- **Samples**: AspireAppointments 13/13; AppointmentTests totals unchanged.

## Alternatives considered

- **Type-level admission derived from lifecycle roles** (the Tier 3 sketch). The generator sees only
  types; keys resolve at runtime. Every scenario that creates a `Patient` would serialize against
  every other, which is pure false positives for suites that use unique data per scenario — the
  normal way to write parallel integration tests — and the framework cannot tell those suites
  from ones that share `"Jane"`. Rejected: admission must be declared, and roles stay audit.
- **An explicit "acquire exclusive access to Database" step mid-scenario.** Hold-and-wait:
  scenario A holds Database at step 2 and wants Smtp at step 5 while B holds Smtp and wants
  Database. The fixes — a global lock order enforced by an analyzer rule, or a timeout — add a rule
  or turn a deadlock into a flaky failure, and a waiting scenario burns a parallelism slot while
  holding. What the idea was really after, visibility of the wait, is delivered by the span
  attributes and the report line.
- **Step-scoped holds** (acquire when a step is ready, release when it finishes). Deadlock-free
  too, since a running step never waits, but it drops scenario-wide isolation across steps, which
  is the point. Deferred, possibly as an opt-in scope later.
- **A resource-side mode plus a use-side mode.** Once capacity is the resource's property, a bare
  use is always "one slot" and a resource-side `LockMode` is redundant; on a capacity-1 resource a
  "shared" use is exclusive by arithmetic, so a rule forbidding it protects nothing.
- **One `[ContendedResource(Capacity = …)]` attribute with `0` = unbounded.** Correct but
  confusing even to its author; three kinds read as what they are.
- **Naming it `Fixture`.** Implies an object with a lifetime that Raun constructs and injects;
  it does not (yet). `Resource` alone clashes with `IResource<T>`, the traced-entity vocabulary.
  `ContendedResource` says what Raun does with it.
- **Parallel by default, unbounded.** Connection storms on real systems under test.
- **Sequential by default.** Safer, but inconsistent with "the author owns data isolation" and
  with every modern runner; the knob makes sequential one flag away.
- **`Task.Run` per scenario.** Not needed for async bodies; adds thread-pool hops and hides a
  synchronous step body instead of surfacing it.
- **A channel-and-pump bus instead of a semaphore.** Same ordering guarantee, more machinery.

## Deferred

- Slot assignment: `ctx.Slots<Smtp>()` returning the indices a scenario holds. Needs a hold object
  threaded run loop → scheduler → `ScenarioContext`, a `ScenarioScheduler.RunAsync` signature
  change, and a real suite that needs it. Until then a pool held in a DI singleton, taken in a Given
  step and returned in teardown, is enough — the gate guarantees it never runs dry.
- Raun-managed token lifetime: construct on first acquire, refcount shared holders, dispose on
  release. Requires Raun to own the instances (MS DI cannot nest scopes) and a way to expose them.
- A synthetic "waited for Database" node in the report Gantt.
- Step-scoped holds as an opt-in.
- A global cap on concurrently running steps across scenarios.
- Writer preference in admission: today a refused exclusive user can be overtaken by every later-
  registered shared user of the same resource and is admitted only when that stream drains
  ("registration order among admissible scenarios"). Skipping later same-resource users in the pass
  a scenario was refused in would preserve no-hold-and-wait and the finite-run guarantee. Open
  decision.
