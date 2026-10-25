# Unified Coverage Outcomes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every branch kind reports one of four outcomes (covered, partial, uncovered, unknown + reason) end to end, and Coverlet path evidence gives expressions and guards real outcomes.

**Architecture:** `CoverageResolver` keeps producing `EvidenceStatus` (two new values). One internal mapping, `CoverageOutcome.Of`, turns a status into an outcome key and reason; both the per-branch source model and the per-category counters use it. The report template renders only the four outcomes.

**Tech Stack:** C# / .NET 10+11, Roslyn, xUnit v3 (MTP), System.Text.Json; vanilla JS report template tested with `node --test` and Playwright.

**Spec:** `docs/superpowers/specs/2026-10-25-unified-coverage-outcomes-design.md`

## Global Constraints

- Outcome keys exactly `covered`, `partial`, `uncovered`, `unknown`; labels "Covered", "Partly covered", "Not covered", "Can't tell"; display order covered, partial, uncovered, unknown.
- Reasons exactly `expression`, `filter`, `shared-range`, `unresolved`, `library`; null unless outcome is `unknown`.
- `ReportCoverageCounts` = `Inventory`, `Covered`, `Partial`, `Uncovered`, `Unknown` only. Measurable = covered + partial + uncovered.
- `CoverletBranchEvidence.PathHits` pairing rule is not changed.
- Repo rules: TreatWarningsAsErrors; jj only (no git mutations); CRLF files stay CRLF (use the Edit tool or `perl -pi`, never Git Bash `sed -i`); commit messages without tooling trailers and never containing the literal skip-ci marker.

## Review Focus

- A guard whose condition range is missing from the XML (e.g. condition shares a range with the throw): expect `unknown`/`unresolved`, never `uncovered`. Test in Task 1.
- An expression inside a guard's condition (`if (x?.Length == 0) throw`): two branches share one range; a partial range must give both `shared-range`. Test in Task 1.
- Old report JSON (pre-change counters) loaded by the new template: no `NaN`/`Infinity`/`undefined` text anywhere. Test in Task 3.
- A category whose branches are all `unknown`: score shows "—", rank sorts as unverified, not 0% or 100%. Test in Task 3.
- Coverlet modules without frequency counts: outcome still decided, path counts not displayed. Test in Task 2.

---

### Task 1: Resolver evidence (Coverlet expressions and guards, guard conditions, shared ranges)

**Files:**
- Modify: `src/Raun.Coverage/MicrosoftXml/CoverageObservations.cs` (enum `EvidenceStatus`)
- Modify: `src/Raun.Coverage/MicrosoftXml/CoverageResolver.cs`
- Test: `test/Raun.Coverage.Test/CoverageResolverTests.cs`, `test/Raun.Coverage.Test/CoverletReaderTests.cs`

**Interfaces:**
- Produces: `EvidenceStatus.Untriggered` (guard condition ran, throw did not) and `EvidenceStatus.SharedRange` (partly executed range shared with another inventoried branch), appended to the enum.

- [ ] **Step 1: Write failing tests**

In `CoverletReaderTests`, extend `Unique_decision_paths_resolve_without_assigning_true_false_labels` into a theory over source as well, using single-decision sources only: `"class C { int M(bool b) { return b ? 1 : 2; } }"` (Expression) and `"class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }"` (Guard), each with path hits (4,0)→Incomplete, (1,1)→Complete, (0,0)→NotReached.

In `CoverageResolverTests` add:

```csharp
[Fact]
public void Guards_score_the_rejection_from_condition_and_throw()
{
    var guard = Guard("class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }");
    var condition = ((IfStatementSyntax)guard.Syntax).Condition.GetLocation().GetLineSpan();
    var probe = guard with { Outcomes = [guard.Outcomes[0], condition] };
    Assert.Equal(EvidenceStatus.Exercised, CoverageResolver.Resolve(guard, Read(Xml(probe, "yes", "yes"))));
    Assert.Equal(EvidenceStatus.Untriggered, CoverageResolver.Resolve(guard, Read(Xml(probe, "no", "yes"))));
    Assert.Equal(EvidenceStatus.NotReached, CoverageResolver.Resolve(guard, Read(Xml(probe, "no", "no"))));
}

[Fact]
public void A_guard_without_resolvable_condition_evidence_is_unknown_not_unreached()
{
    var guard = Guard("class C { void M(string s) { if (s.Length == 0) throw new System.ArgumentException(); } }");
    Assert.Equal(EvidenceStatus.Unknown, Resolve(guard, "no"));   // throw range only, no condition range
}

[Fact]
public void A_partly_executed_range_shared_by_two_branches_is_ambiguous_for_both()
{
    var compilation = TestCompilation.Create("class C { void M(string s) { if (s?.Length == 0) throw new System.ArgumentException(); } }");
    var branches = BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken);
    var expression = Assert.Single(branches, b => b.Measurement == MeasurementKind.Expression);
    Assert.Equal(EvidenceStatus.SharedRange, Resolve(expression, "partial"));
    var lone = Assert.Single(BranchInventoryBuilder.Build(TestCompilation.Create("class C { string M(string s) => s ?? \"x\"; }"),
        new CollectorScope(), TestContext.Current.CancellationToken), b => b.Measurement == MeasurementKind.Expression);
    Assert.Equal(EvidenceStatus.Partial, Resolve(lone, "partial"));
}
```

