# Design: span-form `#line` directives for column-accurate step-into debugging

**Status:** Designed, approved. Not implemented.
**Date:** 2026-06-03
**Supersedes:** the *mechanism* in `docs/superpowers/handoffs/2026-06-03-line-directives-handoff.md`. That handoff mapped each step to a bare **line** (`#line N "file"`); this design upgrades to the C# 10 **span** directive (`#line (sl,sc)-(el,ec) charOffset "file"`) for column-accurate mapping. The handoff's repo conventions, edit-site map, and snapshot procedure remain valid reference material.
**Execute with:** superpowers:subagent-driven-development (sequential; main workdir; no worktrees — matches how this repo is run).

---

## 1. Goal & fidelity bar

Raun lowers each `[Scenario]` method into `RaunScenarios.g.cs` (`Raun.Generated.RaunGenerated`): one `Scenario_X()` builder per scenario, each step carrying a `static async (__inputs, __ctx) => { … }` `Invoke` lambda that re-invokes the DSL call. The scheduler runs those lambdas, so **the code that actually executes under the debugger is the generated lambda, not the user's original method body.**

**Goal:** under a debugger, a breakpoint on a step's DSL call (`When.CreateAppointment(patient, slot)`) binds to the developer's **original source span**, the current-statement highlight covers that exact original call, and stepping never descends into generated plumbing.

**Fidelity bar (chosen: maximal).** Map to the precise original **span** (column-accurate), not just a line. The full span is already available at parse time and currently discarded, so the incremental cost is small and the payoff is real — including the LINQ-unroll case where several generated steps derive from one expression and can share a source line.

**Accepted caveats (settled, not open):**
- Only the *call site* maps. Locals won't match — the generated body uses `__inputs.Get<T>(0)`, not the user's `patient`/`slot`. Out of scope.
- Argument columns drift after the call's `(` because args are rewritten (`__inputs.Get<Patient>(0)` vs `patient`). This is unavoidable and is exactly what Razor tolerates; it does not affect the statement-level highlight (see §4).

---

## 2. Why single-line mapping is the right call (the sequence-point principle)

`#line` directives **remap the sequence points the compiler already emits; they do not create new ones.** Whitespace/layout cannot add sequence points either. Stepping granularity is fixed by the IL Roslyn generates for a statement.

For `var __r = await When.CreateAppointment(arg0, arg1);` Roslyn emits **statement-level** sequence points (plus async-state-machine points around the `await`). The argument sub-expressions — plain method calls like `__inputs.Get<T>(0)` — do **not** get their own sequence points.

Consequences:
- **Rendering the call across multiple lines (one arg per line) buys nothing.** Per-argument `#line` directives would map sequence points that don't exist — pure snapshot churn, identical debugging.
- **The "drift" is harmless.** The span directive names the *exact* original span to highlight; the debugger highlights that named span when the statement's sequence point is hit, regardless of generated line length. Drift only affects sub-statement column mapping, and there are no sub-statement sequence points to map.
- **The only way to change granularity** is to hoist each argument into its own statement (`var __a0 = …;`), which *does* create sequence points — but that bloats every step and buys stepping over variable reads, which nobody wants. Rejected.

This principle is verified empirically by the PDB test in §6 (we read which sequence points actually exist), so the design does not rest on assertion alone.

---

## 3. Capture the full span (parser + IR)

`ScenarioParser.Location` (~`ScenarioParser.cs:446`) already computes `node.GetLocation().GetLineSpan()` and discards everything but the start line. Add a span-returning helper; **leave `Location` and the existing `SourceFile`/`SourceLine` exactly as they are** — those feed the runtime `ScenarioNode`/`ScenarioDefinition` model, which stays line-based and untouched.

```csharp
// null → caller omits the directive (same omission rule as the handoff)
static SourceSpan? SpanOf(SyntaxNode node)
{
    var s = node.GetLocation().GetLineSpan();
    if (string.IsNullOrEmpty(s.Path)) return null;
    return new SourceSpan(
        s.Path,
        s.StartLinePosition.Line, s.StartLinePosition.Character,
        s.EndLinePosition.Line,   s.EndLinePosition.Character);   // 0-based; emitter converts
}
```

`SourceSpan` is a small `readonly record struct` in `Ir.cs`:

```csharp
internal readonly record struct SourceSpan(
    string File, int StartLine, int StartChar, int EndLine, int EndChar);
```

Add one nullable field to `ParsedStep`:

