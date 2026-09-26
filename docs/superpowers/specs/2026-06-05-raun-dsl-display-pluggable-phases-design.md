# Raun DSL display names, pluggable phases, and a generator safety net

- **Date:** 2026-06-05
- **Status:** Design — awaiting review
- **Scope:** `Raun`, `Raun.Generator`, `Raun.Mtp`, `samples/AppointmentTests`

## Summary

Four related changes, surfaced while experimenting with how Given/When/Then steps read
in the test runner:

1. **Sample step names carry their phase word** — the `AppointmentTests` sample reads
   `Given …` / `When …` / `Then …` in the runner. Sample-only.
2. **`[DisplayName]` on a scenario class** — rename the class node (today the raw type
   name `Scenarios`) to something friendly, reusing the BCL
   `System.ComponentModel.DisplayNameAttribute`.
3. **Pluggable phase markers** — recognise any type implementing a new `Raun.IPhase`
   marker interface as a phase, not just the built-in `Given`/`When`/`Then`.
4. **RAUN000 "Unhandled exception"** — turn an unexpected throw in the generator or
   analyzer into a clean diagnostic instead of a cryptic `CS8785` / `AD0001`.

## Goals

- The runner visually distinguishes Given/When/Then steps (via the step text) and lets a
  class node be named.
- Authors can define their own phase vocabulary (e.g. `Arrange`/`Act`/`Assert`, or
  domain phases) by implementing one marker interface, in any namespace.
- Internal Raun failures surface as actionable `RAUN000` diagnostics.

## Non-goals

- Automatic phase-word prefixing by the framework (considered; the user chose the
  sample-only manual form). The `ScenarioNode.Phase` string remains metadata, not display.
- BDD "And" handling for consecutive same-phase steps.
- Renaming the namespace node or the scenario node (the latter is already `[Scenario("…")]`).

## Verified assumptions (compiler-checked, 2026-06-05)

- A `static class` **cannot** implement an interface → `error CS0714`. Confirmed by
  compiling `public static class Given : IPhase { }` against `net10.0`. This is why the
  built-in markers must change shape for feature 3.
- C# 14 static extension members **do** work on a non-static marker:
  `extension(Given)` + a call site `await Given.AnswerExists()` compiles cleanly when
  `Given` is `public sealed class Given : IPhase { private Given() { } }`
  (`LangVersion=preview`, `net10.0`). So the interface approach is viable with no attribute
  fallback.

---

## Feature 1 — Phase words in the sample step names (sample-only)

Prefix each `[StepName]` template in `samples/AppointmentTests/AppointmentDsl.cs` with its
phase word:

| Block | Now | After |
|---|---|---|
| `Given` | `the database is clean` | `Given the database is clean` |
| `Given` | `patient {name} exists` | `Given patient {name} exists` |
| `Given` | `an available slot exists` | `Given an available slot exists` |
| `Given` | `user {name} exists` | `Given user {name} exists` |
| `When` | `creating an appointment` | `When creating an appointment` |
| `When` | `importing the users` | `When importing the users` |
| `Then` | `the appointment should exist` | `Then the appointment should exist` |
| `Then` | `the import should contain {expected} users` | `Then the import should contain {expected} users` |

Leaves render `1. Given patient Jane exists`, `3. When creating an appointment`, etc. No
framework, generator, or runtime change. **Verify** no test asserts on the old exact
strings (the generator snapshot/`SampleSources` tests use their own inline sources, not this
sample).

---

## Feature 2 — `[DisplayName]` on the scenario class

Reuse `System.ComponentModel.DisplayNameAttribute`, matched by simple name the same way
`[Scenario]`/`[StepName]` are matched today.

**Data flow (mirrors the existing attribute pipeline):**

1. `AttributeReader.ClassDisplayName(INamedTypeSymbol type)` — returns ctor arg[0] of an
   attribute whose `AttributeClass?.Name == "DisplayNameAttribute"`, else `null`.
