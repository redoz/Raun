# Handoff: scenario steps as first-class MTP tests (discovery model)

**Status:** Mid-brainstorm. Root cause confirmed, direction partly decided, **blocked on one architecture fork** (below). No design doc written, no production code for the redesign written. A separate, self-contained "bus debugging" deliverable is sitting **uncommitted** in the working tree (see §6).
**Date:** 2026-06-05
**Driver:** Patrik. Collaboration is evidence-driven — verify, don't assert.

---

## 1. The problem (one paragraph)

Raun reports each scenario *step* as its own visible test. Today `ScenarioDiscoverer` discovers **one** `ScenarioTestCase` per `[Scenario]` method; at run time `ScenarioStepReporter` invents per-step `TestUniqueID`s (`UniqueIDGenerator.ForTest(caseId, index)`) and queues `TestStarting/TestPassed|Failed|Skipped/TestFinished` on the `IMessageBus`. The xUnit **native** runner accepts these dynamically (18 steps in the AppointmentTests sample show fine), but the **VSTest** path (`xunit.runner.visualstudio` + `dotnet test`/classic Test Explorer) folds all 18 step results onto the 4 discovered scenario cases and logs `TestRunCache: No test found corresponding to testResult '… ▸ …' in inProgress list` ×18. In VS Test Explorer the per-step breakdown collapses. This is the unresolved "double-check" item **D.2** in `docs/reference/xunit-v3-extensibility-api.md`.

---

## 2. Evidence gathered (so nobody re-derives it)

**Root cause, confirmed (not hypothesized):** The fold is a **VSTest-bridge artifact**. VSTest's `TestRunCache` matches every result to a *discovered* `TestCase` by id and warns+collapses when it can't. Captured fresh from `dotnet test --diag`: 4 cases discovered, 18 results recorded, **each step result carries the parent scenario's `TestCase`** (same `XunitTestCaseUniqueID`, only `DisplayName` differs) → 18 "No test found" warnings.

**MTP has no such match-cache.** Per the MTP framework docs, a framework publishes `TestNodeUpdateMessage`s carrying a state property (`Passed/Failed/Skipped/InProgress`); the official *run* example even mints node `Uid`s inline during execution. So run-time-reported tests are first-class under MTP — nothing to fold them into.

**"MTP v2" is a real, selectable thing.** xUnit v3 (3.2.0+) picks the MTP protocol version via package variant: `xunit.v3.mtp-v1` (default in 3.x), **`xunit.v3.mtp-v2`** (default in 4.0), `xunit.v3.mtp-off` (verified all exist at 3.2.2 on nuget.org). Drop `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`; add `global.json` `{"test":{"runner":"Microsoft.Testing.Platform"}}` for `dotnet test` on .NET 10.

**xUnit v3.2.2 source facts (read by subagent against the real assemblies; cite before trusting memory):**
- Pre-discovery is **per `IXunitTestCase`** only. `TestFrameworkDiscoverer.Find` walks `ITestCase`s and **never calls `CreateTests()`**. Discovery emits one `ITestCaseDiscovered` per case → one MTP node (`TestPlatformDiscoveryMessageSink.OnTestCaseDiscovered`: `Uid = discovered.TestCaseUniqueID`).
- **The only way to get N pre-discovered, individually-addressable nodes is N distinct `IXunitTestCase`s** (exactly how `TheoryDiscoverer` pre-enumerates — one case per data row).
- `CreateTests()` / `IXunitTest` is **execution-time only**, never pre-discovered. The default runner invokes the body **once per `IXunitTest`** (`XunitTestCaseRunner.RunTest` → `XunitTestRunner.Run`) — so N tests on one *ordinary* case = N executions.
- **Escape hatch:** `ISelfExecutingXunitTestCase` (what Raun uses). `XunitTestMethodRunnerBase.RunTestCase` calls `Run(...)` **once** and skips `CreateTests`/the per-test fan-out. So a self-executing case runs its body once regardless of how many tests it reports.
- xUnit's MTP sinks key **every** node to `TestCaseUniqueID` (`TestPlatformExecutionMessageSink` line ~239) and **never set `ParentTestNodeUid`** — **no tree**. (And per testfx#2537, current Test Explorer doesn't render the MTP hierarchy anyway, so even owning the framework we'd show flat `Scenario ▸ step` nodes today.)
- Net: our per-step `TestUniqueID`s on one case **collapse onto the single scenario node** — the same symptom on the xUnit side, independent of VSTest.

