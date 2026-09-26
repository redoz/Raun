# Roadmap: Aspire example, HTML report, and a resourcing system

- **Date:** 2026-06-06
- **Status:** Roadmap / ideas — **not** an executable plan yet. Each feature below gets its own
  `brainstorming` → spec (`docs/superpowers/specs/`) → plan (`docs/superpowers/plans/`) when it's
  actually picked up. This doc is a shared sketch + open-questions list so we don't lose the intent.
- **Scope (future):** `samples/`, `src/Raun` (scheduler/context/model), `src/Raun.Mtp` (reporter),
  `src/Raun.Generator` (attribute lowering).

## Why these three belong together

They reinforce each other, so designing them with awareness of each other pays off:

- The **resourcing system** (C) emits a structured event stream ("User `abc@foo` *created* by step 2")
  that is exactly the material the **HTML report** (B) wants for a resource timeline and for
  debugging ("which step deleted user 123?").
- The **Aspire example** (A) is the natural showcase for both: Aspire resources (a Postgres
  container, a queue, the AppHost itself) map cleanly onto Raun resources, and a distributed run is
  where a beautiful timing/artifact report earns its keep.

What we already have to build on (so none of this starts from zero):

- `ScenarioScheduler` (`src/Raun/Scheduling/ScenarioScheduler.cs`) runs each scenario as a bounded-
  parallel DAG and is **runner-neutral**, surfacing progress through `IStepObserver`
  (`OnStepStartingAsync`/`OnStepFinishedAsync`). It already threads an `IServiceProvider? services`
  down to each step.
- `ScenarioContext` (`src/Raun/ScenarioContext.cs`) already collects per-step `Log(...)` lines and
  `AddAttachment(name, value)` artifacts, and exposes `Services`.
- `StepResult` (`src/Raun/Model/StepResult.cs`) already carries `Duration`, `Logs`, `Attachments`,
  `Status`, `Exception`, and `SkipReason`.
- `RaunStepReporter` / `RaunDiscoverer` (`src/Raun.Mtp`) already bridge step nodes to
  Microsoft.Testing.Platform.

The upshot: **B is mostly a new consumer of data that already exists**, and **C plugs into the same
`IStepObserver` + `ScenarioContext` + `IServiceProvider` plumbing** — no new core execution model.

---

## A. Aspire example + code coverage across the AppHost's child processes

**Intent.** Ship a `samples/` project that drives a real .NET Aspire app (an AppHost orchestrating a
couple of services) with Raun scenarios, and — the hard requirement — collect code coverage that
includes the **service processes the AppHost launches**, not just the test process.

**Sketch.**
- New sample, e.g. `samples/AspireAppointments/` with `AspireAppointments.AppHost`, one or two service
  projects, and `AspireAppointments.Tests` hosting scenarios via
  `Aspire.Hosting.Testing.DistributedApplicationTestingBuilder` (the standard way to stand up an
  AppHost in-test). The Raun DSL phases (`Given`/`When`/`Then`, or custom phases now that `IPhase` is
  pluggable) wrap "resource X is healthy", "call endpoint", "assert projection".
- Coverage: Raun is already an MTP framework, so the `--coverage` MTP extension
  (`Microsoft.Testing.Extensions.CodeCoverage`) covers the **test/host** process out of the box. The
  open problem is the **child processes** Aspire spawns.

**Key challenges / open questions.**
- Cross-process coverage. Aspire launches services as separate processes; their coverage needs the
  coverage profiler attached to each child. Candidate approaches to evaluate:
  (a) `dotnet-coverage collect --server-mode` + propagating the profiler env vars
  (`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`, `MicrosoftInstrumentationEngine_*`)
  to each Aspire resource via `WithEnvironment(...)`, then merging the per-process outputs;
  (b) Coverlet collector — generally weaker for multi-process;
  (c) a thin Raun helper that, when a coverage session is active, injects those env vars into every
  Aspire child resource automatically.
- Merge + report format (`.cobertura`/`.coverage`) and how it surfaces in CI.
- Does this need anything in `src/Raun.Mtp`, or is it purely sample + MSBuild/runsettings wiring?
  (Prefer the latter; only promote to the framework if every Aspire user would re-implement it.)

**Rough effort.** Medium. Mostly an integration/investigation spike (the coverage-across-children
question is the real risk) plus a sample. Decouple from B/C.

---

## B. Beautiful self-contained HTML report (timings, artifacts, logs)

**Intent.** After a run, emit a single, shareable HTML file: a per-scenario timeline (Gantt-style,
showing the DAG's parallelism and each step's `Duration`), drill-down into each step's logs and
attachments, failures with exception/skip reasons, and (once C lands) a resource timeline.

**Sketch.**
- A new MTP extension in `src/Raun.Mtp` (sibling to `RaunStepReporter`) that subscribes to the run's
  step results and writes `TestResults/raun-report.html`. It can consume the same `StepResult` data
  the reporter already produces — `Duration`, `Logs`, `Attachments`, `Status`, `Exception`,
  `SkipReason` — plus node identity (`Phase`, numbering, `GroupId`, `DependsOn`) for the timeline.
- Self-contained: embed a JSON blob + a small vanilla-JS/CSS renderer in one file, zero runtime deps,
  so it opens from disk and is easy to attach to CI artifacts. Gate behind a flag
  (e.g. `--report-html` / an MSBuild property) so it's opt-in.
- The DAG structure (`ScenarioNode.DependsOn`, `GroupId`, the step numbering we already emit) gives a
  faithful parallelism view — fork/join groups render as concurrent lanes.

