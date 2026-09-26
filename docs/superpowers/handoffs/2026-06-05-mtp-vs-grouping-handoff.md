# Handoff — VS Test Explorer tree grouping collapse (Raun.Mtp)

**Date:** 2026-06-05
**Branch / bookmark:** `main` (also was `feat/raun-mtp`, now deleted). HEAD = `63bd4fc Drop Raun.Xunit; MTP is the only adapter`.
**Status:** Root cause NOT yet found. Our node emission is proven correct; the symptom is VS-side rendering, but the user (rightly) wants us to keep hunting for *our* bug before blaming VS/MTP. The last disproven theory was Uid instability.

---

## The problem

In VS Test Explorer, the sample's 18 step nodes render under **`<Empty Namespace>` → `<Empty Class>` → `.`** instead of the wanted `AppointmentTests → Scenarios → <ScenarioMethod> → steps`.

Refined symptom (from the user, the key clue): **the nodes group correctly *while the run is populating* (live), then collapse into one nameless blob the instant the run finishes / when idle.** So VS *does* read our grouping signal during execution, but the settled/idle tree is nameless.

VS version is "v18" (very new; `.vs/Raun.slnx/v18/TestStore`). Sample is a pure-MTP app (no VSTest adapter), so VS uses its native-MTP integration.

---

## What we changed this session (all UNCOMMITTED in the working tree)

Two logical changes are mixed in the working tree:

### A. The grouping fix (the actual attempt)
- **`src/Raun.Mtp/ScenarioTestIdentity.cs`** (new) — splits `ScenarioDefinition.MethodName` (FQN `Namespace.Type.Method`) into (namespace, type, method) and builds a `TestMethodIdentifierProperty(asm, ns, type, method, 0, [], "System.Void")`. Assembly = `Assembly.GetEntryAssembly()?.FullName`. 7-arg ctor confirmed against MTP 1.9.1 XML docs + xunit's `TestNodeExtensions.AddMetadata`.
- **`src/Raun.Mtp/RaunDiscoverer.cs`** — `BuildNode` now also adds `ScenarioTestIdentity.Create(definition.MethodName)`.
- **`src/Raun.Mtp/RaunStepReporter.cs`** — `BuildNode` (shared by start + finish) now also adds the identity. Also wraps `Publish` with `NodeDiagnostics.Log("run", node)`.
- **Tests:** `test/Raun.Mtp.Test/RaunDiscovererTests.cs` (identity test + added `method` param to the `Definition` helper), `RaunStepReporterTests.cs` (start + finish identity tests), `ScenarioTestIdentityTests.cs` (new, the Split edge cases). **Full suite green: 143/143.**

### B. Diagnostics (decide whether to keep)
- **`src/Raun.Mtp/NodeDiagnostics.cs`** (new) — env-gated (`RAUN_NODE_DEBUG`) tracer. Set it to a file path, or `1`/`stderr`/`console`. Logs every published node: uid, displayname, state, identity (asm/ns/type/method), full prop bag + count. Zero-cost when unset. Mirrors the old `RAUN_BUS_DEBUG` precedent.
- Wired in at `RaunTestFramework.OnDiscoverAsync` (discover) and `RaunStepReporter.Publish` (run).

### Other working-tree notes
- **`Directory.Packages.props`** — `Microsoft.Testing.Platform` is back to **1.9.1**, but the comment was rewritten to record the v2 finding. (We tried 2.0.2, see below, and reverted.)
- **`samples/AppointmentTests/AppointmentDsl.cs`** shows as `M` but we did NOT edit it — likely a VS touch. Verify/diff before committing; probably revert it.

**Recommendation when committing:** split into two commits — (1) the identity fix + tests, (2) the diagnostics (or drop diagnostics). Don't commit the stray `AppointmentDsl.cs` change without checking it.

---

## Proven facts (with evidence)

1. **Every node we publish carries the correct identity.** Captured via `RAUN_NODE_DEBUG`:
   - discover / in-progress / finished all show `identity=(ns='AppointmentTests' type='Scenarios' method='Booking'…)`.
   - finished node = in-progress node **+ `TimingProperty`** + flipped state. Nothing identity-related is lost on finish.
2. **Discovery and execution emit the EXACT same 18 Uids — zero divergence.** `comm -23/-13` of the two sorted uid sets = empty both ways. So Uids are stable and matching; VS *can* reconcile discovered ↔ executed by uid. (This disproves the "randomness between discover/execute" theory.)
3. **MTP 1.9.1 exposes only ONE identity property** (`TestMethodIdentifierProperty`) — grepped all `*Property` types in the XML. There is no separate "grouping"/"hierarchy" property we're failing to set. MTP's `TreeNodeFilter.MatchesFilter(testNodeFullPath, props)` implies a `/`-segmented node path, but that path is derived by MTP, not something we set directly.
4. **MTP v2 (2.0.2) does NOT fix it** — same collapse. And it breaks the suite: any xunit-based test project that *references* Raun.Mtp unifies MTP to 2.0.2, and xunit.v3 3.2.2's v1-only runner dies with `TypeLoadException: IDataConsumer`. Raun.Mtp + the sample build/run fine on 2.0.2. Reverted to 1.9.1.

