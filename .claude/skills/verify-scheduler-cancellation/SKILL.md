---
name: verify-scheduler-cancellation
description: Model-check how Raun's scheduler decides why a running step stopped (run cancelled, scenario timeout, step timeout). Use when a change touches src/Raun/Scheduling/CancellationWatchdog.cs or the cancellation/timeout parts of ScenarioScheduler.RunNodeAsync, ApplyFinishedAsync or RunAsync's host registration, or when a cancellation or timeout test flakes.
---

# Verify scheduler cancellation

`verification/scheduler-cancellation/SchedulerCancellation.tla` is a TLA+ model of the cancellation watchdog and the step that it stops. TLC checks every interleaving of these: the test platform cancelling the run, the two deadlines passing, the watchdog thread's pass, the step body, and the scheduler applying the result. A flaky test only reaches some of those interleavings.

## The rules

| Rule | In plain words | Harm when broken |
|---|---|---|
| `NeverFailedForCancellation` | A step that stopped because the run or the scenario was cancelled is reported skipped. It is never reported failed. | A cancelled run reports a failure, with a `TaskCanceledException`, that the scenario did not cause. |
| `TimeoutHasOneOwner` | When both deadlines pass, the deadline that passed first owns the timeout. On a tie the scenario owns it. | The report blames the wrong timeout, and the reader tunes the wrong value. |

## Does my change touch it?

The model covers this code. If your diff changes any of it, run the check.

| Code | Model action |
|---|---|
| `CancellationWatchdog.Supervise`, the pass under `gate`, its one `now` reading and its one `runCancelled` reading | `PassStarts` |
| `CancellationWatchdog.TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios`, `Overdue` | `TimeOutSteps` |
| `CancellationWatchdog.Expire` on the scenario, then on each other operation | `ExpireScenario`, `ExpireStep` |
| `Operation.RequestCancellation`, including the scenario's cascade | `CancelStep`, `CancelScenario` |
| `Operation.CompleteAsync`, its two locks | `CompleteMarks`, `CompleteFinishes` |
| `ScenarioScheduler.RunAsync`: `hostRegistration` (`cancellationToken.Register(() => scenarioWatch.RequestCancellation())`) | `HostCancels`, `HostCallbackRuns` |
| `ScenarioScheduler.RunNodeAsync`: `operationWatch.CompleteAsync` in the inner `finally`, the checks after the body returns, and the `catch (OperationCanceledException) when (...)` filter | `BodyReturns`, `BodyThrowsOnCancel`, `Classify` |
| `ScenarioScheduler.ApplyFinishedAsync`: `scenarioWatch.TimedOut && outcome.Canceled && !outcome.StepTimedOut` | `Apply` |

**Sync commit: `3738035a`.** At that commit the model was confirmed to describe the code above. To see what changed since then, run `jj diff --from 3738035a --to @ -- src/Raun/Scheduling/CancellationWatchdog.cs src/Raun/Scheduling/ScenarioScheduler.cs`. Move the sync commit forward only after you have read the covered code at the new commit and confirmed that every row above still matches it.

## Run it

You need Java 11 or later, and TLC (`tla2tools.jar` v1.7.4) at `~/.cache/tlaplus/tla2tools-1.7.4.jar`, or set `TLA2TOOLS` to point at it. If TLC is missing, the script prints where to get it and the sha256 to check. Downloading TLC is the user's call, so ask before you fetch it.

```sh
bash verification/scheduler-cancellation/check.sh
```

The script takes about 2 seconds. It checks each rule on its own and prints `HOLDS`, `BREAKS` with the shortest path as a list of model actions, or `ERROR`. It exits 0 when every rule holds, 1 when a rule breaks, and 2 when TLC is missing or errored.