2. `ScenarioParser.Parse` — already holds `_method.ContainingType`; sets a new
   `ParsedScenario.ClassDisplayName`.
3. `ScenarioEmitter.BuildScenarioBuilder` — emit `Set("ClassDisplayName", Lit(scenario.ClassDisplayName))`
   into the `ScenarioDefinition` initializer.
4. `ScenarioDefinition.ClassDisplayName` — new `string?` init property.
5. `ScenarioTestIdentity.Create(string methodFullName, string scenarioDisplayName, string? classDisplayName)`
   — when `classDisplayName` is non-null/non-empty, use it as `typeName`; otherwise keep the
   current FQN-split behavior. Update both call sites:
   - `src/Raun.Mtp/RaunDiscoverer.cs:57`
   - `src/Raun.Mtp/RaunStepReporter.cs:163`
6. Sample: add `using System.ComponentModel;` and `[DisplayName("Appointment booking")]` to
   `samples/AppointmentTests/Scenarios.cs`'s `Scenarios` class.

**Resulting tree:** `AppointmentTests` (namespace) → `Appointment booking` (class) →
`customer books an appointment` (scenario) → numbered steps.

---

## Feature 3 — Pluggable phase markers via `IPhase`

- New empty marker in `src/Raun/Phases.cs`: `public interface IPhase { }`.
- `Given`/`When`/`Then` change from `static class` to
  `public sealed class X : IPhase { private X() { } }`. They remain non-instantiable;
  call sites (`Given.X()`, `extension(Given)`) are unchanged.
- `SymbolHelpers.PhaseOf` — recognise any receiver whose type implements `Raun.IPhase`
  (scan `type.AllInterfaces` for `Name == "IPhase"` &&
  `ContainingNamespace.ToDisplayString(NoGlobal) == "Raun"`), returning `type.Name` as the
  phase. Built-ins keep working; `public sealed class Arrange : IPhase { }` in any namespace
  is accepted with phase `"Arrange"`.
- The `ScenarioNode.Phase` string stays metadata only (set by parser, emitted into the node,
  never read by scheduler/reporter/display) — so custom phase names cannot affect behavior.
- Analyzer wording: update `Descriptors` RAUN002/004/006 from "Given/When/Then" to
  "a phase marker (a type implementing `Raun.IPhase`)". Logic already routes through
  `PhaseOf`, so no analyzer control-flow change. Update the matching Notes in
  `AnalyzerReleases.Unshipped.md`.

---

## Feature 4 — RAUN000 "Unhandled exception"

Only for genuine exceptions — distinct from `TryParse` returning `null` for unsupported
constructs (those already produce the specific RAUN00x).

- `Descriptors.UnhandledException` = **RAUN000**, `Category = Raun.Usage`, `Error`,
  title "Unhandled exception in Raun generator",
  message `"Raun failed to process a scenario: {0}"` (`{0}` = exception text).
