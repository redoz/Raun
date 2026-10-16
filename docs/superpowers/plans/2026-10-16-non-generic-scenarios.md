# Non-generic scenario bases implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development to implement the independent runtime and generator tasks, then verify their integration.

**Goal:** Remove NoWorld from ordinary authoring without replacing the execution model.

**Spec:** [Approved design](../specs/2026-10-16-non-generic-scenarios-design.md).

## Constraints

- Scenarios<TWorld> derives from Scenarios; Phase<TWorld> derives from Phase.
- Preserve the typed-world API and all existing guards. Keep one scope, cache, DI, and scheduler path.
- Common steps can run in a typed suite; explicitly typed steps must match its world.
- Tests first, zero build warnings, no new dependencies.

## Tasks

- [x] Runtime worker: add failing API and scope tests, then split Scenarios/Phase and broaden scope caching to common Phase. Validate typed binding before caching.
- [x] Generator worker: add failing authoring and execution tests, recognize both base forms, allow world-independent steps, retain mismatch and receiver diagnostics.
- [x] Parent: migrate the in-memory sample and current user docs to common bases. Keep examples with actual worlds generic. Verify the starter example.
- [x] Build and run focused and full runtime, generator, and MTP tests; run both samples and report checks. Validate links and diagrams if changed.
- [x] Independent review of API direction, compatibility, wrong-world rejection, and documentation; resolve findings, commit locally.

## Verification results

- Release solution build with ContinuousIntegrationBuild: zero warnings and errors.
- Runtime suite: 538 passed; final scope tests rerun: 5 passed.
- Generator suite: 690 passed, including the base-chain arity regression; existing snapshots unchanged.
- MTP suite: 139 passed.
- Appointment sample: 160 nodes, 155 passed and 5 expected branch skips.
- Existing Aspire sample passed with the generic world API.
- Current documentation starter compiled with zero warnings and errors.
- Resource matrix browser checks passed; 111 local documentation links passed.
- Independent API and compatibility review found no remaining issues.
- Patch whitespace checked; original line endings preserved.