Read each result with the `/statechart` skill. Run it on `SchedulerCancellation.tla` with `SchedulerCancellation.cfg` and the view `verification/scheduler-cancellation/view.ts`. It tells each broken rule as its shortest path in plain words, and when every rule holds, it draws how a step moves between states. As of 3738035a, `/statechart` does not run on Windows: its script spawns `/bin/sh`. On Windows, read the action list that `check.sh` prints against the table above.

## Expected result at the sync commit

```
HOLDS  NeverFailedForCancellation (4153 distinct states found)
HOLDS  TimeoutHasOneOwner (4153 distinct states found)
```

`NeverFailedForCancellation` broke on the code before the sync commit, and the CI flake in `StepClassTests.A_cancelled_scenario_still_disposes_its_world` (`Assert.Null() Failure ... TaskCanceledException ... at Clinic.AppointmentActions.Hangs`) was that break:

1. The watchdog starts a pass and checks the scenario. The host token is not cancelled yet, so the scenario stays live.
2. The test platform cancels the run. `CancellationTokenSource` sets the token's flag without taking the watchdog's `gate`.
3. In the same pass, `Expire` on the step reads the host token again and finds it cancelled. It cancels the step's token, with `timedOut: false`, and leaves the scenario token live.
4. The step body throws `TaskCanceledException`. `CompleteAsync` runs, and then the catch filter `scenarioToken.IsCancellationRequested && !operationWatch.TimedOut` is false.
5. The step is reported **Failed** with the cancellation exception. The host callback or the next pass cancels the scenario, but too late.

The fix reads the host token once per pass, as `runCancelled` next to `now`, and passes it to `TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios` and to `Expire`. In the model, `PassStarts` records it as `snap.host`. `TimeoutOwnershipTests.A_run_cancelled_while_the_watchdog_looks_never_stops_a_step_before_its_scenario` replays this path in the real watchdog, so it fails on every run when the fix is undone.

## Real bug or modeling error?

When a rule breaks, walk the printed path against the code in the table.

- **Real bug:** each action on the path is something the code can do in that order. Check especially that each span the model treats as one action runs under `gate` in the code, or is a single flag read. Report it in the code's words, as in the numbered list above. Do not weaken a rule or the model so that it passes, and do not change product code unless the user asks.
- **Modeling error:** some action on the path does something the code cannot do. For example, it does two things the code does in separate lock spans, it reads a value that the code reads at another time, or it misses a guard that the code has. Fix the model so that it matches the code. Then show that the fixed model still breaks on the broken variants under "Able to fail".

## When you change the covered code on purpose

Change the model in the same change. Each action keeps a comment that names the code it stands for. Keep that comment true, and keep one action for each lock span or flag read. Add a variable when the code gains state that the rules depend on. Run `check.sh`. Then move the sync commit, and update the expected result above.

## Able to fail

Each rule was seen to break:

- `NeverFailedForCancellation` breaks when the pass reads the host token per operation, as before the sync commit. In the model, use `hostFlag` instead of `snap.host` in `TimeOutSteps`, `ExpireScenario` and `ExpireStep`. TLC then finds the 11-step path above.
- `TimeoutHasOneOwner` breaks when `TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios` no longer runs. With the call removed from `Supervise`, and `TimeOutSteps` made to change nothing in the model, TLC finds a step whose deadline passed first being reported as a scenario timeout.

After a change to the model, repeat one of these breaks in a scratch copy, so that you know the rule can still fail.

## What it does not cover

- One scenario and one running step. Sibling steps, `RunAsync`'s scheduling loop, and the teardown runner (`TeardownRunner`) are not modeled.
- The cancellation grace, and the host termination when a body does not stop within it.
- Steps whose timeout has not started yet (`WatchBeforeItsTimeoutStarts` before `StartTimeout`). The model starts the step with its timeout running.
- Thread-pool starvation. It decides which interleaving happens and how late. It does not add interleavings. So the rules hold under load exactly when they hold here. A test that waits too briefly for a deadline is a test problem, which this model cannot see.
- Simulated time (`DeadlineClock`, `SimulatedClock`).
