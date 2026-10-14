# Bounded teardown callbacks

Date: 2026-10-14
Status: approved in chat; implemented and locally verified; release pending
Scope: staff-review finding 3. Findings 4–6 stay open.

## Approved behavior

Each selected cleanup callback gets an independent 60-second deadline, then its own cancellation token
is requested. The existing cancellation grace defaults to another 60 seconds. If the callback and its
cancellation handlers stop during grace, teardown records a callback-specific TimeoutException and
continues in the existing reverse dependency/registration order. Later callbacks get fresh tokens;
a previously cancelled scenario or callback does not suppress required cleanup. Optional callbacks
still follow Run.Always, Run.OnSuccess, and Run.Never. Throwing cleanups still allow subsequent cleanup.

If cleanup or its cancellation callbacks survive grace, the same independent watchdog forcibly kills
the entire host. Cleanup never advances and scope/resource ownership never releases while a callback
is alive. Arm supervision before invoking the delegate, including synchronous work. Deadlines/grace
use real time, including simulated scenario timing. For N selected registrations that return within
grace, total cleanup is bounded by N times (callback deadline + grace), excluding observer/reporting
and disposal work outside callback execution. A fatal stop may leave cleanup and reports incomplete.

## Implementation boundary

Reuse CancellationWatchdog with an independent teardown lifetime and no link to the cancelled scenario
token. Keep a separate immutable context view per callback, sharing the teardown node's output,
resource effects, formatting, scenario metadata, clock, and activity. This prevents a captured old
context from observing the next callback's token. Ambient Current is the same context handed to the
callback. Collect all output on the single teardown node, without duplicating resource observers.

Expose cleanupTimeout on ScenarioScheduler and RunLoop, defaulting to 60 seconds. Add the MTP run option
--cleanup-timeout-ms. Require finite positive timeouts within Int32.MaxValue milliseconds; do not allow
an unbounded cleanup override. CLI resolution uses the runtime default as its single authority.
Callback diagnostics identify registration sequence and registering step, including multiple
registrations from one step. Preserve existing successful cleanup logs and rich message snapshots.

## Alternatives

- One overall teardown deadline: rejected for this fix. The existing design requires independent
  bounded callbacks; a single cancelled token would prevent later required cleanup from getting a
  fair attempt. Per-callback budgets bound a finite registration set while preserving that contract.
- Use the already cancelled scenario token: rejected; required cleanup is needed after cancellation.
- Abandon callbacks on timeout: rejected; recreates finding 2's scope and resource ownership race.
- Mutate one context's token between callbacks: rejected; a captured old context would silently start
  pointing at a different callback's token.
- Duplicate teardown nodes/report schema: unnecessary; callback-specific failures fit existing logs
  and the teardown aggregate exception.

## Verification

Tests first: fresh independent tokens; cooperative callback cancellation with subsequent required
cleanup; cancellation-ignoring callback held during grace with no successor cleanup or scenario return;
callback-handler draining; success/throw/order/policy behavior; rich output and ambient-context sharing;
required cleanup after scenario timeout/host cancellation; runtime/CLI defaults, overrides, and invalid
values. Use existing child-host fixture to prove async, synchronous, and cancellation-handler hangs
terminate without running subsequent cleanup, with entry markers and CI-safe startup budgets. Real
scope/admission tests prove ownership during cleanup grace. Final gate: zero-warning solution build,
full tests, report checks, fresh review, green hosted CI, then the next beta and saved-review resolution.

Local verification: 1,406 passed, 5 skipped, 0 failed (1,411 total); zero-warning Release solution
build; 73 report checks; all four packages packed. The 26 added cases cover runtime deadlines,
CLI configuration, and child-process termination. Staff review found no material blockers.
