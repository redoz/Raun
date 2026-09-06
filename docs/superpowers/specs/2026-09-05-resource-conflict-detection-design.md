# Resource conflict detection (replaces lock-based C2) — Design

- **Date:** 2026-09-05
- **Status:** Approved (Patrik, 2026-09-05). Supersedes the "C2 — real locking & scheduling" phase
  of `2026-06-06-resourcing-system-design.md`.
- **Origin:** `2026-09-04-c2-resource-scheduling-findings.md` — the analysis that showed half the
  declared claims are unlockable by construction.
- **Scope:** `src/Raun.Generator` (one analyzer rule, claim emission order), `src/Raun`
  (a scenario-scoped conflict ledger wired through the scheduler and `ResourceContext`). No new DSL
  surface. No change to `Raun.Mtp` or the report.

## Summary

Resource claims stay what they are today — trace labels with an implied `LockMode` — and the
framework **never locks or waits on them**. Instead it **detects conflicts**: two steps that may run
concurrently and both touch the same resource identity, with at least one of them mutating it, are
an authoring defect and are reported as one.

Two tiers ship together:

1. **RAUN013 (compile time).** Inside one parallel group, two elements pass the *same step-output
   local* to parameters with a conflicting pair of roles. Caught in the IDE before anything runs.
2. **Conflict ledger (run time).** Inside one scenario, two *unordered* steps (no dependency path
   between them) record effects on the *same resolved identity* with a conflicting pair of modes.
   The later claim throws `ResourceConflictException`; that step fails naming both steps and the
   identity. This catches what the analyzer cannot see: two different locals that resolve to one
   runtime key, and every return-role claim.

A third tier — type-level admission control so *scenarios* touching a shared fixture do not overlap
— is deferred until scenarios run concurrently at all. Today they run sequentially.

## Why not locks

The original C2 was two-phase locking across concurrently running scenarios, with wound-wait to
break deadlocks. Every link in that chain is forced and every link costs:

- **Return-role claims resolve after the call.** `[Created]` binds to `__r`, which exists only once
  the step has run. Nothing can lock a row before it is created.
- **Parameter-role claims resolve mid-flight.** `__inputs.Get<T>(i)` exists only once the producer
  has passed. A scenario's full claim set is therefore never known at scenario start, so the
  "pre-acquire everything in canonical order" fast path is not weakened — it is gone.
- **Per-step locks are useless across scenarios.** Scenario A creates `User:jane` in step 1 and
  asserts on it in step 3; scenario B deletes `User:jane` between them. Held by nobody at that
  moment. The isolation unit has to be the whole scenario, which is 2PL.
- **2PL with incremental acquisition is hold-and-wait, so it can deadlock.** Wound-wait prevents
  the deadlock by cancelling the younger scenario and re-running it from the top. That re-run repeats
  real side effects on the system under test, interleaves with teardown, and turns the retry cap into
  a new class of flaky failure. Every step author inherits an idempotency contract.

So the fear "we will end up with random deadlocks" is slightly off: wound-wait is provably cycle-free.
What you actually end up with is **nondeterministic re-execution of real writes**, which for an
integration-test framework is worse. And none of it buys anything until scenarios run concurrently,
which they do not.

Detection has none of these costs. It never waits, so it cannot deadlock; it never re-runs anything;
and it reports the defect the locks would have silently papered over.

## Who owns a conflict decides the response

- **Inside one scenario, fail.** Two parallel siblings both mutating the same entity is a bug in the
  scenario. Serializing them would pick an arbitrary order, change the timing, and hide the bug.
  Failing names both steps and tells the author to sequence them or downgrade one to `[Read]`.
- **Across scenarios, serialize.** Scenarios are independent by contract; their relative order is the
  framework's business, not the author's. Serializing them is the correct, expected behaviour (xUnit
  collections). This is Tier 3 and is deferred.

## Facts about the graph this design relies on

Verified in `ScenarioParser`:

- Sequential statements always join on `_prevFrontier`. **Within a scenario, concurrency arises only
  from tuple groups, array groups, and the LINQ `Range().Select().ToArray()` unroll.**
- `if` arms are mutually exclusive (opposite guard values on one condition) and everything after the
  `if` depends on the merge nodes (or the condition), so arms are never concurrent with each other
  or with what follows.