- **Generator** (`ScenarioGenerator`):
  - `Transform` wraps `ScenarioParser.TryParse` in try/catch; on throw, returns an
    error-carrying result (exception text + the method's file/line) rather than a
    `ParsedScenario`. Introduce a small equatable result type carrying either the parsed
    scenario or the error (file path + line + message) so the incremental pipeline stays
    cache-friendly.
  - `RegisterSourceOutput` reports RAUN000 for each error result, and wraps
    `ScenarioEmitter.Emit` / entry-point emission in try/catch → RAUN000 at `Location.None`.
- **Analyzer** (`ScenarioAnalyzer`):
  - Wrap the body of `AnalyzeMethod` in try/catch → report RAUN000.
  - Add `Descriptors.UnhandledException` to `SupportedDiagnostics`.
- Add a RAUN000 row to `AnalyzerReleases.Unshipped.md`.

---

## Testing strategy (behavioral, TDD)

- **Feature 2 — generator:** a scenario class with `[DisplayName("X")]` lowers to
  `ScenarioDefinition.ClassDisplayName == "X"`; absent → `null`
  (`Raun.Generator.Test`).
- **Feature 2 — identity:** `ScenarioTestIdentityTests` — `Create(fqn, scenario, "X")`
  yields `TypeName == "X"`; `Create(fqn, scenario, null)` keeps the FQN-split type.
- **Feature 2 — snapshots:** regenerate the three
  `GeneratorSnapshotTests.*#RaunScenarios.g.verified.cs` (the `ScenarioDefinition`
  initializer gains a `ClassDisplayName` line).
- **Feature 3 — generator:** a scenario over a custom `sealed class Foo : IPhase { }`
  lowers with `Phase == "Foo"`; existing Given/When/Then lowering stays green.
- **Feature 3 — analyzer:** a receiver type that does **not** implement `IPhase` →
  `RAUN004` (`AnalyzerTests`).
- **Feature 4 — analyzer:** an `AnalyzeMethod` that throws is reported as `RAUN000` (inject
  a throwing path via a targeted test seam or a crafted input); RAUN000 appears in
  `SupportedDiagnostics`.
- **Feature 4 — generator:** an emit/parse throw surfaces as a RAUN000 diagnostic rather
  than an unhandled generator exception.
- **Whole build:** `dotnet build` + full test run green; the `AppointmentTests` sample
  discovers and runs with the new tree (`Appointment booking` class node, `Given …` leaves).

## Files touched

| File | Change |
|---|---|
| `samples/AppointmentTests/AppointmentDsl.cs` | Phase words in `[StepName]`s (F1) |
| `samples/AppointmentTests/Scenarios.cs` | `[DisplayName]` on `Scenarios` (F2) |
| `src/Raun/Phases.cs` | `IPhase`; `Given/When/Then` → sealed `: IPhase` (F3) |
| `src/Raun/Model/ScenarioDefinition.cs` | `ClassDisplayName` (F2) |
| `src/Raun.Generator/Lowering/AttributeReader.cs` | `ClassDisplayName(...)` (F2) |
| `src/Raun.Generator/Lowering/Ir.cs` | `ParsedScenario.ClassDisplayName` (F2) |
| `src/Raun.Generator/Lowering/ScenarioParser.cs` | read class display name (F2) |
| `src/Raun.Generator/Lowering/SymbolHelpers.cs` | `PhaseOf` via `IPhase` (F3) |
| `src/Raun.Generator/Emit/ScenarioEmitter.cs` | emit `ClassDisplayName` (F2) |
| `src/Raun.Generator/ScenarioGenerator.cs` | parse/emit try/catch → RAUN000 (F4) |
| `src/Raun.Generator/Analysis/Descriptors.cs` | RAUN000; reword 002/004/006 (F3,F4) |
| `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs` | wrap `AnalyzeMethod`; register RAUN000 (F4) |
| `src/Raun.Generator/AnalyzerReleases.Unshipped.md` | RAUN000 row; reword notes (F3,F4) |
| `src/Raun.Mtp/ScenarioTestIdentity.cs` | `Create` takes class display name (F2) |
| `src/Raun.Mtp/RaunDiscoverer.cs` | pass `ClassDisplayName` (F2) |
| `src/Raun.Mtp/RaunStepReporter.cs` | pass `ClassDisplayName` (F2) |
| Tests across `Raun.Generator.Test`, `Raun.Mtp.Test` | per strategy above |

## Risks

- **Feature 3 shape change** is binary-breaking for `Given/When/Then` (static → sealed). Raun
  is the new MTP-redesign framework (pre-release), so acceptable; call-site source is
  unaffected (verified).
- **Snapshot churn** (Feature 2): expected and regenerated as part of the work.
- **RAUN000 equatability**: the error result type must be value-equatable to keep the
  incremental generator cache healthy; carry primitive fields (file/line/message), not a
  `Location`.