## Ruled out
- Missing `TestMethodIdentifierProperty` (added; live grouping proves VS reads it).
- Uid randomness / discover≠execute mismatch (sets identical).
- MTP version (v1 vs v2 — same behavior).

---

## Leading hypotheses still open (for next session)

The user refuses to accept "VS is broken" — so keep looking for our bug. Candidates, roughly in priority:

1. **DECISIVE EXPERIMENT — compare against a known-good native-MTP framework in the SAME VS.** Make a trivial MSTest-with-`EnableMSTestRunner` (or TUnit) project: one namespace/class with 2–3 `[TestMethod]`s, no VSTest adapter. Does *it* group + stay grouped in this VS?
   - If **yes** → it's us. Capture its node emission (it also publishes MTP `TestNode`s) and diff the shape against ours — uid format, display name, which properties, node *count per method* (they emit 1 node/method; we emit N step-nodes/method — see #2).
   - If **no** (it also collapses) → it really is this VS build's native-MTP discovery. Then option = report it / accept.
   - This is the experiment that ends the "is it us or VS" debate. Do this first.
2. **We emit N nodes sharing ONE `(ns,type,method)` identity** (4–5 step nodes all claim `method=Booking`). Conventional frameworks emit one node per method (or distinct parametrized cases). VS's settled tree may dedupe/collapse multiple nodes that share a method identity. Worth testing: temporarily give each step a DISTINCT method name (e.g. `method = Booking_<stepId>`) and see if the settled tree stops collapsing. If it does → the fix is making each step a distinct "method" identity (or adding a per-scenario parent and treating steps as the leaves differently).
3. **LINQ duplicate display names** (minor, probably not the whole-tree cause): the `ImportUsersViaLinq` scenario emits 3 discovery nodes all named `user {name} exists` (unsubstituted template) with distinct uids. Other scenarios have unique names and *still* collapse, so this isn't the root cause — but it's a real separate bug (residual gap #5) and could confuse VS for that one scenario.
4. **`EnableMSTestRunner=true` leftover** in `samples/AppointmentTests/AppointmentTests.csproj` (line ~11) is suspicious for a Raun app — confirm it isn't pulling an MSTest discovery path that conflicts. Likely harmless but unverified.

---

## How to reproduce the diagnostics

```bash
# discovery nodes
rm -f /tmp/d.log && RAUN_NODE_DEBUG=/tmp/d.log dotnet run --project samples/AppointmentTests --no-build -- --list-tests >/dev/null 2>&1
# run nodes (start + finish per step)
rm -f /tmp/r.log && RAUN_NODE_DEBUG=/tmp/r.log dotnet run --project samples/AppointmentTests --no-build >/dev/null 2>&1
# uid diff (should be empty both ways)
grep -oE "uid='[^']+'" /tmp/d.log | sort -u > /tmp/du.txt
grep -oE "uid='[^']+'" /tmp/r.log | sort -u > /tmp/ru.txt
comm -23 /tmp/du.txt /tmp/ru.txt ; comm -13 /tmp/du.txt /tmp/ru.txt
```

Build/test reminders (from prior handoffs): run tests with `dotnet test Raun.slnx` (NOT `--nologo` — MTP rejects it and prints help with "Zero tests ran"). Sample/AppointmentTests is `samples/AppointmentTests`, scenarios in `Scenarios.cs`, methods `Booking`, `BookingWithParallelArrange`, `ImportUsers`, `ImportUsersViaLinq` (all in `namespace AppointmentTests; class Scenarios`).

## Key files
- `src/Raun.Mtp/RaunDiscoverer.cs` — `BuildNode` / `BuildNodes`, `MakeUid` = `{ScenarioId}:{StepId}`.
- `src/Raun.Mtp/RaunStepReporter.cs` — per-step lifecycle → `TestNodeUpdateMessage`.
- `src/Raun.Mtp/RaunTestFramework.cs` — `OnDiscoverAsync` / `OnExecuteAsync`, filter parsing.
- `src/Raun.Mtp/RaunRunLoop.cs` — `SelectScenarios` maps filter uids → scenarios.
- `src/Raun/StableId.cs` — how ScenarioId / StepId hashes are computed (check determinism if revisiting #2).
- xunit MTP reference: `C:\dev\vendor\xunit\src\common\MicrosoftTestingPlatform\` (esp. `TestNodeExtensions.cs`, `TestPlatformDiscoveryMessageSink.cs`). NOTE: xunit in VS Test Explorer actually runs via `xunit.runner.visualstudio` (VSTest), NOT this MTP path — so xunit is NOT a reliable "this works in VS" reference for native MTP.