- Ordering edges are `DependsOn`, `MergeSources` (a merge selects one source's output) and
  `Guards[].ConditionIndex` (a guarded node runs after its condition). The transitive closure of those
  three is "must run after".
- A local has one definition at any program point (arm re-assignments become merges), so two
  arguments naming the same local in one group name the same producer node.

## Tier 1 — RAUN013 `ConflictingParallelAccess`

**Where:** `ScenarioAnalyzer`, alongside the existing group analysis (`AnalyzeAwaited` for tuples and
arrays, `AnalyzeLinqArray` for the unroll). Purely local to one group; no graph is built.

**Rule.** For one parallel group, collect for every element every `(local, mode, role, argument
location)` where:

- the element call resolves to a DSL method,
- the argument (matched to its parameter by `NameColon` or position, as the parser does) is, or
  contains, an identifier bound to an `ILocalSymbol` that is a prior step output, and
- the parameter carries a role: `[Edited]`/`[Deleted]` ⇒ exclusive; `[Read]` ⇒ shared; a bare
  parameter named in the method's `References`/`Consumes` ⇒ shared (the lineage confers the role).

Two entries from **different elements** on the **same local** where **at least one is exclusive** is
RAUN013. Report once per offending pair, at the later element's argument. Return roles never
participate: a step's return is its own output, shared with no sibling.

**LINQ unroll.** The lambda body is one call repeated `count` times. If `count >= 2` and the body
passes an outer step-output local with an exclusive role, the group conflicts with itself: RAUN013 at
that argument.

**Severity:** Error. The runtime ledger would fail the same scenario deterministically (Tier 2), so
letting it compile only moves the failure later.

**Message:** `Steps '{0}' and '{1}' run in parallel and both access '{2}', at least one with a
mutating role ({3}); give one step a dependency on the other, or declare the access as [Read]`.

**Not caught here, by design:** two *different* locals that resolve to one runtime identity, e.g.
`(Given.PatientExists("Jane"), Given.PatientExists("Jane"))`. That is Tier 2's job.

## Tier 2 — scenario-scoped conflict ledger

**New types** in `src/Raun/Resources/`:

- `ResourceLedger` (public sealed). One per `ScenarioScheduler.RunAsync`. Constructed with the
  scenario's "must run after" relation as an `IReadOnlyList<ScenarioNode>` and computes the
  transitive ancestor set per node once. Exposes `Claim(int nodeIndex, string stepDisplayName,
  ResourceIdentity identity, LifecycleVerb verb)`.
- `ResourceConflictException` (public sealed, derives from `InvalidOperationException`). Carries
  `Identity`, `StepDisplayName`, `Verb`, `OtherStepDisplayName`, `OtherVerb`. Message:
  `Step '{step}' ({verb}) and step '{other}' ({otherVerb}) both touch {identity} and nothing orders
  them; add a dependency between them or declare one access as [Read]`.

**Claim semantics.** Under one lock, for every prior claim on the same identity by a *different*
node that is *unordered* with respect to the claiming node (neither is an ancestor of the other):
if either verb's `LockMode` is `Exclusive`, throw. Otherwise register the claim. A repeated claim by
the same node on the same identity is dedup, never conflict. Ordered nodes never conflict, whatever
the verbs: the graph already serialized them.

**Structural, not observed.** Detection does not depend on whether the two steps happened to overlap
in wall-clock time. Two unordered steps that conflict fail the scenario on every run, so the
diagnostic is deterministic and cannot be flaky. The one asymmetry is *which* of the two steps
fails: the one whose claim arrives second. The message always names both.

**Plumbing.**

- `ResourceContext` gains an internal `AttachLedger(ResourceLedger ledger, int nodeIndex)`; its
  `Record` calls `ledger.Claim(...)` **before** recording the effect. A refused claim records no
  effect — the exception carries the identity instead. A `ResourceContext` built outside the
  scheduler (a DSL method under unit test) has no ledger and behaves exactly as today.
- `ScenarioContext` gains the same internal `AttachLedger`, mirroring `AttachTeardown`, and
  forwards to its `Resources`. No constructor changes.
- `ScenarioScheduler.RunAsync` builds one ledger from `definition.Nodes` and attaches it inside
  `RunNodeAsync`. Merge, pass-through, and teardown nodes never invoke a body, so they claim nothing.
  Preflight is a one-node scenario and is unaffected.

**Generator: declare before you touch.** Claims whose expression and subject expressions do not
mention `__r` (all parameter roles, and lineage targets whose subject is a parameter) are emitted
**before** the DSL call; claims involving `__r` stay after it. A conflict on an input is therefore
refused before the step performs its real side effect. This reorders the per-step effect stream
(parameter effects now precede return effects — which is already what the tests assert for
`When.Book`) and moves the `Resource_scenario` snapshot.

**Escape hatch for deliberate races:** none. A scenario that intentionally sends two concurrent
mutations to one entity (to test optimistic concurrency in the SUT, say) cannot currently declare
that intent; it would have to declare one side `[Read]`, which misreports the trace. Open item: add
an explicit opt-in only when a real scenario needs it. No design until then.

## Tier 3 — deferred: type-level admission across scenarios

> **Superseded 2026-09-06** by `2026-09-06-concurrent-scenarios-design.md`: cross-scenario admission
> is declared with `[ContendedResource]`/`[Uses<T>]`, not derived from roles.

When scenarios run concurrently (they do not yet — `RaunRunLoop` is sequential), the run loop
gains admission control: a scenario starts only when its **static, type-level claim set** (every
role-bearing parameter/return type with its `LockMode`, emitted by the generator onto the
definition) does not conflict with any running scenario's; the whole set is acquired atomically at
start and released at end, so there is no hold-and-wait and no deadlock. Coarse on purpose:
type-level is the only granularity knowable before a scenario runs. Singleton fixtures ("the
database") are exactly the case where type == identity, so a singleton marker returns *in that
design*, not before. `[Created(Key = nameof(arg))]` can later refine type to identity where the key is
argument-derived. Needs its own brainstorm; do not start it from this document.

## Testing

**Analyzer (`AnalyzerTests`):**
- RAUN013 is a supported diagnostic.
- Tuple: `(When.Rename([Edited] p), When.Suspend([Edited] p))` on one local ⇒ RAUN013.
- Tuple: `[Edited]` vs `[Read]` on one local ⇒ RAUN013.
- Tuple: `[Read]` vs `[Read]` ⇒ clean. Different locals ⇒ clean. Sequential statements ⇒ clean.
- Lineage-named target (shared) vs `[Edited]` on one local ⇒ RAUN013; vs `[Read]` ⇒ clean.
- LINQ unroll with count 2 passing an outer local to `[Edited]` ⇒ RAUN013; count 1 ⇒ clean.
- Named arguments are matched to parameters correctly.
- Existing valid scenarios (all `SampleSources`) stay clean.

**Ledger (`ResourceLedgerTests`):**
- Unordered nodes, Exclusive vs anything ⇒ throws with both names, identity, both verbs.
- Unordered nodes, Shared vs Shared ⇒ no throw.
- Ordered via `DependsOn`, via `MergeSources`, via `Guards` ⇒ no throw even for Exclusive vs
  Exclusive.
- Same node claiming twice ⇒ no throw.
- Different identities ⇒ no throw.

**Scheduler (`SchedulerTests`):**
- Two sibling nodes both `Create` the same identity ⇒ exactly one is `Failed` with
  `ResourceConflictException`, the other `Passed`; the failure names both steps.
- Chained nodes `Edit` then `Delete` the same identity ⇒ both pass.
- A `ResourceContext` with no ledger (direct construction) still records as before.

**Generator (`ResourceLoweringTests`, snapshot):**
- Parameter-role calls appear before the DSL call in the emitted source; return-role calls after.
- Effect order for `When.Book` unchanged: Read, Edit, Create.
- `Resource_scenario` snapshot re-verified.

**End to end:** `dotnet test Raun.slnx` grows from 386; both samples still run green (their
parallel groups create distinct identities).

## Documentation

- `LockMode`: "the conflict class a verb implies; two effects on one identity conflict when at least
  one is Exclusive; used for dedup precedence and the scenario conflict ledger. No locks are taken."
- `ResourceContext` / `ScenarioContext.Resources`: mention the ledger and the exception.
- `2026-09-04-c2-resource-scheduling-findings.md`: status flips to "resolved — lock-based approach
  rejected, detection shipped", with a pointer here.

## Rejected alternatives

- **2PL + wound-wait (the original C2).** See "Why not locks".
- **Per-step locks across scenarios.** Do not protect anything a scenario asserts on later.
- **Serialize on intra-scenario conflict.** Picks an arbitrary order and hides the defect.
- **Timing-based overlap detection** (fail only when the two steps were observed running at the same
  time). Flaky by construction; the structural rule fails deterministically instead.
- **Warning severity for RAUN013.** The runtime would fail the same scenario anyway.
- **Building the analyzer rule on the full DAG.** Unnecessary: concurrency inside a scenario is
  exactly group membership, which is syntactic.
