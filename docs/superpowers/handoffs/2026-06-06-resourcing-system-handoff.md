# Handoff — Raun scenario resources (feature C)

**Date:** 2026-06-06
**Version control:** `jj` (Jujutsu), not git. Repo root `C:\dev\raun`. Working copy is **clean**;
everything below is committed on top of `main`. `jj log` shows the spec/roadmap commits at the tip.
**Status:** Feature C is **brainstormed and spec'd — spec committed and approved.** The implementation
**plan has NOT been written yet.** Next session starts at `superpowers:writing-plans`.

---

## You are picking up

Building **scenario resources** for Raun: scenario steps declare that they create/load/read/edit/
delete symbolic resources, which drive (1) real cross-scenario locking and (2) tracing. This is
**feature C** of `docs/superpowers/plans/2026-06-06-roadmap-aspire-report-resources.md`.

**Read first (committed):** `docs/superpowers/specs/2026-06-06-resourcing-system-design.md` — the full
design. It is the source of truth; the summary below is just orientation so you don't re-derive it.

**Your task:** Use `superpowers:writing-plans` to turn the spec into a **TDD implementation plan for
phase C1 only** (see Phasing), saved to
`docs/superpowers/plans/2026-06-06-resourcing-c1-effects-tracing.md`. Then execute it
(`superpowers:executing-plans` or `superpowers:subagent-driven-development`). Plan C2 separately later.

---

## The locked design (so you don't replay the brainstorm)

- **Symbolic resources, real locks.** A resource is a `(type, key)` identity + arbitrary `data`. The
  framework uses it ONLY for coordination + tracing — it materializes/mutates/rolls back **nothing**
  real. A wounded scenario just re-runs; the author owns step idempotency.
- **Scenario = the transaction & retry unit.** Within a scenario the existing DAG already serializes;
  locking/deadlocks are purely a **cross-scenario** concern.
- **Scheduling = two-phase locking + wound-wait** (older scenario wounds younger holder → deterministic,
  cycle-free, no detector). Wound = cancel via the scheduler's existing per-step `CancellationTokenSource`,
  release locks, re-run from the top. Retry cap (default ~3) then fail with a diagnostic.
- **Imperative substrate is primary; attributes are sugar** the generator lowers to it.
  `ctx.Resources.Create/Load/Read/Edit/Delete(...)`, all async.
- **Typed identity** — the domain record IS the identity. Either hand-written CRTP
  `record User(...) : IResource<User> { static ResourceKey KeyFor(User u) => u.Email; }` (static
  abstract member) OR plain record + `[Resource(Key = nameof(Email))]` and the generator emits the
  `IResource<User>` half. **Key resolver chain** (first match wins): `[ResourceKey]` → registered
  selector `Resources.Identify<User>(u => u.Email)` → `IResourceIdentity` (runtime keys) → value equality.
- **EXPLICIT ROLES, NO DEFAULTS.** A resource-typed parameter/return with no role attribute is a build
  error → new diagnostic **RAUN009**. Menu: return `[Creates]`(excl,new) / `[Loads]`(shared,existing) /
  `[Edits]`(excl); parameter `[Reads]`(shared) / `[Edits]`(excl) / `[Deletes]`(excl). Mode map:
  Read/Load = shared; Create/Edit/Delete = exclusive. Claims **dedup by identity** (exclusive > shared;
  lifecycle precedence Delete > Edit > Create > Load > Read). Method-level `[Creates]` etc. is an
  explicit shorthand for a single-resource step's return — NOT a default.
- **Coarse/ambient resources** — singleton marker type `TheSystem : ISingletonResource<TheSystem>` +
  generic attribute `[Requires<TheSystem>(Access.Exclusive)]`, AND a bare-string form
  `ctx.Resources.Exclusive("System")` / `Shared("reporting-api")`.
- **Release via `IAsyncDisposable` / `await using`.** Framework owns one scenario-scoped scope
  (`BeginScenarioScope`); `DisposeAsync` is the single release path for success/failure/wound. Lifecycle
  verbs auto-park their token in that scope; `LockAsync(...)` hands the token back for an explicit
  **narrower** `await using` (opt-in early release, trades serializability).
- **Lock manager is session-scoped** (shared across concurrent scenarios), injected via the scheduler's
  existing `IServiceProvider`. Per-identity async reader/writer gate (queue of `TaskCompletionSource`;
  **build our own**, BCL has none). Never block a thread (steps already run as `Task`s).
- **Static fast path + dynamic fallback.** Compile-time-known keys → pre-acquire at scenario start in a
  global canonical order (deadlock-free). Runtime keys → acquire on demand, wound-wait covers them.