**Key challenges / open questions.**
- Capturing absolute start/stop timestamps. Today the scheduler records `Duration` but not wall-clock
  start; a timeline needs both. Small addition to `StepResult` (e.g. `StartedAt`) or to the observer
  callbacks. (Note: scripts/core avoid ambient `DateTime.Now` in some places — decide where the clock
  lives, likely the scheduler injecting a time source.)
- Artifacts are currently `string`-valued (`AddAttachment(name, value)`). Do we extend to file-path /
  binary artifacts (screenshots, coverage, dumps) for the report? Probably yes — coordinate the
  attachment model with A (coverage files) and C (resource snapshots).
- Streaming vs end-of-run: write once at session end (simpler) vs incremental (resilient to crashes).
- Relationship to MTP's built-in `--report-trx`: HTML is complementary, human-facing.

**Rough effort.** Medium. The data is largely present; the work is the timestamp/artifact model and a
polished renderer. Can start independently; reserve a "resources" section for when C lands.

---

## C. Resourcing system (effects, tracing, resource-based scheduling/locks)

**Intent.** Let steps declare that they **create / reference / edit / delete** named resources.
`Given.ExistingUser("abc@foo")` would emit a `User abc@foo` *created* (or *referenced*) effect. Those
effects power two things: (1) richer tracing/debugging (and a resource lane in the report), and (2)
resource-aware scheduling — at minimum locks: "this scenario needs **exclusive** access to user 123",
or to "the system", or to "the reporting api"; others can share **read** access.

**Sketch.**
- **Resource identity:** `(Type, Key)`, e.g. `("User","abc@foo")`, `("Api","reporting")`, and a
  singleton `("System","")`. Keys may be constants or runtime values (a prior step's output).
- **Access intent → lock mode:** `Reference`/read = **shared**; `Create`/`Edit`/`Delete` = **exclusive**
  (write). A keyed reader-writer lock per resource gives the scheduling behaviour for free.
- **Two declaration channels** (deliberately both, because they serve different masters):
  - *Declarative (static, for scheduling).* An attribute on the DSL method — e.g.
    `[Resource("User", Access.Create)]` — read by the generator (`AttributeReader`, like `[StepName]`)
    and lowered onto `ScenarioNode`. Known **before** execution, so the scheduler can plan/lock.
  - *Dynamic (runtime, for tracing).* A `ScenarioContext` API — e.g. `ctx.Resources.Created("User",
    user.Email, data)` / `.Referenced(...)` / `.Edited(...)` / `.Deleted(...)` — emitting effect
    events with **resolved** runtime values for the trace and report.
- **Lock manager:** an `IResourceLockManager` living at **session scope** (so it spans scenarios that
  run in parallel), injected via the existing `IServiceProvider`. The scheduler acquires a ready
  node's declared locks just before `Invoke` and releases them after the step reaches a terminal
  status — a natural extension point in `RunNodeAsync`/the launch loop, observed through the existing
  `IStepObserver` lifecycle.
- **Tracing/report:** every effect event (with step id, timestamp, resolved key, optional snapshot)
  becomes a resource lifeline in B — created → edited → deleted across steps — turning "who deleted
  user 123?" into a glance.

**Key challenges / open questions.**
- **Static vs dynamic keys for scheduling.** Exclusivity and resource *type* are known statically, but
  a concrete key may only exist at runtime. Options: resolve keys bindable from scenario
  inputs/constants ahead of time; otherwise acquire locks **just-in-time** when the node becomes ready
  (the scheduler already gates launch on dependencies — locks are one more gate). Coarse fallback:
  lock at the type level when the key is unknown.
- **Deadlock avoidance.** A node holding multiple locks must acquire them atomically in a canonical
  order, or use wait-die / try-acquire-with-backoff. Define this before building.
- **Scope & today's model.** Each scenario currently runs its own `ScenarioScheduler`. Cross-scenario
  locks (e.g. exclusive "the system") require the lock manager to be **shared across all running
  scenarios** — i.e. owned at the MTP session level (`RaunTestApplication`), not per scenario.
- **Interaction with the existing DAG.** Resource locks are an *orthogonal* constraint layered on the
  dependency edges; make sure a lock wait can't masquerade as a dependency stall (the scheduler throws
  on an unexplained stall today).
- **Ergonomics.** How opt-in is the API? Is `Reference` of a never-created resource a warning/error?
  Do we want resource *pools* ("any free worker") in addition to named resources?

**Rough effort.** Large; highest design risk. Recommend splitting:
- **C1 — Effects & tracing (no scheduling):** the `ScenarioContext.Resources` API + event capture +
  surfacing in the report. Low risk, immediately useful, unblocks B's resource lane.
- **C2 — Resource-based locking:** the declarative attribute, session-scoped `IResourceLockManager`,
  and scheduler integration. Build on C1 once the locking semantics are nailed down.

---

## Suggested sequencing

1. **B (HTML report)** and **A (Aspire + coverage)** are largely independent and can proceed in
   parallel; B is low-risk because the data mostly exists.
2. **C1 (resource effects + tracing)** next — small, and it enriches B with a resource timeline.
3. **C2 (resource locks + scheduling)** last — the deepest design; do a focused `brainstorming`
   session on lock semantics, key resolution, and deadlock avoidance before any code.

Each item: `brainstorming` → spec under `docs/superpowers/specs/` → TDD plan under
`docs/superpowers/plans/` → execute. This doc is just the map.
