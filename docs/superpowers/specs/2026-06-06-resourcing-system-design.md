# Scenario resources: symbolic resources, real locks — Design

- **Date:** 2026-06-06
- **Status:** Design — awaiting review
- **Scope:** `src/Raun` (core model, scheduler, a new `Resources` subsystem), `src/Raun.Generator`
  (attribute lowering), `src/Raun.Mtp` (session-level lock manager + scenario priority), plus the
  HTML report (feature B of the roadmap) as a consumer.
- **Origin:** Feature C of `docs/superpowers/plans/2026-06-06-roadmap-aspire-report-resources.md`.

## Summary

Let a scenario step declare that it **creates / loads / reads / edits / deletes** named resources
(`Given.UserExists("jane@x")` → a `User` resource). Resources are **symbolic** — identity + data
tokens the framework uses only for *coordination* and *tracing*; it never materializes or mutates
real state (whatever real work a step does is the step's own business). Those symbolic effects power
two things:

1. **Real cross-scenario locking** — shared/exclusive locks so scenarios running in parallel don't
   collide on a shared resource ("this scenario needs exclusive access to `User:123`", or to "the
   system"), with **wound-wait** + automatic re-run when a genuine collision occurs.
2. **Tracing/debugging** — an effect stream (who created/edited/deleted what, when) that feeds the
   HTML report's resource timeline and answers "which step deleted `User:123`?".

The authoring surface is a small imperative API on the existing `ScenarioContext`
(`ctx.Resources.*`), with declarative attributes (`[Creates]`/`[Loads]`/`[Reads]`/`[Edits]`/`[Deletes]`,
per parameter and return — **explicit and required**, no defaults) that the source generator lowers to
that API and uses to drive a static, deadlock-free scheduling fast path.

## Goals

- Symbolic resources with **type-safe identity** (the domain record *is* the resource identity), so a
  resource referenced by one step is provably the same one another step created — riding the dataflow
  the generator already tracks.
- **Real, async, deadlock-free** cross-scenario locking (two-phase locking + wound-wait) that never
  blocks a thread.
- A clean authoring surface: scenario bodies carry **no** resource ceremony; DSL steps declare each
  resource role **explicitly** (the analyzer errors on a missing one — no silent defaults); ambient
  resources ("the system", "the reporting api") are expressible without inventing a type.
- The effect stream is independently useful for tracing/reporting even with locking turned off.

## Non-goals

- **No real state management.** The framework does not create, snapshot, restore, or delete anything
  in the system under test. There is no rollback/compensation; a wounded scenario simply re-runs, and
  the author is responsible for making steps safe to repeat (ephemeral/idempotent test state).
- **No within-scenario locking.** A single scenario's DAG already serializes dependent steps and
  cannot self-deadlock; resources matter only *across* concurrently-running scenarios.
- Not a distributed lock manager — locks live in the test process for the duration of one test run.

## Verified assumptions (from the codebase)

- `ScenarioScheduler.RunAsync` already runs a scenario's DAG as bounded-parallel `Task`s, takes an
  `IServiceProvider? services`, raises an `IStepObserver`, and creates a per-step
  `CancellationTokenSource` (used today for timeouts). These are the seams for: injecting the lock
  manager (services), acquiring/holding locks, observing effects, and **wounding** (cancellation).
- `ScenarioContext` already collects per-step `Log`/`AddAttachment` and exposes `Services`; it is the
  natural home for `ctx.Resources`.
- The generator already reads attributes via `AttributeReader`, tracks **variable dataflow** between
  steps (so a step's resource *identity* rides the value already flowing to it — the *role* is never
  inferred, it stays explicit), and lowers per-node metadata through `Ir` → `ScenarioParser` →
  `ScenarioEmitter` → `ScenarioNode`. Parameter and return-value attributes are reachable
  (`IParameterSymbol.GetAttributes()`, `IMethodSymbol.GetReturnTypeAttributes()`).
- C# 14 / `LangVersion=preview`: generic attributes, static abstract interface members, and CRTP are
  all available.

---

## Core model

- **Resource** = a symbolic `(type, key)` identity plus arbitrary `data` (shown in the trace).
- **Lifecycle verb** = `Create` · `Load` · `Read` · `Edit` · `Delete`. It is both a trace label and
  the lock-mode source: **`Read`/`Load` ⇒ shared**, **`Create`/`Edit`/`Delete` ⇒ exclusive**.
- **Scenario = the transaction and the retry unit.** Locks are held for the scenario (two-phase
  locking) and released together at the end. If a scenario is wounded mid-flight it is cancelled,
  releases its locks, and re-runs from the top.
- **Locks are real; state is symbolic.** The scheduler genuinely serializes on the locks and does
  wound-wait + re-run; it calls no real APIs and rolls nothing back.

Lock-mode mapping is the classic reader/writer rule: many concurrent `Read`s/`Load`s (shared) coexist;
a `Create`/`Edit`/`Delete` (exclusive) excludes everyone. `Read` is *using* an in-scope resource;
`Load` (a.k.a. Get) is *fetching an existing* one — both shared, but distinct lifecycles.

---

## Authoring surface (the syntax)

### 1. What the scenario author writes — nothing extra

Locking is invisible *in the scenario body* — the narrative is unchanged. The resource roles live on
the **DSL step definitions** (next), not here: each value flowing between steps carries the resource
*identity*, and the step's declared role carries the *access*.

```csharp
[Scenario("a suspended user cannot sign in")]
public static async Task SuspendedUserCannotSignIn()
{
    var user = await Given.UserExists("jane@acme.com");  // step creates User:jane@acme.com (exclusive)
    user      = await When.Suspend(user);                // step edits it — same key, still exclusive
    await Then.CannotSignIn(user);                       // step reads it (shared)
}
```

### 2. DSL step declarations — explicit resource roles (no defaults)

Each resource-typed parameter and return value is an independent claim, and its **role must be stated
explicitly**. There are **no defaults**: if a step's parameter or return is a resource type and
carries no role, the analyzer raises **RAUN009** and the build fails. This is deliberate — whether
`Task<User> Foo(...)` *creates* a new user or *loads* an existing one, and whether a `User` parameter
is *read* or *mutated*, changes both the lock mode and the trace, and must never be guessed.

The role menu:

| Where | Attribute | Means | Lock |
|---|---|---|---|
| return | `[Creates]` | a **new** resource | exclusive |
| return | `[Loads]` | an **existing** resource (Get/Load) | shared |
| return | `[Edits]` | the edited result of an existing resource | exclusive |
| parameter | `[Reads]` | read-only use | shared |
| parameter | `[Edits]` | mutates it | exclusive |
| parameter | `[Deletes]` | removes it | exclusive |

```csharp
[StepName("When {user} books {slot}")]
public static async Task<Appointment> Book(
    [Reads] User user,         // shared lock on the user
    [Edits] Slot slot)         // exclusive; the slot is consumed
    => new(user, slot);        // [return: Creates] → a new Appointment (exclusive)

[StepName("Given user {email} exists")]
public static async Task<User> UserExists(string email)
    => new(email);             // [return: Creates] (or method-level [Creates]) → new User

[StepName("Given user {email} is loaded")]
public static async Task<User> LoadUser(string email)
    => await Db.Get(email);    // [return: Loads] → EXISTING User, shared

[StepName("When {user} is suspended")]
public static async Task<User> Suspend([Edits] User user)   // exclusive on the user…
    => user with { Suspended = true };                      // [return: Edits] → same resource
```

(return roles are written `[return: Creates]` etc.; shown as comments above for brevity.)

A resource-typed parameter or return with **no** role is a hard error:

> **RAUN009** — *Resource access must be declared.* "Parameter/return '{0}' is the resource type
> '{1}'; declare its access with `[Reads]`, `[Edits]`, or `[Deletes]` (parameter) or `[Creates]`,
> `[Loads]`, or `[Edits]` (return) — there is no default."

Method-level `[Creates]`/`[Loads]`/`[Edits]` is allowed as an explicit shorthand when a step touches a
single resource (it targets the return). It is *not* a default: a step with an unannotated resource
**parameter** still errors.

Claims **dedup by identity**. The same `(type, key)` is one lock — **exclusive wins over shared** —
and one lifecycle is recorded, the strongest of **Delete > Edit > Create > Load > Read**. So `Suspend`'s
`[Edits]` parameter and `[return: Edits]` collapse to one exclusive claim on the user; restating it is
harmless. Multi-resource steps (read one, edit another, create a third) fall out naturally.

### 3. Resource types — type-safe identity

The resource identity is the domain type itself. The **key** is resolved by a layered chain (first
match wins):

1. `[ResourceKey]` on the key member (declarative, generator-read),
2. a registered selector `Resources.Identify<User>(u => u.Email)` (for types you can't annotate),
3. an `IResourceIdentity` interface the type implements (for **runtime-computed** keys),
4. whole-record value equality (fallback for immutable identity-only records).

Two equivalent ways to make a type a resource — pick per type:

```csharp
// (a) Hand-written: CRTP + a static abstract member. Key projection lives at the TYPE level
//     (so Load<User>(key) and generic lock code need no instance and no reflection).
public sealed record User(string Email, bool Suspended = false) : IResource<User>
{
    public static ResourceKey KeyFor(User u) => u.Email;     // static abstract impl
}

// (b) Codegen: a plain record stays pristine; the generator emits the IResource<User> half.
[Resource(Key = nameof(Email))]
public sealed partial record User(string Email, bool Suspended = false);
```

Both yield the same compile-time **resource catalog** (every resource type + which steps
create/load/read/edit/delete it) that powers the static scheduling fast path.

### 4. Coarse / ambient resources

For things no single step "owns" — "the system", an external API. Two forms, both collapsing to the
same internal `(type, key)`:

```csharp
// First-class singleton: a marker type IS the identity; declared with a generic attribute.
public sealed class TheSystem : ISingletonResource<TheSystem>;

[Scenario("nightly reset")]
[Requires<TheSystem>(Access.Exclusive)]      // whole scenario holds TheSystem exclusively
public static async Task NightlyReset() { ... }

// Ad-hoc / "whatever you want to make up": a bare string, no type required.
await ctx.Resources.Exclusive("System");
await ctx.Resources.Shared("reporting-api");
```

### 5. The imperative substrate (what everything lowers to)

The primary mechanism; the attributes are sugar over it. It hangs off the `ScenarioContext` steps
already optionally accept, is fully generic, and is **async** end to end.

```csharp
await ctx.Resources.Create(user);                       // new, exclusive — key via User.KeyFor
await ctx.Resources.Load<User>("admin@acme.com");       // existing, shared — by key, no instance
await ctx.Resources.Read(user);                         // shared use of an in-scope resource
await ctx.Resources.Edit(user with { Suspended = true });
await ctx.Resources.Delete(user);
await ctx.Resources.Exclusive<TheSystem>();             // singleton, type-checked
```

### 6. Lock lifetime — `IAsyncDisposable` / `await using`

Every acquisition yields an `IAsyncDisposable` token; **`DisposeAsync` is the single release path for
all outcomes** — success, failure, and wound. By default the *framework* owns one scenario-scoped
scope, so authors write no `using` for declarative effects:

```csharp
// scenario runner (framework/generated), conceptually — ONE scope per scenario:
await using var scope = ctx.Resources.BeginScenarioScope(priority);   // IAsyncDisposable
    // each [Creates]/[Loads]/[Reads]/[Edits]/[Deletes] across the steps acquires INTO `scope`
    // ... run the DAG ...
// scope.DisposeAsync() releases every held lock — on normal end, on exception, AND on wound
// (cancellation unwinds to here). A wounded victim needs no special teardown.
```

The lifecycle verbs (`Create`/`Load`/`Read`/`Edit`/`Delete`) **park their token in the current scenario
scope automatically** — you ignore the return, and they release at scenario end. `LockAsync` instead
**hands the token back** so you can scope it yourself with a **narrower** `await using`, to release an
expensive resource early:

```csharp
await using (await ctx.Resources.LockAsync("search-index", Access.Exclusive))
{
    await RebuildIndex();        // held only inside this block
}                                // released here, NOT at scenario end
```

Early release trades away strict serializability for that resource (another scenario can interleave
once you let go), so it is an opt-in "I know what I'm doing" tool; the scenario-scoped default is safe
by construction.

---

## Scheduling & concurrency model

### Two-phase locking + wound-wait

- Each scenario gets a **priority** = a monotonic start timestamp/sequence assigned by the session.
- A scenario acquires locks as it goes (growing phase) and holds them until it ends (shrinking phase
  = release-all on scope dispose). This is 2PL → serializable schedules.
- **Wound-wait** prevents deadlock without a cycle detector: when an **older** scenario A requests a
  resource held by a **younger** B, A *wounds* B — B is cancelled, releases its locks, and re-runs
  later; A proceeds. A younger scenario requesting an older's resource simply waits. Provably
  cycle-free and **deterministic** by priority. (A wait-for-graph *detect-cycle + abort* variant is a
  possible internal alternative; it needs the same machinery and is strictly more complex, so
  wound-wait is the baseline.)

### Wound = cancellation

Wounding reuses the per-step `CancellationTokenSource` the scheduler already creates: the victim's
token fires, its in-flight `AcquireAsync`/step throws `OperationCanceledException`, the scenario
unwinds to its `await using` scope, locks release, and it is re-queued. **Retry cap:** a bounded
number of re-runs (configurable; default small, e.g. 3), after which the scenario fails with a precise
diagnostic naming the contended resources — so a pathological resource design can't loop forever.

### Async locks, no blocked threads

- `IResourceLockManager` is **session-scoped** (shared across all concurrently-running scenarios) and
  injected via the scheduler's existing `IServiceProvider`.
- Per-identity **async reader/writer gate**: a queue of `TaskCompletionSource` waiters, readers share,
  a writer excludes. The BCL has no async RW-lock; Raun core deliberately carries minimal
  dependencies, so we build a small internal one (decision recorded here; revisit only if it proves
  fiddly).
- Nothing blocks: steps already run as `Task`s in the scheduler's `running` set, so an awaited lock
  just suspends that one step's task while other ready steps proceed.

### Static fast path + dynamic fallback

- **Static:** keys known at compile time (constants, or bound to scenario inputs) let the generator
  emit a per-scenario claim set. The scheduler pre-acquires these at scenario start in a **global
  canonical order** → that scenario can't deadlock at all.
- **Dynamic:** runtime-computed keys are acquired on demand mid-step; wound-wait is the safety net
  that keeps even these deadlock-free.

### Cross-scenario execution

This feature presumes — and enables — scenarios running **concurrently** with a shared lock manager.
Today each scenario runs an independent `ScenarioScheduler`; the session layer (`Raun.Mtp`) must own
the lock manager, assign scenario priorities, and run scenarios concurrently against it. (See Open
questions.)

---

## Tracing / report integration

Every acquire emits a **`ResourceEffect`** — verb, identity `(type, key)`, optional `data` snapshot,
owning step, timestamp — plus lifecycle transitions (`Wounded`, `Retried`, `Contended`). These flow
through the existing `IStepObserver`/`StepResult` channel and become:

- a **resource lifeline** in the HTML report (created → edited → deleted across steps), and
- debugging answers ("which step deleted `User:123`?", "why did this scenario re-run?").

The effect stream is valuable **without** locking, which is why the work phases cleanly (below).

---

## Components & boundaries

New subsystem under `src/Raun/Resources/` (core, runner-neutral), plus generator and session wiring.

| Unit | Responsibility | Depends on |
|---|---|---|
| `IResource<TSelf>` / `ISingletonResource<TSelf>` | Marker/identity interfaces; static-abstract `KeyFor`. | — |
| `IResourceIdentity` | Opt-in interface for runtime-computed keys (resolver link 3). | — |
| `ResourceIdentity` (`Type`, `ResourceKey`), `ResourceKey`, `LockMode`, `Access` | Value types for identity + mode. | — |
| `ResourceEffect` | One trace event (verb, identity, data, step, timestamp). | model |
| `ResourceIdentityResolver` | The 4-link key-resolution chain. | attributes, selectors |
| `ResourceContext` (`ctx.Resources`) | Imperative API surface; `BeginScenarioScope`, `LockAsync`, the verbs. | lock manager, resolver |
| `IResourceLockManager` + `AsyncReaderWriterGate` | Session-scoped, per-identity async RW gate; wound-wait. | — |
| `ScenarioLockScope` | `IAsyncDisposable` bag of a scenario's held tokens; releases on dispose. | lock manager |
| Attributes: `[Resource]`, `[ResourceKey]`, `[Creates]`, `[Loads]`, `[Reads]`, `[Edits]`, `[Deletes]`, `[Requires<T>]` | Declarative surface. | — |
| Scheduler integration (`ScenarioScheduler`) | Pre-acquire static claims; hold via scope; wound = cancel. | lock manager |
| Session integration (`Raun.Mtp`) | Own the lock manager; assign priorities; run scenarios concurrently. | core |
| Generator + analyzer (`Raun.Generator`) | Read resource roles (incl. param/return); raise **RAUN009** when a resource-typed param/return has no role; emit identity half + catalog; lower effects to `ctx.Resources.*`. | Roslyn |

Each unit is independently testable: the lock manager and gate with no scheduler; the resolver with no
runtime; the generator lowering via the existing `GeneratorHarness`.

---

## Testing strategy (behavioral, TDD)

- **Async RW gate:** shared coexist; exclusive excludes; FIFO fairness; cancellation removes a waiter;
  no thread blocking (assert via controlled `TaskCompletionSource` ordering).
- **Wound-wait:** older wounds younger (younger's token cancels, releases, re-queues); younger waits
  for older; deterministic outcome by priority; retry cap trips a diagnostic.
- **2PL holding:** locks acquired by a step stay held until scenario scope disposal (not step end);
  `await using` early-release shortens it.
- **Identity resolver:** each chain link wins in order; value-equality fallback; `with`-edited record
  keeps its key.
- **Roles & RAUN009:** a resource-typed param/return with **no** role errors (`RAUN009`); with a
  role, lowers to the right claim/mode (`[Creates]`/`[Loads]`/`[Reads]`/`[Edits]`/`[Deletes]`); static
  keys produce a catalog claim, runtime keys don't. (Via `GeneratorHarness`, behavioral.)
- **End-to-end:** two scenarios contending an exclusive resource serialize and both pass; a forced
  collision wounds and re-runs the younger; effects appear in the trace.

---

## Open questions / risks

1. **Cross-scenario execution model.** The biggest unknown: today scenarios run independent
   schedulers. Realizing real cross-scenario locks needs a session-level coordinator that runs
   scenarios concurrently and shares the lock manager + assigns priorities. This likely touches how
   `Raun.Mtp` drives execution and may be the largest implementation slice.
2. **Re-run idempotency.** Symbolic resources mean the framework rolls nothing back; a re-run repeats
   the step's real side effects. We document the "steps must be safe to repeat" contract; do we also
   want an optional `onRollback` escape hatch later (non-breaking to add)?
3. **Flow-through return ergonomics.** Roles are explicit by design (no defaults). A return that is
   the edited result of an `[Edits]` parameter still needs `[return: Edits]`; once usage data exists,
   revisit whether the generator may infer *that one* same-identity case to cut ceremony — without
   reintroducing guessed lock modes.
4. **Determinism limits.** Wound-wait is deterministic by priority, but *which* scenarios collide can
   still vary with platform parallelism; the outcome (who wins) is stable, the timing isn't.
5. **Scheduler stall guard.** A scenario blocked entirely on a lock (nothing in its `running` set)
   must not trip the scheduler's "stalled with unresolved steps" guard — lock-waits are legitimate
   pending work, not a graph defect. Needs explicit handling.
6. **Async RW-lock: build vs. dependency** — baseline is build-our-own; revisit if it's fiddly.
7. **Generic attributes / static abstract members** require the preview language version (already in
   use repo-wide) and may interact with the analyzer's existing rules — verify no new warnings.

---

## Suggested phasing

Mirrors the roadmap's C1/C2 split so value lands early:

- **C1 — effects & tracing (no locking).** `ResourceContext` verbs + identity resolver +
  `ResourceEffect` stream through the observer + report lifeline. Low risk; unblocks the HTML report's
  resource lane; exercises the whole authoring surface without the scheduler changes.
- **C2 — real locking & scheduling.** `IResourceLockManager` + async gate + `ScenarioLockScope` +
  scheduler/session integration + wound-wait + the static fast path. Built on C1; this is where the
  cross-scenario execution question (#1) is resolved.

Each phase gets its own implementation plan (`writing-plans`) when picked up.
