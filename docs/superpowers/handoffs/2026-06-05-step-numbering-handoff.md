# Handoff — Step numbering + scenario-name grouping (Raun.Mtp)

**Date:** 2026-06-05
**Branch / bookmark:** `main` (detached HEAD per `git`; repo is `jj`). HEAD = `63bd4fc Drop Raun.Xunit; MTP is the only adapter`.
**Status:** (1) The VS tree-grouping collapse is **RESOLVED**. (2) A follow-up feature — step
numbering + showing the scenario name as the grouping node — is **spec'd and approved, not yet
implemented**. Next session: implement it via TDD.

---

## Part 1 — Grouping collapse: RESOLVED (was stale VS cache, not our code)

The prior handoff (`2026-06-05-mtp-vs-grouping-handoff.md`) chased a VS Test Explorer collapse to
`<Empty Namespace>/<Empty Class>/.`. Root cause found and confirmed:

- **Our emission is correct and identical across discover/execute.** Traced live with
  `RAUN_NODE_DEBUG`: every discovery *and* execution node carries
  `ns='AppointmentTests' type='Scenarios' method='<Method>'`. Not us.
- **xunit "works" because in this VS it runs via `executor://xunit/VsTestRunner2` (VSTest),** not
  native MTP. Our sample is the only thing on VS 18's `executor://testingplatform-bridge/v1`
  (the MTP→VSTest bridge) — a different renderer, so the xunit comparison was apples-to-oranges.
- **The collapse was stale cache.** VS persists discovered tests in
  `.vs/Raun.slnx/v18/TestStore/0/006.testlog` (MessagePack; `0xd9`=str8). It had accumulated
  **namespace-stripped discovery records** (`.Scenarios.X`, the 16 `AppointmentTests` bytes
  clobbered) from earlier-this-session iterations *before* the identity split was correct, while
  fresh execution records were full-FQN. VS rendered the **idle** tree from the stale discovery
  records → collapse; every live run re-grouped correctly.
- **Fix/proof:** closed VS, backed up + deleted `TestStore`, reopened, Discover+Run All →
  **tree groups correctly** (`AppointmentTests → Scenarios → <Method> → steps`). User confirmed
  with a screenshot. Old log backed up to `%TEMP%\raun-testlog-pre-clear-2026-06-05\006.testlog`.

**Lesson (durable):** when you change identity/uid/display emission mid-dev, **clear
`.vs/<sln>/<ver>/TestStore`** before judging VS Test Explorer — it caches discovery and renders
the idle tree from it.

**Suite:** `dotnet test Raun.slnx` → **143 passed, 0 failed** (current working tree). The sample
takes ~10s only because of a debug `Task.Delay(5000)` (see cleanup below).

---

## Part 2 — Next work: step numbering + scenario name (spec'd, approved, NOT implemented)

**Spec (committed):** `docs/superpowers/specs/2026-06-05-step-numbering-design.md`. Read it first;
summary of the approved decisions:

1. **Numbering — grouped sub-numbers.** Walk `ScenarioNode`s in `Index` order. A standalone step
   (`GroupId == null`) consumes the next top-level integer `N`. A parallel/array group (consecutive
   nodes sharing a non-null `GroupId`) consumes **one** integer `N`; members get `N.1`, `N.2`, …
   `GroupId` is reliably populated (snapshot tests: tuple→`g1`, array→`g0`, standalone→`null`).
2. **Leaf text — drop the `{scenario} ▸ {step}` prefix.** Standalone → `"{label}. {step}"`
   (`1. the database is clean`); group member → `"{label} {step}"` (`2.1 patient Jane exists`).
3. **Scenario name → `Method` only.** `TestMethodIdentifierProperty.MethodName` = scenario
   `DisplayName`; `Namespace`/`TypeName` unchanged (`AppointmentTests`/`Scenarios`). Gives
   `AppointmentTests → Scenarios → customer books an appointment → 1. …`.
