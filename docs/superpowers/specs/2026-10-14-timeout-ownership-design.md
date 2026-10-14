# Timeout ownership and process termination

Date: 2026-10-14
Status: released as v0.3.0-beta.20; local and hosted verification passed
Scope: staff-review finding 2. Finding 3 will reuse this policy in a separate change.

## Intent and agreed policy

A timeout must not let a surviving step use disposed services or infrastructure reassigned to another scenario. The user selected cancellation followed by process termination after a grace period: test hosts are short-lived, and bounded termination matters more than recovering an uncooperative operation in the same process.

There is no safe supported way to kill an individual .NET Task. The final action terminates the entire test-host process, including other scenarios and any in-process work. Raun must explain this behavior in its public timeout documentation.

## Evidence and current failure

ScenarioScheduler.RunAsync removes unfinished tasks at the scenario deadline, publishes timeout failures, and runs teardown. RunLoop then disposes the scenario scope and releases contention admission. RunNodeAsync separately abandons the raw invocation on a step timeout. Both paths permit work to survive past the ownership boundary. The existing scenario-timeout regression explicitly expects abandonment and therefore must be replaced with tests of the new contract, preserving its failure, dependent-skip, and teardown assertions on safely drained paths.

The invoke delegate can also execute synchronously before returning a Task. A watchdog installed only after invoking it cannot bound that work. Cancellation callbacks can hang too, so awaiting CancelAsync must not prevent the hard deadline from firing.

## Proposed behavior

1. Start timeout supervision before invoking user code, including argument preparation. Use real elapsed time even when scenario timing is simulated.
2. On a step or scenario deadline, request the appropriate cancellation token. Remember that the deadline won: later success or cancellation cannot turn a timeout failure into a pass or skip.
3. Keep the invocation, context, scope, cleanup registrations, conflict claims, and scenario admission owned throughout a configurable grace period. Never run teardown or dispose the scope while the timed-out invocation is alive.
4. If the operation finishes within grace, observe its completion or exception, retain the timeout failure, and collect its final logs/effects/cleanup registrations. Then perform normal teardown and scope release. A scenario timeout skips remaining scenario work; a step timeout skips its dependents while preserving independent branches after safe termination of the timed-out work.
5. If relevant work remains alive at grace expiry, write a concise fatal diagnostic naming the scenario/step, timeout, and grace period, then terminate the process with a nonzero exit code. Do not wait for observers, report sinks, cancellation callbacks, or cleanup to acknowledge this action. Diagnostic failure must not prevent termination.
6. Host cancellation follows the same ownership rule: request cancellation, drain within grace, then terminate if work ignores it. Cooperative host cancellation keeps its existing skipped outcome. An already cancelled run with no launched work does not terminate the process.

The grace deadline must be supervised independently of the scheduler continuation and must already be armed when cancellation callbacks run. Use a background watchdog thread per active scenario, shared by its operations, so a blocked scheduler or exhausted worker pool cannot indefinitely delay the hard deadline. Use monotonic real time for deadlines. Cancellation callbacks themselves are owned work: body completion does not permit release while its cancellation callbacks are still alive. Supervision ends only after the underlying work has actually completed. A timeout wrapper returning is insufficient when its invocation is still alive. Concurrent step/scenario watchdogs must be idempotent and must not extend an earlier hard deadline.

No timeout is enabled merely by adding a grace setting. Grace only applies after an existing timeout or host cancellation fires. On the fatal path, HTML/TRX output and buffered logs may be incomplete, required cleanup does not run, and external resources may remain. This is an intentional consequence of bounded process termination; the parent test runner receives a failed/crashed host rather than a successful run.

## Configuration and ownership

Default grace: 60 seconds from the cancellation request. Expose a finite nonnegative grace duration through ScenarioScheduler and RunLoop, and `--cancellation-grace-ms` through the existing MTP run-options provider. Zero means no grace. Reject negative, infinite, and unsupported timer durations. CLI overrides the runtime default for an MTP run. Keep one runtime policy authority; MTP only resolves options and passes them through.

Use the default fatal action in production and exercise it in child-process tests. Any internal testing seam for the fatal action must not permit normal release of ownership if a substituted action returns; it must fail closed. Do not add a public user callback that can silently resume execution after the hard deadline.