Add helper `private static InventoryBranch Guard(string source)` = the single branch with `Measurement == MeasurementKind.Guard`. Update `Guards_use_throw_hits_...`: the guard row now expects `Unknown` for throw-only evidence (rename the test to `Helpers_are_unmeasurable_and_expressions_are_coarse_without_paths`, keeping the helper and expression assertions). The lone-expression source must yield exactly one branch: use `"class C { string M(string s) => s ?? \"x\"; }"` and select it with `b.Measurement == MeasurementKind.Expression`.

- [ ] **Step 2: Run, verify the new tests fail**

Run: `dotnet test --project test/Raun.Coverage.Test/Raun.Coverage.Test.csproj -f net11.0 -- --filter-class Raun.Coverage.Test.CoverageResolverTests --filter-class Raun.Coverage.Test.CoverletReaderTests`
Expected: compile error on `EvidenceStatus.Untriggered`/`SharedRange`.

- [ ] **Step 3: Implement in `CoverageResolver.Resolve`**

- Coverlet: `pathHits.Length > 1 && branch.Measurement is Decision or Expression or Guard` → all zero `NotReached`, all positive `Complete`, else `Incomplete` (one rule, the Expression special case goes).
- Guard (no paths): throw hit `Yes` → `Exercised`; otherwise match the `if` condition span: `Yes` → `Untriggered`, `No` → `NotReached`, anything else (null/partial) → `Unknown`.
- Expression/Filter `Partial`: if any other inventoriable branch node (reuse the node predicate from `CoverletBranchEvidence`, extracted to an `internal static bool IsBranchNode(SyntaxNode)` there) lies inside the matched smallest range's span, return `SharedRange`, else `Partial`. `Match` gains an overload returning the matched range so the span is available.

- [ ] **Step 4: Run, verify pass** — same command, both TFMs (`-f` omitted). Expected: all pass.

- [ ] **Step 5: Commit** — `jj commit -m "feat(coverage): Coverlet paths decide expressions and guards; guards scored on rejection"`

---

### Task 2: One outcome mapping, the new counters and branch reasons

**Files:**
- Create: `src/Raun.Coverage/Analysis/CoverageOutcome.cs`
- Modify: `src/Raun/Reporting/Coverage/ReportCoverage.cs` (`ReportCoverageCounts`, `ReportCoverageBranch`)
- Modify: `src/Raun.Coverage/Analysis/ClassifiedCoverageAnalyzer.cs` (`Summarize`)
- Modify: `src/Raun.Coverage/Analysis/CoverageSourceBuilder.cs` (remove private `Outcome`)
- Test: `test/Raun.Coverage.Test/CoverageOutcomeTests.cs` (new), `CoverageSummaryTests.cs`, `CoverageSourceTests.cs`

**Interfaces:**
- Consumes: `EvidenceStatus` incl. `Untriggered`, `SharedRange` (Task 1).
- Produces: `internal static class CoverageOutcome { public static (string Outcome, string? Reason) Of(EvidenceStatus status); }`; `ReportCoverageCounts { int Inventory, Covered, Partial, Uncovered, Unknown }`; `ReportCoverageBranch(int Start, int Length, BranchCategory? Category, string Outcome) { string? Reason; IReadOnlyList<long> PathHits; }`.

- [ ] **Step 1: Write failing tests**