## Phasing — do C1 first

- **C1 — effects & tracing (NO locking).** `ctx.Resources` verbs + identity resolver + `ResourceEffect`
  stream through the existing `IStepObserver`/`StepResult` channel + the `[Creates]/[Loads]/[Reads]/
  [Edits]/[Deletes]` attributes + **RAUN009** analyzer rule + generator lowering/catalog. NO scheduler
  or session changes. Low-risk, exercises the whole authoring surface, unblocks the HTML report's
  resource lane. **This is what you plan and build first.**
- **C2 — real locking & scheduling.** `IResourceLockManager` + async RW gate + `ScenarioLockScope` +
  scheduler/session integration + wound-wait + static fast path. **Carries the biggest open question
  (#1 in the spec):** today each scenario runs its own `ScenarioScheduler`; real cross-scenario locks
  need a session-level coordinator that runs scenarios concurrently against a shared lock manager. Plan
  this only after C1 lands.

---

## Conventions & gotchas (carry these forward)

- **jj, no trailers.** `jj commit -m "<msg>"` finalizes the working copy and starts a fresh one; no
  `git add`, no `Co-Authored-By`/tooling trailers. **Green commits, TDD** — failing test first, watch it
  fail, minimal impl, watch it pass, commit.
- **.NET 10, C# `LangVersion=preview`.** Build whole repo: `dotnet build` from `C:\dev\raun`.
- **Tests are xUnit v3 on Microsoft.Testing.Platform.** Full project run (always works):
  `dotnet test test\<Project>\<Project>.csproj`. To NARROW, use **MTP** filter syntax:
  `dotnet test <proj> -- --filter-class "Raun.Generator.Test.Foo"` or `--filter-method "*Bar*"`.
  **Do NOT** use VSTest `--filter "FullyQualifiedName~X"` — MTP rejects it (dumps help, "Zero tests ran").
- **Warnings are errors repo-wide** (`Directory.Build.props`: `TreatWarningsAsErrors`,
  `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`). Notes:
  - An **empty** marker interface trips **CA1040** → scope a `#pragma warning disable CA1040` around it.
    Precedent: `src/Raun/Phases.cs` (`IPhase`). `ISingletonResource<TSelf>` will need this; `IResource<TSelf>`
    won't (it has `KeyFor`).
  - Catch-all `catch (Exception)` is fine — CA1031 is disabled in `.editorconfig`.
- **New diagnostic RAUN009** must be added in THREE places or RS2008 fails the build:
  `src/Raun.Generator/Analysis/Descriptors.cs`, `ScenarioAnalyzer.SupportedDiagnostics`, and a row in
  `src/Raun.Generator/AnalyzerReleases.Unshipped.md`.
- **Generator snapshot tests:** if emitted output changes, the `*.verified.cs` snapshots under
  `test/Raun.Generator.Test/Snapshots/` go stale. DiffEngine is disabled, so Verify writes
  `*.received.cs`; confirm the diff is only what you expect, then overwrite `.verified.cs` with it.

## Key files to know

- `src/Raun/ScenarioContext.cs` — has `Log`/`AddAttachment`/`Services`; add a `Resources` facade here.
- `src/Raun/Scheduling/ScenarioScheduler.cs` — DAG runner; per-step `CancellationTokenSource`,
  `IStepObserver`, `IServiceProvider` (the C2 seams).
- `src/Raun/Scheduling/IStepObserver.cs`, `StepContext.cs`; `src/Raun/Model/{ScenarioNode,StepResult,
  ScenarioDefinition}.cs` (StepResult already carries Duration/Logs/Attachments).
- Generator pipeline: `src/Raun.Generator/Lowering/{AttributeReader,Ir,ScenarioParser,SymbolHelpers}.cs`,
  `Emit/ScenarioEmitter.cs`, `ScenarioGenerator.cs`, `Analysis/{Descriptors,ScenarioAnalyzer}.cs`.
- Behavioral generator test harness: `test/Raun.Generator.Test/GeneratorHarness.cs`
  (`Run(src).AssertCompiles()`, `.Definitions()`, `.AnalyzeAsync(src)`).
- MTP/session: `src/Raun.Mtp/{RaunTestApplication,RaunDiscoverer,RaunStepReporter}.cs`.

## Already shipped this session (context, not your task)

Separately from feature C, the DSL-display-names / pluggable-`IPhase` / RAUN000 work from
`docs/superpowers/plans/2026-06-05-raun-dsl-display-pluggable-phases.md` is **done** — 6 green jj
commits, whole-repo build clean, all test projects green (Raun.Test 30, Generator.Test 45,
Mtp.Test 73, sample 18). Don't redo it.
