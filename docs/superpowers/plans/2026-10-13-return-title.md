# Return-aware StepName Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Bind `{$return:id ?? name}` to the actual producer output, preserving fallback and input snapshots.

**Architecture:** Add an optional completed-title delegate to the existing ScenarioNode seam. Generator records part indices; runtime supplies frozen title and boxed output. HTML/MTP consume the existing StepResult.TitleMessage.

**Tech Stack:** C#/.NET 10, Roslyn SyntaxFactory, xUnit v3, MTP, self-contained HTML.

**Spec:** ../specs/2026-10-13-return-title-design.md

## Global Constraints

- Work in C:/dev/raun-beta5; jj only, no commits/pushes/tags in this task.
- Preserve all existing uncommitted resource snapshot and report UI changes.
- No new required model members; old manually/generated nodes keep current behavior.
- Generator uses SyntaxFactory exclusively.
- Coordinate builds: runtime build first; generator tests depend on the updated runtime assembly.

## Review Focus

- Body/start-observer mutation must not replace existing INPUT snapshots.
- Branch selection can turn provisional Passed into Failed; restore fallback.
- Null output versus empty/zero/false output uses actual null semantics.
- Named/optional arguments, member-path fallback, multiple holes and escaped braces keep grammar predictable.
- Declared object/base/interface return types retain existing resolver semantics; no guessing links from matching text.

### Task 1: Runtime completion seam

Ownership: src/Raun/Model/ScenarioNode.cs, StepNotes.cs, src/Raun/Scheduling/ScenarioScheduler.cs, new test/Raun.Test/Reporting/ReturnTitleRuntimeTests.cs. Optional MTP test-only changes.
Produces: optional FormatCompletedTitleMessage delegate and guarded completion helper.

- [x] Add failing tests for success, null, failure, skip, formatter exception, input snapshot preservation and branch-selection failure.
- [x] Add the optional delegate and guarded completion after successful Invoke; restore initial title on late failure.
- [x] Build/test Raun.Test with zero warnings; notify generator implementer when runtime assembly is ready.

### Task 2: Generator grammar and emission

Ownership: src/Raun.Generator/Lowering/PlaceholderPath.cs, DisplayNameBuilder.cs, Ir.cs, ScenarioParser.cs, Emit/ScenarioEmitter.cs, analyzer only if necessary, related generator tests/snapshots.
Consumes: Task 1 exact delegate signature.

- [x] Add failing binding/analyzer/runtime-generator tests covering approved grammar and misuse.
- [x] Bind `$return` to unwrapped declared result; fallback through existing parameter path rules.
- [x] Render fallback for initial/discovery title and generate completion formatter replacing only marked part indices.
- [x] Test Task<T>/ValueTask<T>, multiple holes, constant/runtime fallback, failures/null/skips and immutable input snapshots.
- [x] Run generator suite after runtime assembly is ready; review and accept only intended snapshots.

### Task 3: Sample, report, documentation and review

Ownership: coordinator; sample AppointmentsDsl.cs, README.md, preview artifacts.

- [x] Change Given template to `Given patient {$return:id ?? name} can reach the API`; keep string input and Patient return.
- [x] Run full Release build/tests and report JS suite.
- [x] Obtain scoped independent review and fix material findings.
- [x] Run Aspire sample with explicit API coverlet configuration, restore/remove temporary config.
- [x] Verify Given/When/Then share canonical Patient IDs and full snapshots; refresh actual report preview and provide link.