```csharp
public sealed class CoverageOutcomeTests
{
    [Theory]
    [InlineData(EvidenceStatus.Complete, "covered", null)]
    [InlineData(EvidenceStatus.Exercised, "covered", null)]
    [InlineData(EvidenceStatus.Incomplete, "partial", null)]
    [InlineData(EvidenceStatus.Partial, "partial", null)]
    [InlineData(EvidenceStatus.Untriggered, "partial", null)]
    [InlineData(EvidenceStatus.NotReached, "uncovered", null)]
    [InlineData(EvidenceStatus.Reached, "unknown", "expression")]
    [InlineData(EvidenceStatus.Evaluated, "unknown", "filter")]
    [InlineData(EvidenceStatus.SharedRange, "unknown", "shared-range")]
    [InlineData(EvidenceStatus.Unknown, "unknown", "unresolved")]
    [InlineData(EvidenceStatus.Unmeasurable, "unknown", "library")]
    public void Every_status_maps_to_one_outcome(EvidenceStatus status, string outcome, string? reason)
        => Assert.Equal((outcome, reason), CoverageOutcome.Of(status));

    [Fact]
    public void The_mapping_covers_the_whole_enum()
        => Assert.All(Enum.GetValues<EvidenceStatus>(), status => Assert.Contains(CoverageOutcome.Of(status).Outcome,
            new[] { "covered", "partial", "uncovered", "unknown" }));
}
```

Rewrite `CoverageSummaryTests` to assert, for a mixed set of `AnalyzedBranch` facts in one category (one each of Complete, Exercised, Incomplete, Untriggered, NotReached, Reached, Evaluated, Unknown, Unmeasurable): `Covered == 2, Partial == 2, Uncovered == 1, Unknown == 4, Inventory == 9`, and `Inventory == Covered + Partial + Uncovered + Unknown`. In `CoverageSourceTests` replace `"missed"` with `"uncovered"`, and assert an `Evaluated` branch carries `Reason == "filter"` and a `Complete` one `Reason == null`. Add to `CoverletReaderTests.Unique_decision_paths_...`: a module with `HasFrequencyCounts = false` still resolves the same status, and its snippet's `PathHits` is empty.

- [ ] **Step 2: Run, verify failure** — `dotnet test --project test/Raun.Coverage.Test/Raun.Coverage.Test.csproj -f net11.0`. Expected: compile errors (`CoverageOutcome`, new counters).

- [ ] **Step 3: Implement**

`CoverageOutcome.Of` is an exhaustive switch per the table (throw `ArgumentOutOfRangeException` for undefined values). `Summarize` counts by `CoverageOutcome.Of(branch.Status).Outcome`. `CoverageSourceBuilder` sets `Outcome`/`Reason` from it. Replace the old `ReportCoverageCounts` members; doc comment: "Outcome counters; measurable = covered + partial + uncovered." `ReportCoverageBranch.Reason` doc: "Why the outcome is unknown: expression, filter, shared-range, unresolved or library."

