# Timeout Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for native implementation, or superpowers:subagent-driven-development if selected by the user. Steps use checkbox syntax for tracking.

**Goal:** Resolve staff-review finding 2: cancel timed-out work, retain ownership for 60 seconds, and terminate the test host if work survives.

**Architecture:** A scenario-owned watchdog supervises invocation lifetimes and cancellation callbacks independently of scheduler continuations. Scheduler results, cleanup, and RunLoop ownership release wait for safe completion within grace. MTP resolves a single grace option and passes it into the runtime.

**Tech Stack:** .NET 10, C#, existing DAG scheduler and RunLoop, Microsoft.Testing.Platform, xUnit v3, subprocess tests.

**Spec:** [Timeout ownership and process termination](../specs/2026-10-14-timeout-ownership-design.md)

## Global constraints

- Default grace is 60 seconds from the cancellation request; zero grace is allowed; infinite/negative/unsupported durations are rejected.
- Hard termination affects the entire host, exits nonzero, and may leave reports and required cleanup incomplete.
- Arm supervision before user invocation and argument preparation; blocked synchronous work and cancellation callbacks must not defeat the deadline.
- Retain scenario scope, cleanup registrations, and contention ownership until all relevant invocation/cancellation work ends.
- Findings 3–6 remain open; do not add teardown deadlines or change DI disposal in this slice.
- Use the existing attached worktree. Preserve original checkout edits, file line endings, graph assertions, and lockfiles. Never add a project Version.

## Review focus

- Synchronous Invoke or argument preparation blocks before returning a Task: watchdog still cancels and terminates the child host.
- A cancellation callback hangs after the body stops: its scope stays owned, and hard termination remains armed.
- Host cancellation races with a step/scenario timeout: classify the winning cause consistently and publish one terminal result.
- Work finishes during grace and registers late cleanup: cleanup runs only after it ends and includes the late registration.
- A timer outlives a successful run: no delayed process termination after the operation/scenario safely completed.

## Task 1: Safe runtime timeout supervision

**Files:** create `src/Raun/Scheduling/CancellationWatchdog.cs`; modify `src/Raun/Scheduling/ScenarioScheduler.cs`; modify `test/Raun.Test/ScenarioTimeoutTests.cs` and `test/Raun.Test/SchedulerTests.cs`; create `test/Raun.Test/TimeoutOwnershipTests.cs`.

**Interfaces:** append optional `TimeSpan? cancellationGracePeriod = null` to ScenarioScheduler's constructor, preserving existing named arguments. Resolve null to TimeSpan.FromSeconds(60); validate finite supported nonnegative values. Keep watchdog types internal. The watchdog owns a single background thread, registers each operation before its user code starts, records monotonic timeout/cancellation and hard deadlines, tracks actual body and cancellation-callback completion, and disarms only after both complete. Provide an internal fail-closed termination seam for focused tests; production prints a fatal diagnostic and exits nonzero.

- [x] Write a failing barrier-based scenario-timeout regression: register cancellation observation, hold the body open after cancellation, assert no cleanup/RunAsync completion, then release it and preserve failed/TimeoutException, dependent-skip, and passed-teardown assertions. Release all barriers in finally.
- [x] Run focused timeout tests and record the ownership assertion failing on current abandonment.
- [x] Add equivalent step-timeout, late-cleanup registration, cooperative cancellation, callback-lifetime, and completion-race tests. Keep normal graph/merge/guard assertions intact.
- [x] Implement the watchdog and integrate it before user code. Replace abandonment with safely observed draining inside grace, preserving timeout failure even if the body subsequently succeeds. An independent hard deadline must not await observer delivery or cancellation callbacks. Observe late faults without turning timeout into success.
- [x] Preserve host cancellation skip semantics and normal independent-branch behavior. Stop scenario work on scenario timeout. Ensure a per-step wrapper cannot hide a live raw invocation from scenario ownership.
- [x] Run focused scheduler tests until green; validate no armed watchdog survives ordinary successful runs. Commit the runtime change after subprocess termination evidence in Task 3 is green.

## Task 2: Runtime and MTP configuration plus admission proof

**Files:** modify `src/Raun/Running/RunLoop.cs`, `src/Raun.Mtp/RunOptionsProvider.cs`, `src/Raun.Mtp/RaunTestFramework.cs`; create `src/Raun.Mtp/CancellationGrace.cs` and `test/Raun.Mtp.Test/CancellationGraceTests.cs`; add RunLoop ownership tests to `test/Raun.Test/TimeoutOwnershipTests.cs`.

