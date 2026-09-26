# Handoff: span-form `#line` debug mapping

**Status:** Designed + planned + **execution mode decided**. Not implemented. Ready to execute.
**Date:** 2026-06-03
**Decision (final):** execute with **superpowers:subagent-driven-development** — one fresh subagent per plan task, two-stage review between tasks, green + `jj commit` at each task boundary. Main workdir, **no worktrees** (matches how this repo is run).

**Authoritative documents (read these first, in order):**
1. **Spec / design:** `docs/superpowers/specs/2026-06-03-line-span-mapping-design.md` — the decisions and why.
2. **Plan (source of truth for execution):** `docs/superpowers/plans/2026-06-03-line-span-mapping.md` — task-by-task, with exact code, commands, and expected output. **Do the work from the plan; this handoff only orients you.**
3. **Superseded predecessor:** `docs/superpowers/handoffs/2026-06-03-line-directives-handoff.md` — the original line-only design. Its mechanism is replaced by span mapping; its repo conventions and edit-site map remain valid reference. (The plan's final task adds a supersede note to it.)

---

## 1. What & why (one paragraph)

Raun lowers each `[Scenario]` method into `RaunScenarios.g.cs`, where each step's DSL call becomes a `static async (__inputs, __ctx) => { var __r = await When.CreateAppointment(...); return (object?)__r; }` lambda that the scheduler runs. Today, debugging steps through that generated file. **Goal:** emit C# 10 **span-form** `#line` directives so a breakpoint on a step's DSL call binds to the developer's **exact original call span** (column-accurate), the current-statement highlight covers that original call, and stepping never descends into generated plumbing. The generated file defaults to `#line hidden`; only the awaited call statement in each `Invoke` lambda is bracketed with a `#line (sl,sc)-(el,ec) charOffset "file"` directive mapped to the original invocation; returns stay hidden.

---

## 2. Decisions already settled — do NOT re-litigate

- **Span mapping, not line-only** (maximal fidelity; the original span is already captured at parse time and was being discarded).
- **Single-line mapping** to the original invocation expression (`receiver → )`). Multi-line / one-arg-per-line and arg-hoisting were considered and **rejected**: `#line` remaps the sequence points the compiler already emits; it does not create them. A single `await Call(...)` statement gets statement-level sequence points, so layout changes nothing, and hoisting buys only stepping over variable reads (unwanted).
- **`charOffset` = the generated lambda-body indent (≈20)**, and the directive's start position = the **original invocation start**. This aligns the generated statement start (`var`/`await`) onto the original call, so the statement's sequence point lands on the call. (The spec's first draft used `indent + prefixLen`; that anchored the wrong token — the plan pins the corrected formula, calibrated against real PDB output.)
- **Mechanism:** structured `LineSpanDirectiveTriviaSyntax` (primary). Controlled raw-text fragment is the pre-approved fallback **only if** the Task 1 spike shows `NormalizeWhitespace` renders the trivia unpredictably.
- **Runtime model untouched:** `ScenarioNode`/`ScenarioDefinition` stay line-based (`SourceFile`/`SourceLine`). Span info (`ParsedStep.CallSpan`) is added **solely** for emission.
- **PDB sequence-point test is in-scope** (promoted from "optional follow-up"): for column fidelity, only the PDB proves the columns are right — snapshots only prove we emitted a directive.
- **.NET 10+ only.** No old-Roslyn / old-LangVersion compatibility concerns. Span-form `#line` (C# 10+) and `LineSpanDirectiveTriviaSyntax` (Roslyn 4.0+) are both well within our net10 / Roslyn-5.3 baseline.
- **Omission rule:** pathless input (`CallSpan is null`) emits **no** span directive — keeps the existing pathless snapshots free of machine-specific paths.

---

## 3. How to run it (the subagent workflow)

Invoke **superpowers:subagent-driven-development** and execute the plan's tasks **in order**:

- **One fresh subagent per task.** Give it: the plan path, the specific task number, and the standing gates (below). It implements only that task's steps.
- **Two-stage review between tasks:** (1) you verify the task's exit criteria (build `0 Warning(s), 0 Error(s)`; the stated passing test count; no stray `*.received.cs`); (2) confirm the diff matches the plan before the subagent's `jj commit`. Then dispatch the next.
- **Task 1 (spike) is throwaway** — the subagent creates it, records the rendering/base-convention findings, **deletes it, and does not commit**. Carry its findings forward into Task 4 (and the Task 4 commit body).
- **Task 4 has a calibration loop** (`charOffset`): the PDB fidelity test prints the actual sequence points; the subagent adjusts `LambdaBodyIndent` by the observed column delta until the call maps exactly. This is expected, not a failure.
- **Snapshots:** never blind-accept. Diff `*.received.cs` vs `*.verified.cs`, confirm the change is exactly what the plan predicts, then promote.

### Standing gates for every subagent
- Build: `dotnet build Raun.slnx --nologo` → **`0 Warning(s), 0 Error(s)`** (warnings-as-errors, full analyzers; fix CA/IDE nits).
- Tests: `dotnet test Raun.slnx --nologo`. Baseline **92**; walks 92 → 93 (Task 2) → 95 (Task 4) → 96 (Task 5).
- Commits: `jj commit -m "..."`. **No `Co-Authored-By` / tooling trailer.** One commit per task (Task 1 commits nothing).

---

## 4. Current git state

On top of `b733f8f` (old handoff), this work has added two doc commits (jj):
- `7f0a2957` — design/spec.
- `377e8a70` — implementation plan.
- (this handoff will be the next commit.)

Working copy is otherwise empty — **no production code or tests have been written yet.** A fresh agent starts at the plan's Task 1.

---

## 5. Risks / watch-items

- **Structured trivia × `NormalizeWhitespace`** — the one real unknown, de-risked by the Task 1 spike before any emitter change. Fallback (raw-text fragment) is pre-approved.
- **`charOffset` base convention** — 1-based positions vs `charOffset` semantics differ subtly; the spike pins it and the PDB test verifies the end-to-end mapping, so a wrong value cannot ship silently.
- **PDB reading brittleness** — if exact-column assertions prove version-brittle, the plan permits demoting to file+start-line (still far stronger than snapshots); record any demotion in the commit body. Do not drop the PDB test.
- **Manual debugger checklist (plan Task 5 Step 7)** is the only confirmation of the *actual* IDE experience — it requires a human and is not automated.

---

## 6. Definition of done

- `dotnet test Raun.slnx --nologo` → **96 passing**; `dotnet build Raun.slnx --nologo` → `0 Warning(s), 0 Error(s)`.
- 3 pathless snapshots contain only `#line hidden` (no `#line (`); the path-bearing snapshot contains the 4 span directives.
- PDB fidelity test green (call maps column-accurately to the original span; nothing visible in the generated file); compile-success test green.
- Spike deleted; spike outcome + calibrated `charOffset` recorded in the Task 4 commit body.
- Old handoff carries the supersede note.
- Manual debugger checklist run by a human (tracked separately; not a code gate).