```csharp
public SourceSpan? CallSpan { get; init; }
```

Populate it from `SpanOf(invocation)` at the step-construction site (`ScenarioParser.cs:370`), alongside the existing `SourceFile`/`SourceLine`. That is the entire data-model change — additive, isolated to emission, runtime model unaffected.

---

## 4. Emit the span directive (emitter)

**4.1 Baseline.** Default the whole generated file to `#line hidden` by appending it to the raw `Header` const (never passed through `NormalizeWhitespace`), exactly as the handoff specified:

```csharp
const string Header = "// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\n#line hidden\n";
```

Everything not explicitly annotated (node construction, `CreateAll`, module initializer, `FormatDisplayName`, the `Scenario_X()` builder, the `__r`/cast/return boilerplate) inherits this hidden baseline and is never stepped into. Approach *fails safe*: anything we forget to annotate stays hidden rather than leaking into stepping.

**4.2 Bracket the awaited call in `BuildInvokeLambda`.** Produce, per step:

```
#line (77,16)-(77,52) <charOffset> "C:\…\AppointmentTests.cs"
            var __r = await When.CreateAppointment(__inputs.Get<…>(0), __inputs.Get<…>(1));
#line hidden
            return (object?)__r;
```

(Void steps map the bare `await …;` and put `#line hidden` on `return (object?)null;`.)

- **Original span** = `step.CallSpan`, each `LinePosition` converted from Roslyn's 0-based to the directive's 1-based (spike confirms the exact base convention — see §5).
- **`charOffset`** = the generated column of the `When` token, so generated `When.CreateAppointment(` aligns column-for-column with the original. Computed analytically as `indentColumn + prefixLength`, where `prefix` is `"var __r = await "` (result step) or `"await "` (void step), and `indentColumn` is `NormalizeWhitespace`'s deterministic indent for the lambda-body depth (constant across all steps). No second pass, no text measuring.
- **Emission shape:** structured `LineSpanDirectiveTriviaSyntax` as leading trivia on the call statement; structured `LineDirectiveTriviaSyntax(#line hidden)` as leading trivia on the `return`. (`LineSpanDirectiveTrivia` exists in Roslyn 4.0+; our baseline is 5.3.)
- **Omission rule:** `step.CallSpan is null` ⇒ emit **no** span directive (just the `#line hidden` on the return). The snapshot harness parses pathless, so `CallSpan` is null there and the existing snapshots gain only the baseline + per-return `#line hidden` — never a machine-specific path. The populated-path branch is proven separately by a path-bearing harness overload (§6).

**4.3 Mechanism fallback.** If the §5 spike shows structured `LineSpanDirectiveTrivia` does not render with a predictable column under `NormalizeWhitespace`, fall back to building the lambda body from a **controlled raw-text fragment** (directive text + statement, with known indentation) so `charOffset` is deterministic by construction. The emitted directive text is the contract either way.

---

## 5. Spike: validate `charOffset` and rendering (throwaway)

A throwaway test (`test/Raun.Generator.Test/LineSpanSpikeTests.cs`, deleted before the final commit) that builds a one-statement lambda body with a `LineSpanDirectiveTrivia` attached, runs `NormalizeWhitespace(eol:"\n").ToFullString()`, and confirms:

1. The directive renders on its **own line**, compiler-legal, statement intact and not glued to it.
2. The **actual generated column** of the call token equals our computed `indentColumn + prefixLength`.
3. The directive's **base convention** (1-based positions; `charOffset` semantics — offset into the following line) — pin down against a known-good emitted directive.

**Decision recorded in the implementation commit body:**
- All pass → structured `LineSpanDirectiveTrivia` (preferred).
- Column unpredictable / mangled → raw-text fragment fallback (§4.3); note the symptom.

Then delete the spike and return the suite to its prior green count.

---

## 6. Verification (what proves it works)

1. **Compile-success** — a path-bearing harness run (`RunWithPath`, mirroring the existing `Run`, parsing with a fixed `"Scenario.cs"` path) whose generated code is compiled; assert no errors. The C# compiler accepts our span directives with a real path. Strongest *cheap* automated bar.
2. **Snapshots** —
   - 3 existing pathless snapshots re-accepted: change by **exactly** the baseline `#line hidden` + one per-return `#line hidden`. **No `#line (…)` span directive** (pathless ⇒ `CallSpan` null). Any other diff ⇒ regression, stop.
   - 1 new path-bearing snapshot locks the populated-path span-directive shape deterministically (columns derive from the fixed sample source).
