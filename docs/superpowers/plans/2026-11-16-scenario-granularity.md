# Scenario Granularity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report each scenario as one MTP test when running under Stryker (or when asked to), instead of one test per step.

**Architecture:** Adapter-only. `Raun.Mtp` resolves a `TestGranularity` once per framework instance; in `Scenario` mode it swaps the discovery function, the report sink and the selector. Core (`Raun`) is untouched.

**Tech Stack:** C#, Microsoft.Testing.Platform, xUnit v3 (tests), jj for version control.

**Spec:** `docs/superpowers/specs/2026-11-16-scenario-granularity-design.md`

## Global Constraints

- No changes under `src/Raun/` or `src/Raun.Generator/`.
- Step mode output must stay exactly as it is: every existing test stays green unmodified.
- Environment variable names, verbatim: `RAUN_TEST_GRANULARITY`, `STRYKER_MUTANT_FILE`.
- Valid override values, case-insensitive: `step`, `scenario`.
- Scenario node uid is exactly `ScenarioDefinition.ScenarioId`.
- Version control via `jj` only; no trailers in commit messages.
- Build must stay warning-free (`AnalysisLevel 10.0-all`, warnings as errors).

## Review Focus

- Empty or whitespace `RAUN_TEST_GRANULARITY` / `STRYKER_MUTANT_FILE`: treated as unset, not as an error. (Task 1)
- Step uid sent in scenario mode (stale IDE cache): selects nothing, does not throw. (Task 2)
- Scenario whose only steps are synthetic, or with zero results at finish: no crash; reported skipped. (Task 4)
- Preflight failure in scenario mode: every scenario node reports skipped with the preflight reason, preflight node itself fails. (Task 5)
- Two scenarios finishing concurrently: each gets exactly one in-progress and one terminal message. (Task 4)

---

### Task 1: `TestGranularity` and its resolver

**Files:**
- Create: `src/Raun.Mtp/TestGranularity.cs`
- Test: `test/Raun.Mtp.Test/TestGranularityTests.cs`

**Interfaces — produces:**
- `public enum TestGranularity { Step, Scenario }`
- `internal static class TestGranularityResolver` with
  `const string OverrideVariable = "RAUN_TEST_GRANULARITY"`, `const string StrykerVariable = "STRYKER_MUTANT_FILE"`,
  `static TestGranularity Resolve(TestGranularity? configured, Func<string, string?> environment)`.
  Throws `InvalidOperationException` whose message contains the bad value, `RAUN_TEST_GRANULARITY`, `step` and `scenario`.

- [ ] Tests (theory rows: override, configured, stryker → expected):
  `(null, null, null) → Step`; `(null, null, "x.txt") → Scenario`; `(null, Step, "x.txt") → Step`;
  `(null, Scenario, null) → Scenario`; `("step", Scenario, "x.txt") → Step`; `("SCENARIO", Step, null) → Scenario`;
  `("", null, null) → Step`; `("  ", null, "x.txt") → Scenario`; `(null, null, " ") → Step`.
  Plus `An_unknown_override_value_fails_naming_the_valid_values` for `"scenrio"`.
- [ ] Run, see fail; implement; run, see pass.
- [ ] `jj commit -m "feat(mtp): resolve test granularity from code, environment and Stryker"`

### Task 2: Whole-scenario selection

**Files:**
- Create: `src/Raun.Mtp/Filtering/WholeScenarioSelector.cs`
- Test: `test/Raun.Mtp.Test/WholeScenarioSelectorTests.cs`

**Interfaces — produces:**
- `internal sealed class WholeScenarioSelector : NodeSelector` with
  `static WholeScenarioSelector ForScenarioIds(IReadOnlySet<string> scenarioIds)` and
  `static WholeScenarioSelector Widening(NodeSelector inner)`.
  `Matches(definition, step)` is true for every step of a selected scenario. `Widening` selects a scenario when `inner` matches any non-synthetic step.

- [ ] Tests: scenario id in set → all steps match; other scenario → none; a step uid (`"scn:a"`) in the set → nothing matches and nothing throws; `Widening(new UidNodeSelector({"scn:b"}))` → steps a, b, c of `scn` all match, steps of `other` do not; `Widening` ignores a match on a synthetic step only.
- [ ] Fail, implement, pass, `jj commit -m "feat(mtp): select whole scenarios from a uid list or a step filter"`

### Task 3: Scenario discovery node

**Files:**
- Modify: `src/Raun.Mtp/RaunDiscoverer.cs`
- Test: `test/Raun.Mtp.Test/RaunDiscovererTests.cs` (append)

**Interfaces — produces:**
- `public static TestNode? RaunDiscoverer.BuildScenarioNode(ScenarioDefinition definition, NodeSelector? selector = null)` — `null` when the selector selects no non-synthetic step.

- [ ] Tests: uid `"scn"`, display name `"my scenario"`, `DiscoveredTestNodeStateProperty` present, `TestMethodIdentifierProperty` present, file location from the first non-synthetic step with a known source (synthetic first step skipped), no location when none known; selector excluding the scenario → `null`; null selector → node.
- [ ] Fail, implement, pass, `jj commit -m "feat(mtp): discover a scenario as a single node"`

### Task 4: `ScenarioMtpReportSink`