**Interfaces:** append optional `TimeSpan? cancellationGracePeriod = null` to RunLoop; pass it to its default ScenarioScheduler. Custom RunScenario delegates retain responsibility for their own execution lifecycle. Register `--cancellation-grace-ms` with exactly one nonnegative integer milliseconds argument. CancellationGrace resolves the CLI override to a TimeSpan, otherwise returns the 60-second default; it follows ScenarioParallelism's existing resolution pattern. RaunTestFramework passes the result into RunLoop.

- [x] Add failing parser/default/zero/invalid/override tests and an end-to-end option-surface test. Reject values unsupported by the runtime timer/deadline implementation.
- [x] Add a real-DI RunLoop regression with an exclusive resource and a held timed-out body. After cancellation, assert its scoped service is undisposed and the contended successor has not entered; release the body and assert cleanup, disposal, and safe successor admission in order.
- [x] Implement option resolution and constructor wiring; avoid parallel policy implementations in MTP.
- [x] Run focused RunLoop and MTP option tests; confirm existing parallelism/filtering behavior stays green.

## Task 3: Real process termination and release evidence

**Files:** create `test/Raun.Test/TimeoutProcessTests.cs` and an isolated non-test fixture project under `test/Fixtures/TimeoutHost/` (using repository project/package conventions and its lockfile); update `test/Raun.Test/Raun.Test.csproj` to build the fixture without compiling its entry point into tests; update README cleanup/timeouts documentation, the spec, and `docs/reviews/2026-10-14-raun-staff-review.md`.

**Interfaces:** fixture command modes `scenario`, `step`, `synchronous`, `preparation`, `callback`, and `cooperative` run the real production watchdog with short explicit grace. Fixture writes markers for body entry, cancellation, cleanup, scope disposal, successor admission, and completion. Parent tests invoke the fixture assembly directly with dotnet, capture stdout/stderr asynchronously, and independently kill/reap the process tree on a generous parent timeout.

- [x] Write subprocess tests expecting nonzero hard termination and a named fatal diagnostic for the five noncooperative modes; require cancellation markers and absence of early cleanup/disposal/admission markers. The cooperative mode must complete with a timeout failure and safe cleanup without fatal termination.
- [x] Run the fixture tests against the old implementation to establish missing termination/unsafe cleanup; then run against Task 1's implementation and fix only demonstrated gaps.
- [x] Verify competing timeout/host-cancellation paths cannot defer an earlier hard deadline; include stuck cancellation callbacks and synchronous user-code cases. Reap every child on assertion or cancellation failures.
- [x] Document the 60-second default, CLI override, whole-host termination, and potentially incomplete reports/cleanup. Keep teardown-bounding finding 3 explicitly open.
- [x] Obtain final fresh review of ownership, supervision, races, and child-process evidence. Address actionable findings before marking finding 2 complete.
- [x] Run `dotnet build Raun.slnx -c Release` with zero warnings, `dotnet test Raun.slnx -c Release`, and `node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs`; pack all four projects and verify package versions.
- [ ] Record red/green evidence, residual limitations, commit and beta release in the saved review. Commit/push and publish the next beta under the existing authorization; verify the release workflow. Check off finding 2 only after this gate passes.

## Execution recommendation

Native execution in the current session: the tasks share a single lifecycle policy and scheduler integration, so keeping one implementer through the changes reduces coordination. Use one fresh final review after integrated tests. The written plan is ready for user review before implementation.

## Execution record

Native implementation completed. Production termination is verified in real child hosts, so no fake
termination callback was introduced. Scope/disposal/exclusive-admission proof uses real DI integration
tests rather than duplicate marker plumbing in the child fixture. Cancellation callback ownership,
first-cause races, initially cancelled zero-grace runs, and blocked managed shutdown have behavioral
coverage. Staff review cleared the final source. Local gate: zero-warning Release solution build,
1,380 passed / 5 skipped / 0 failed, 73 report checks passed, and four packages packed. Publication and
saved-review resolution remain the final pending step for beta.20.

The fixture must be a solution project as well as a non-assembly project reference: solution builds otherwise remove the Release configuration from the out-of-solution dependency. Clean-fixture reproduction confirmed this; the solution now includes TimeoutHost.

Hard-stop probes explicitly prove body/preparation entry, with five-second entry deadlines and two-second grace to separate scheduler startup from noncooperative execution under CI load. Completed-host probes stay alive beyond grace to prove watchdog disarming.