- [ ] **Step 4: Run, verify pass** — `dotnet build Raun.slnx -c Release` then `dotnet test --project test/Raun.Coverage.Test/Raun.Coverage.Test.csproj`. Expected: pass on both TFMs. (`Raun.Test` JS-dependent tests may fail until Task 3; C# compile must be clean.)

- [ ] **Step 5: Commit** — `jj commit -m "feat(coverage)!: one outcome set for every branch kind"`

---

### Task 3: The report's Coverage view on four outcomes

**Files:**
- Modify: `src/Raun/Reporting/Html/report-template.html` (coverage functions and CSS)
- Test: `test/Raun.Test/Reporting/coverage-view.test.cjs`, `test/Raun.Test/Reporting/CoverageRenderingTests.cs`, `test/Browser/coverage-view.cjs`

**Interfaces:**
- Consumes: counts `{inventory, covered, partial, uncovered, unknown}`; branch `{outcome, reason, pathHits}` (Task 2, camelCase JSON).
- Produces: JS constant `COVERAGE_OUTCOMES = [{key:"covered",label:"Covered"},{key:"partial",label:"Partly covered"},{key:"uncovered",label:"Not covered"},{key:"unknown",label:"Can't tell"}]` and `COVERAGE_REASONS` (reason → explanation text, reusing today's per-kind explanation strings: expression = the `??`/`?.`/`&&` text, filter = "The exception filter ran. Its true and false outcomes cannot be determined from this coverage data.", library = the library-check text, unresolved = the "Execution data can exist…" text, shared-range = "Several branches share this source range, so partial execution cannot be attributed to this one.").

- [ ] **Step 1: Rewrite the JS tests to the new vocabulary (failing)**

In `coverage-view.test.cjs`, replace the per-kind tests (lines ~34–100, 133, 265–290, 382–397) with outcome tests, keeping the harness. Required assertions:
- `coverageFlow` for categories with mixed kinds yields outcome nodes ⊆ the four keys, in `COVERAGE_OUTCOMES` order, and per-kind links sum to the kind's inventory.
- `coverageSegments(counts)` returns segments keyed `covered`/`partial`/`uncovered`/`unknown` with those counts, zero-count segments omitted.
- `coverageRank`: fraction = covered/measurable; worse fraction first; ties by more missing (partial+uncovered), then names; all-unknown → `family:"unverified"`, `fraction:null`.
- Row cells and `buildCoverageScope` heading read "N of M covered"; row "Not covered" and "Partly covered" numbers come from `uncovered` and `partial`.
- Run header: "Logical" = round(100·covered/measurable) of category 4; tooltip contains "of M logic branches covered" and no longer contains "excluded"; with measurable 0 the number is "—".
- Band selection `{from:"kind:4", key:"partial"}` filters methods/snippets by `branch.outcome === "partial"`.
- A source line's title for an `unknown` branch with `reason:"filter"` contains the filter explanation.
- Old-format counters (`{decisionMeasurable:3, complete:1}`) render no `NaN`, `Infinity` or `undefined` text.

In `CoverageRenderingTests` update any asserted count names to the new ones. In `test/Browser/coverage-view.cjs` update selectors/labels that name old outcomes (e.g. "All decision paths tested", "Not reached") to the four labels.

- [ ] **Step 2: Run, verify failure** — `node --test test/Raun.Test/Reporting/coverage-view.test.cjs`. Expected: failures on outcome keys/labels.

- [ ] **Step 3: Implement in `report-template.html`**

Replace `coverageFlow` outcomes, `coverageRank`, `coverageSegments`, `coverageLabels`, `coverageLogicBar` and its score text, the row number cells, `coverageColumns` (`logic` = covered/measurable of category 4; `missed` = `uncovered`; `partial` = `partial`; headers "Logic covered", "Not covered", "Partly covered"), the `buildCoverageScope` heading, `buildRunHeader` score and tooltip, and the source-line tooltip (kind → outcome label, + `COVERAGE_REASONS[reason]` when unknown). Keep the `coverageProfile` families loop only if it still has meaning; otherwise one bar + labels. CSS: classes `coverage-covered`, `coverage-partial`, `coverage-uncovered`, `coverage-unknown` (hatched, from the existing helper pattern); delete the removed classes listed in the spec. Every count read uses `|| 0`.

- [ ] **Step 4: Run, verify pass**

Run: `node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs` → all pass.
Run: `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release` → pass.
Run (browser): `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -c Release -- --report-html --results-directory artifacts/matrix-sample`, then in `test/Browser`: `npm ci && npx playwright install chromium`, then `node test/Browser/coverage-view.cjs artifacts/matrix-sample/raun-report.html` → passes.

- [ ] **Step 5: Commit** — `jj commit -m "feat(report): coverage view speaks four outcomes"`

---

### Task 4: Package consumption, docs, full verification

**Files:**
- Modify: `test/Raun.PackageTests/PackageConsumer.cs` (`VerifyCoverage`)
- Modify: `docs/reference/running-tests.md` (coverage paragraph)

- [ ] **Step 1:** `VerifyCoverage` asserts the new counters: `inventory > 0`, `inventory == covered + partial + uncovered + unknown`, and `covered + partial > 0` (the sample exercises real branches). Drop checks on removed names.
- [ ] **Step 2:** In `running-tests.md`, after "The report shows coverage for logical branches and for all branches.", add: "Every branch has one outcome: covered, partly covered, not covered, or can't tell. Argument checks count as covered once a test triggers them. With Coverlet, expressions such as `??` and `?:` get exact outcomes; Microsoft coverage XML can only show that they ran."
- [ ] **Step 3: Full verification (CI-equivalent)**: `dotnet restore Raun.slnx --locked-mode`; `dotnet build Raun.slnx -c Release --no-restore`; `dotnet test Raun.slnx -c Release --no-build`; both node tests; the four browser scripts on the sample report; pack the four packages to `artifacts/`; `dotnet restore test/Raun.PackageTests/Raun.PackageTests.csproj --locked-mode`, build, and `dotnet run --project test/Raun.PackageTests/Raun.PackageTests.csproj -c Release --no-build`. Expected: all green.
- [ ] **Step 4: Commit** — `jj commit -m "test(package): coverage consumption checks the outcome counters; docs"`