This change does not repair asynchronous DI disposal (finding 4), add teardown deadlines (finding 3), change resource-key dispatch (finding 5), or split coverage packaging (finding 6). The timeout boundary must leave those future fixes a consistent ownership contract.

## Alternatives considered

- Abandon work and proceed with cleanup: rejected because it recreates the demonstrated scope/resource race. Memory leakage is only one consequence; infrastructure mutation survives too.
- Wait indefinitely until all work finishes: preserves ownership but contradicts the user's bounded-termination policy.
- Quarantine only the affected scope and resource leases: rejected for this slice. Surviving work may use run-level services, singletons, or external resources, and the host can still dispose those while returning. It also cannot ensure a bounded host lifetime.
- Process isolation for each scenario: permits selective termination but requires a new execution/serialization boundary and loses current shared in-process service semantics. Deferred; whole-host termination meets the chosen policy with less architecture.
- Kill a Task or use Thread.Abort: unsupported and unsafe in modern .NET.

## Verification

Use TaskCompletionSource barriers to prove ordering, with finite test-level deadlines and finally blocks that release test operations. Avoid elapsed-time-only tests of ownership.

- Scenario timeout: cancellation observed, operation held inside grace, no teardown or return until release; final timeout failure, dependent skip, and successful teardown.
- Step timeout: same ownership proof; dependent steps never execute against a timed-out output, independent work retains its established behavior.
- Late registration/effects: a grace-period operation can finish registering required cleanup and that cleanup runs after the body ends.
- RunLoop integration: scoped service remains undisposed and an exclusive-resource successor cannot start while timed-out work is alive; both become safe after completion.
- Cooperative timeout and host cancellation: existing failure versus skip semantics retained, no process termination.
- Child processes: noncooperative scenario and step bodies, synchronous blocked invocation/preparation, and blocked cancellation callbacks terminate nonzero after timeout plus grace. Markers prove no early cleanup/disposal or contended-resource reuse. Give subprocess tests an independent parent timeout and always reap child processes.
- Boundary races: completion before deadline stays normal; completion within grace still fails on timeout; competing deadlines do not produce duplicate terminal callbacks or leave armed watchdogs after completion.
- Configuration: default, zero, CLI override, and invalid values; normal runs without timeouts unchanged.
- Before release: zero-warning solution build, full solution tests, report interaction tests, all four packages, final review, and checklist resolution evidence.

## Review notes

The policy was selected by the user in chat. The user selected the 60-second default; the CLI name is `--cancellation-grace-ms`. Process termination intentionally sacrifices complete reports and cleanup when safe cooperative draining fails. Hard termination must be tested outside the unit-test process. Finding 3 remains open until cleanup itself has an independently bounded policy.

## Implementation verification

The independent scenario watchdog, immediate asynchronous token propagation, first-cause classification,
real scope/admission retention, CLI override, and forced host termination are implemented. The final
staff review found no remaining source blockers for finding 2. Regression evidence includes red/green
premature-cleanup, both host/timeout orderings, step-before-scenario timeout, same-scan propagation,
and a child whose managed ProcessExit handler blocked Environment.Exit. Production now uses forced
process termination with a fail-closed fallback, bypassing managed shutdown handlers.

Local Release build: zero warnings/errors. Whole solution: 1,380 passed, 5 skipped, 0 failed (1,385 total).
All 73 report interaction checks pass; all four packages pack successfully. There are 32 additional
regression cases. Finding 3 (independent teardown deadlines) remains open.

Hosted beta.18 verification exposed a fixture build-configuration issue: project references outside
Raun.slnx defaulted to Debug during a Release solution build. After cleaning the local Release fixture,
the same missing-fixture test failure was reproduced. TimeoutHost is now explicitly in the solution;
beta.19 supersedes the unpublished beta.18 tag with this build correction.

Hosted beta.19 fixture execution revealed overly tight 100 ms entry deadlines. Under load, safe
cancellation before body entry produced normal failed/skipped completion, so those cases could not
prove termination of live work. The child probes now use five-second entry deadlines, two-second
grace, and explicit BODY/PREPARATION entry assertions. A separate deterministic delayed-preparation
case verifies safe non-entry completion within grace. Beta.20 is the next publication target; neither
beta.18 nor beta.19 published packages.

Published beta.20 after hosted CI run 38600542209 and release run 38601130858 passed. All four packages are attached to the release. Saved review finding 2 is complete; finding 3 is next.