4. **Zero-pad for lexical sort (load-bearing).** VS sorts sibling leaves lexically as strings.
   Pad the top-level number AND each sub-index to a **per-scenario** digit-width (≤9 steps render
   `1.`–`9.`; ≥10 render `01.`…). Else `10.` sorts before `2.`, `2.10` before `2.2`.
5. **Uid unchanged** (`{ScenarioId}:{StepId}`); discovery + execution must share the **same**
   numbering source so their leaf text agrees.

**Planned files** (display layer only):
- `src/Raun.Mtp/ScenarioStepNumbering.cs` (NEW) — pure `Compute(ScenarioDefinition) →
  IReadOnlyDictionary<int,string>` (index→label). Algorithm in the spec (group-by-first-encounter,
  then pad). Mirrors `ScenarioTestIdentity` precedent.
- `src/Raun.Mtp/ScenarioTestIdentity.cs` — `Create(methodFullName, scenarioDisplayName)`; method =
  scenario name.
- `src/Raun.Mtp/RaunDiscoverer.cs` + `RaunStepReporter.cs` — compose numbered display, drop prefix.
- Tests: `ScenarioStepNumberingTests` (NEW: linear, tuple, array, group-at-start, two groups, ≥10
  padding, ≥10-member group, lexical-sort assertion); update `ScenarioTestIdentityTests`,
  `RaunDiscovererTests`, `RaunStepReporterTests`.

**Approach:** TDD, behavioral test first (per `superpowers:writing-plans` → `executing-plans`).
The brainstorming session's terminal step is `writing-plans`; start there.

**Post-impl verification (don't skip):** the `Method` now contains spaces. VS renders `Booking`
and parametrized names with spaces fine, but the bridge managed-name path proved fragile — so
**clear `TestStore`, rebuild, Run All in VS, confirm grouping still holds** with the scenario name
as the method (and that any special chars in a scenario name don't break the managed name).

---

## Working-tree state (all uncommitted except the spec)

```
M Directory.Packages.props                         # MTP back to 1.9.1 + updated comment — keep
M samples/AppointmentTests/AppointmentDsl.cs        # DEBUG: await Task.Delay(5000) in ImportUsers — REVERT
M src/Raun.Mtp/RaunDiscoverer.cs                  # identity fix (correct) — keep/commit
M src/Raun.Mtp/RaunStepReporter.cs                # identity fix (+ NodeDiagnostics.Log) — keep
M src/Raun.Mtp/RaunTestFramework.cs               # NodeDiagnostics wiring — keep or drop w/ diagnostics
M test/Raun.Mtp.Test/RaunDiscovererTests.cs       # identity tests — keep
M test/Raun.Mtp.Test/RaunStepReporterTests.cs     # identity tests — keep
?? src/Raun.Mtp/ScenarioTestIdentity.cs            # NEW identity helper — keep
?? src/Raun.Mtp/NodeDiagnostics.cs                 # NEW env-gated tracer (RAUN_NODE_DEBUG) — keep or drop
?? test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs # NEW — keep
?? docs/.../2026-06-05-mtp-vs-grouping-handoff.md    # prior handoff
?? docs/.../2026-06-05-step-numbering-handoff.md     # this handoff
   docs/.../specs/2026-06-05-step-numbering-design.md # COMMITTED this session
```

**Recommended commits (jj; no trailers):** (a) the identity fix + its tests (the change that makes
grouping work); (b) `NodeDiagnostics` separately, or drop it (the step-numbering work changes
`RaunDiscoverer`/`RaunStepReporter` anyway, so decide before starting). **Revert the
`Task.Delay(5000)`** in `AppointmentDsl.cs` before committing the sample.

## Repro / build reminders
- Tests: `dotnet test Raun.slnx` (NOT `--nologo`; MTP rejects it → "Zero tests ran").
- Node trace: `RAUN_NODE_DEBUG=<file> dotnet run --project samples/AppointmentTests --no-build [-- --list-tests]`.
- Sample scenarios in `samples/AppointmentTests/Scenarios.cs`: `Booking` (linear), `BookingWithParallelArrange`
  (tuple group), `ImportUsers` (array group, group is first step), `ImportUsersViaLinq` (array of 3).
