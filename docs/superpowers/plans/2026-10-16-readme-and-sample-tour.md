# README and sample tour implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task by task.

**Goal:** Make the README a short introduction with working examples for each feature.

**Architecture:** The README introduces concepts in order of need. Samples contain the runnable examples. Three reference pages own the detailed authoring, resource, and run rules.

**Tech stack:** Markdown, C#, .NET 10, existing source generator and MTP tests.

**Spec:** In-chat design approved by the user on 2026-10-16: a 150–200 line README, core concepts, eight short stages, small snippets linked to samples, detailed rules moved to reference pages.

## Constraints

- Use plain, concise language. No sales language or unnecessary jargon.
- Preserve supported contracts. No runtime or generator changes.
- Reuse the scenario-cases worktree. Do not touch the main checkout, push, or integrate.
- Keep the existing booking scenario's six-node filter result.

## Review focus

- A new reader can run the sample without assembling unrelated fragments.
- Each excerpt matches the linked source.
- References and source links resolve after the move.
- Sample assertions check the behavior they demonstrate.
- Added scenarios preserve discovery and filtering assertions.

## Tasks

- [x] Shorten README.md to the approved discovery journey. Check length, snippets, and links.
- [x] Move detailed rules to docs/reference/scenario-authoring.md, resources.md, and running-tests.md. Correct stale report descriptions.
- [x] Add focused sample examples for alternate representations, key-only tracking, optional titles, manual tracking, scoped services, and capacity tokens. Keep checks in the scenarios.
- [x] Add samples/AppointmentTests/README.md and samples/AspireAppointments/README.md as runnable feature guides. Update stale comments and the MTP expected sample count.
- [x] Build with zero warnings; run the sample and affected MTP tests; check Markdown links and whitespace; obtain an independent review and resolve findings.

## Verification

- Release solution build: 0 warnings, 0 errors.
- Complete starter example compiled with the real generator: 0 warnings, 0 errors.
- MTP tests: 139 passed. Core sample: 140 passed, 5 expected branch skips. Aspire sample: 16 passed.
- 90 local documentation links checked; no broken links or heading anchors.
- Independent review: no unresolved findings.
- Report output path checked with a real run.
- CI locked restores passed for Windows and linux-x64.
- Patched the existing Aspire MessagePack dependency from 2.5.302 to 2.5.303 after NuGet audit blocked restore (GHSA-qhrr-8q5h-9q3h). Aspire unit tests: 7 passed; Aspire sample: 16 passed after the patch.
- Preserved existing OpenTelemetry versions during lock refresh; final dependency review passed.