3. **PDB sequence-point assertion (promoted to in-scope).** Snapshots prove we *emitted* a directive; only the PDB proves the *columns are right*. Emit with `EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)`, read sequence points via `System.Reflection.Metadata.MetadataReader`, locate the `Invoke` lambda's points, and assert they reference the expected original path and span.
   - **Execution-risk note:** exact-column PDB reading is fiddly/version-brittle. If pinning exact columns proves brittle, demote to asserting the sequence point maps to the original **file + start line** (still far stronger than snapshots); record the demotion in the commit body. Do **not** drop this test entirely.
4. **Full suite green** — the harness compiling generated code + the xUnit acceptance tests prove runtime behavior is unchanged.
5. **Manual debugger checklist** (run once by a human; not automated) — see §8.

---

## 7. Scope / non-goals (YAGNI)

- Do **not** map `FormatDisplayName`, `Scenario_X()`, `CreateAll`, or the module initializer (no one breaks on scenario *construction* or reporting).
- Do **not** change the runtime model (`ScenarioNode`/`ScenarioDefinition`), the analyzer, or runtime types.
- Do **not** hoist arguments into separate statements (rejected in §2).
- Do **not** render the call multi-line (rejected in §2).
- No `/pathmap` handling beyond Roslyn's default. `step.CallSpan.File` is the compiler-derived path (matches the PDB); used verbatim, no relativization.

---

## 8. Manual debugger checklist (human, once)

Against the sample `AppointmentTests` in an IDE:
1. Breakpoint on a `When.CreateAppointment(...)` line in the original `*Tests.cs` → run → binds and hits **on that original call** (not in `RaunScenarios.g.cs`); highlight covers the call span.
2. Step Over / Step Into repeatedly → moves between original DSL call sites; never descends into `RaunScenarios.g.cs` plumbing.
3. Breakpoint on a void step (`Then.AppointmentExists(...)`) → binds to the original call and hits.
4. Locals showing `__r`/`__inputs` instead of the user's names is expected (accepted non-goal).

---

## 9. Repo conventions for the executor

- **Build gate is strict:** `TreatWarningsAsErrors`, `EnableNETAnalyzers`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild`. Every build must report **`0 Warning(s), 0 Error(s)`**. Fix any CA/IDE nit the new helpers raise.
- **Tests:** `dotnet test Raun.slnx --nologo`. The handoff baseline is **92**; this feature adds the path-bearing snapshot + compile-success + PDB sequence-point facts (final count set by the plan). The spike is throwaway and must be gone from the final count.
- **`using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;`** is already imported in `ScenarioEmitter.cs:7`, so `LineSpanDirectiveTrivia`, `LineDirectiveTrivia`, `LineDirectivePosition`, `Trivia`, `Literal`, `Token`, `EndOfLine`, `TriviaList` are unqualified there.
- **Commits:** `jj commit -m "..."` (this repo uses jj). **No `Co-Authored-By` / tooling trailer.**
- **Snapshot re-accept:** Verify writes `*.received.cs` on mismatch (DiffEngine disabled in `VerifyConfig.cs`); diff `received` vs `verified`, confirm the change is exactly expected and nothing else, replace `verified` with `received`, delete `received`, re-run to confirm green. **Never blind-accept.**

### Edit-site map (verified against current code)
- `src/Raun.Generator/Emit/ScenarioEmitter.cs` — `Header` const (L14), `Emit`/`NormalizeWhitespace` (L23/L45/L47), `BuildInvokeLambda` (L216–257). **Primary edit site.**
- `src/Raun.Generator/Lowering/Ir.cs` — add `SourceSpan` struct + `ParsedStep.CallSpan` (near L20–49).
- `src/Raun.Generator/Lowering/ScenarioParser.cs` — add `SpanOf` near `Location` (L446); populate `CallSpan` at step construction (L357–373). Leave `Location`/`SourceLine` as-is.
- `test/Raun.Generator.Test/GeneratorHarness.cs` — `Run` (~L34–64), `RunDriver` (~L67–81). Add path-bearing `RunDriver(source, path)` + `RunWithPath(source, path)` + a PDB-emitting runner. Align with the real `GeneratorResult`/`References` member names.
- `test/Raun.Generator.Test/GeneratorSnapshotTests.cs` — add path-bearing snapshot + compile-success + PDB facts.
- `test/Raun.Generator.Test/Snapshots/` — 3 existing re-accepted + 1 new path-bearing.