**Spike (throwaway, lives at `C:\dev\mtp-spike\`, outside the repo):** a minimal `xunit.v3.mtp-v2` project — custom `[StepScenario]` + discoverer returning **N self-executing step-cases**, all sharing one memoized `Lazy<Task>` keyed on the scenario, with two observability logs (`runs.log` = real scenario executions, `invocations.log` = step-case `Run` calls). Proven under MTP v2:
- **Run All** → `total: 3` distinct nodes, `runs.log` = **1** (scenario body ran once for 3 step-cases). ✅
- **Run one step** (`exe -id <uid>`) → `Total: 1`, `runs.log` = **1** (a single step triggers the whole scenario exactly once). ✅ — this answers Patrik's "if you run one step you run all its deps + that step — can we do that?": **yes.**

---

## 3. Decisions settled — do NOT re-litigate

- **MTP v2 only. No legacy / no VSTest.** (`xunit.v3.mtp-v2`, drop `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`, `global.json` MTP runner.)
- **Model B: one MTP node per step** (per-step visibility is Raun's whole point), implemented as **N self-executing step-cases per scenario sharing one memoized scenario run** so the DAG executes exactly once. Proven viable (§2 spike).
- **Single-step UX = (b) "light up all":** running any one step should report results for **every** step that executed, not just the selected one. (Rationale: with a dependency DAG, running "the appointment should exist" also runs patient/slot/create, so they should light up too.)

---

## 4. THE OPEN FORK (this is the blocker — answer first)

Patrik asked: *"you said xUnit does something we might not like — could we plug-and-replace that part?"* The part (the message-sink→TestNode mapping) is **internal to `xunit.v3.core`**; you can't swap it while riding on xUnit, and step granularity is already lost by the time it's an MTP node, so a downstream rewriter can't recover it. So the real choice is:

- **Path 1 — stay an xUnit extension.** Get N nodes via N self-executing step-cases (spike). (b) "light up all" requires a **workaround**: when one step runs, publish results for sibling step nodes that the platform never scheduled (no `TestCaseStarting/Finished` lifecycle for them) — feasibility **not yet verified** (the spike edit for this is staged but unrun; see §6). Keeps full xUnit ecosystem **and `[Fact]` coexistence in the same project.**
- **Path 2 — Raun becomes its own MTP `ITestFramework`.** Replace xUnit's adapter; we own discovery, node reporting, run semantics. Makes (b) **clean** (we own the node lifecycle — no sibling hack). Cost: **leaves the xUnit host.** MTP allows **one** framework per test app, so `[Scenario]` and ordinary xUnit `[Fact]` **cannot coexist in one project** unless our framework also hosts/delegates to xUnit (meaningfully more work). Bigger build (own run loop, filtering, cancellation, `dotnet test` wiring). Still no literal tree today.

**Gating question put to Patrik (awaiting answer):** *Do you want Raun scenarios to live alongside regular xUnit `[Fact]` tests in the same project?*
- **Yes** → Path 1 (finish the (b) sibling-reporting spike to see how it behaves within xUnit's constraints).
- **No / separate test projects are fine** → Path 2 (cleaner; (b) falls out naturally). This matches Patrik's earlier "it feels more proper to discover everything upfront."

---

## 5. Next steps (by fork branch)

**Immediately (either path):** get Patrik's answer to §4. Then move to a written design (`docs/superpowers/specs/2026-06-05-…`) before any production code (brainstorming HARD-GATE still in force — nothing in `src/` for the redesign until design approved).

**If Path 1 (xUnit extension):**
1. Finish the staged (b) spike: rebuild `C:\dev\mtp-spike`, run **Run-All** (expect `total: 3`, not 9 — no double-count) and a **single-step** run, confirm siblings light up **and** check for ordering/`No test found`-style breakage when publishing nodes whose case lifecycle never started. This is the one real unknown for Path 1.
2. Real discovery needs the **step list at discovery time**. Today the `ScenarioDefinition` is resolved at *run* time from `ScenarioRegistry`. Confirm the generated definition/registry is reachable during `ScenarioDiscoverer.Discover` (it's in the same assembly being discovered) and emit N step-cases from it.
3. Per-step result lookup: each step-case reports its own step's pass/fail/skip from the shared run results; CTS ownership for the shared run must not let one case's cancellation kill the shared run for siblings.

**If Path 2 (own MTP framework):**
1. Spike a raw `ITestFramework` (`RegisterTestFramework`, handle `DiscoverTestExecutionRequest` + `RunTestExecutionRequest`, publish `TestNodeUpdateMessage`s). Confirm `dotnet test` + Test Explorer drive it and that filtered single-step runs let us publish all step nodes cleanly.
2. Decide the `[Fact]` coexistence story (separate projects vs. delegating host).
3. Re-home discovery onto the generated `ScenarioDefinition` directly (no xUnit discovery).

---

## 6. Working-tree state (UNCOMMITTED)

Nothing committed this session. `git status` shows:

**Separate, self-contained deliverable — "enable full xUnit message-bus debugging" (green, independently committable):**
- `src/Raun.Xunit/TracingMessageBus.cs` (new) — `IMessageBus` decorator logging every `QueueMessage` (kind, test id, result detail, accept/stop bool). Opt-in via `RAUN_BUS_DEBUG` (file path, or `1`/`stderr`/`console`); zero-cost off.
- `src/Raun.Xunit/ScenarioTestCase.cs` (edit) — wraps the bus via `TracingMessageBus.MaybeWrap(messageBus)`.
- `test/Raun.Xunit.Test/TracingMessageBusTests.cs` (new) — 2 behavioral tests (TDD). Full suite green: 21 / 18 / 30 / 29, 0 warnings.
- `samples/AppointmentTests/xunit.runner.json` (new) + csproj edit — `diagnosticMessages` + `internalDiagnosticMessages` on.
- `.gitignore` (edit) — ignores transient diag/trace artifacts.
- **Note:** this rides on xUnit's `IMessageBus`. If we go **Path 2**, `TracingMessageBus` doesn't transfer (we'd trace MTP `TestNodeUpdateMessage`s instead) — but it was the instrument that *found* the root cause and is still useful while on xUnit. Decide whether to commit it regardless (recommended: yes — it's good standalone diagnostics).

**Throwaway spike:** `C:\dev\mtp-spike\` (outside the repo). The (b) "report all from shared run" edit is **applied but not yet run**. Delete when the design is settled.

**Transient diag artifacts (gitignored, safe to delete):** `vsdiag*.log`, `vstest-fresh*.log`, `xudiag.txt`, `bustrace-*.txt`.

---

## 7. Key references

- `docs/reference/xunit-v3-extensibility-api.md` — Raun's own xUnit v3 API notes; item **D.2** is exactly this bug.
- xUnit MTP docs: https://xunit.net/docs/getting-started/v3/microsoft-testing-platform
- MTP framework authoring (TestNode / states / `ParentTestNodeUid`): https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-architecture-test-framework
- xUnit v3.2.2 source (tag `v3-3.2.2`): `XunitTestMethodRunnerBase.cs`, `XunitTestCaseRunner.cs`, `ISelfExecutingXunitTestCase.cs`, `TheoryDiscoverer.cs`, `TestFrameworkDiscoverer.cs`, `src/common/MicrosoftTestingPlatform/{TestPlatformDiscoveryMessageSink,TestPlatformExecutionMessageSink,TestNodeExtensions}.cs`.
- testfx#2537 — TestNode hierarchy not consumed by current runners.
- Existing handoff style mirrored from `docs/superpowers/handoffs/2026-06-03-line-span-mapping-handoff.md`.