**Files:**
- Create: `src/Raun.Mtp/ScenarioMtpReportSink.cs`
- Create: `src/Raun.Mtp/StepReporting.cs` (helpers moved out of `MtpReportSink`: state mapping, output lines, attachment writing)
- Modify: `src/Raun.Mtp/MtpReportSink.cs` (call the shared helpers; no behaviour change)
- Test: `test/Raun.Mtp.Test/ScenarioMtpReportSinkTests.cs`

**Interfaces — produces:**
- `internal sealed class ScenarioMtpReportSink : RunEventSink`, same constructor shape as `MtpReportSink`:
  `(SessionUid sessionUid, IMessageBus messageBus, IDataProducer producer, string attachmentRoot)`.
- `internal static class StepReporting` with `IProperty MapState(StepResult)`, `IEnumerable<string> OutputLines(StepResult)`,
  `void AddAttachments(TestNode, string attachmentRoot, ScenarioDefinition, StepResult)`.

Behaviour:
- `ScenarioStarted` → node (uid = `ScenarioId`, name = `DisplayName`) with `InProgressTestNodeStateProperty`.
- Step events publish nothing.
- `ScenarioFinished` → one terminal node. Fold over non-synthetic results in `StartedAt` order:
  first step whose mapped state is `TimeoutTestNodeStateProperty` wins; else first `Failed`/`Error` mapped state; else `Passed` if any step passed; else `Skipped` with the first non-null skip reason (fallback `"skipped"`; also the zero-results case).
- `TimingProperty`: min `StartedAt` to max `StartedAt + Duration`; omitted when there are no results.
- `StandardOutputProperty`: per step, a header line `"{numbered name} — {Status}"` (numbering via `StepNumbering`, name = `StepResult.DisplayName`), then that step's `StepReporting.OutputLines` indented two spaces.
- Attachments of every step attached to the scenario node.

- [ ] Tests, one per fold row: all passed → passed; one assertion failure among passes → `FailedTestNodeStateProperty` carrying that exception; non-assertion exception → `ErrorTestNodeStateProperty`; timeout plus a later failure → timeout; passed + not-taken → passed; passed + skipped → passed; all skipped with reason `"preflight failed"` → skipped with that reason; zero results → skipped; only synthetic results → skipped.
  Plus: started publishes exactly one in-progress node with uid `"scn"`; step events publish nothing; output contains `"1. step a — Passed"` and `"2. step b — Failed"`; timing spans first start to last end; two scenarios interleaved each get one in-progress and one terminal node.
- [ ] Existing `MtpReportSinkTests` stay green after the helper extraction.
- [ ] `jj commit -m "feat(mtp): report a scenario as one folded test node"`

### Task 5: Wire into the framework and `RaunTestApplication`

**Files:**
- Modify: `src/Raun.Mtp/RaunTestFramework.cs`
- Modify: `src/Raun.Mtp/RaunTestApplication.cs`
- Test: `test/Raun.Mtp.Test/ScenarioGranularityFrameworkTests.cs`
- Modify if present: public API baseline / `PlatformSurfaceTests` expectations.

**Interfaces — consumes:** everything from Tasks 1–4.
**Produces:**
- `RaunTestFramework`: the full internal ctor gains `TestGranularity? testGranularity = null`; new
  `internal RaunTestFramework(TestGranularity? testGranularity, Func<string, string?> environment)` for tests.
  Every ctor path resolves `_granularity` once via `TestGranularityResolver.Resolve` (real environment unless injected).
- `RaunTestApplication.RunAsync(..., int maxParallelSteps = 0, TestGranularity? testGranularity = null)` with XML doc; fix the `<see cref>` in the class remarks.

Behaviour in `Scenario` mode:
- `ReadSelector`: uid list → `WholeScenarioSelector.ForScenarioIds`; tree filter → `WholeScenarioSelector.Widening(new TreeNodeSelector(...))`.
- Discovery publishes `BuildScenarioNode` per definition (preflight included), skipping nulls.
- Execution uses `ScenarioMtpReportSink` in place of `MtpReportSink`; the HTML sink is added as before.

- [ ] Tests (framework built with injected environment): discovery of a 3-step scenario yields exactly one node, uid `"sg-scn"`; run with no filter yields one passed node and each step body ran once (counter); run with uid filter `["sg-scn"]` runs all three steps once; uid filter `["sg-scn:b"]` runs nothing; a failing middle step yields one failed node for the scenario; preflight failing yields scenario node skipped; `testGranularity: Step` with `STRYKER_MUTANT_FILE` set keeps per-step nodes; default framework with `STRYKER_MUTANT_FILE` set yields scenario nodes.
- [ ] Full suite: `dotnet test` green on both target frameworks.
- [ ] `jj commit -m "feat(mtp): scenario granularity under Stryker and as a RaunTestApplication option"`

### Task 6: Docs

**Files:**
- Modify: `docs/reference/running-tests.md` — section "Mutation testing with Stryker": what changes, `RAUN_TEST_GRANULARITY`, the `testGranularity` argument, precedence, HTML report still per step.
- Modify: `README.md` only if it lists `RunAsync` options.

- [ ] `jj commit -m "docs: mutation testing with Stryker and test granularity"`
