------------------------------ MODULE SchedulerCancellation ------------------------------
(***************************************************************************)
(* Why did a running step stop? Raun's cancellation watchdog decides, and  *)
(* the scheduler turns the decision into the step's result.                *)
(*                                                                         *)
(* One scenario, one running step with its own timeout, a scenario         *)
(* timeout, and a test platform that may cancel the run.                  *)
(*                                                                         *)
(*   src/Raun/Scheduling/CancellationWatchdog.cs                           *)
(*     Supervise, TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios,       *)
(*     Expire, Operation.RequestCancellation, Operation.CompleteAsync      *)
(*   src/Raun/Scheduling/ScenarioScheduler.cs                              *)
(*     RunAsync and its hostRegistration, RunNodeAsync, ApplyFinishedAsync *)
(*                                                                         *)
(* One action = one span the code runs under the watchdog's gate, or one   *)
(* read/write of a volatile flag outside it. The host token is the only    *)
(* flag that changes while the gate is held: CancellationTokenSource sets   *)
(* IsCancellationRequested without taking Raun's gate, so a pass reads it  *)
(* once, with the clock.                                                   *)
(***************************************************************************)

VARIABLES
    hostFlag,      \* the host CancellationToken has been cancelled
    hostCallback,  \* "none" | "pending" | "ran": cancellationToken.Register(() => scenarioWatch.RequestCancellation())
    scen,          \* scenario Operation. started = CancellationStarted, and timeoutCts, the scenarioToken, is cancelled
    step,          \* step Operation. started = CancellationStarted, and stepCts, the ctx.CancellationToken, is cancelled
    scenPassed,    \* the scenario deadline has passed, in real time
    stepPassed,    \* the step deadline has passed, in real time
    order,         \* which deadline passed first: "none" | "step" | "scenario" | "tie"
    wd,            \* the watchdog's pass; anything but "idle" means it holds the gate
    snap,          \* the pass's one reading of the clock and of the host token, now and runCancelled
    body,          \* where RunNodeAsync is with the step
    returned,      \* the step body returned instead of throwing
    outcome,       \* NodeOutcome.Result.Status as RunNodeAsync built it
    outCanceled,   \* NodeOutcome.Canceled, from operationWatch.CancellationStarted
    outTimedOut,   \* NodeOutcome.StepTimedOut, from operationWatch.TimedOut
    reported       \* the step's result once ApplyFinishedAsync has applied it

vars == <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, wd, snap,
          body, returned, outcome, outCanceled, outTimedOut, reported>>

Init ==
    /\ hostFlag = FALSE
    /\ hostCallback = "none"
    /\ scen = [started |-> FALSE, timedOut |-> FALSE]
    /\ step = [started |-> FALSE, timedOut |-> FALSE, completed |-> FALSE]
    /\ scenPassed = FALSE
    /\ stepPassed = FALSE
    /\ order = "none"
    /\ wd = "idle"
    /\ snap = [scen |-> FALSE, step |-> FALSE, stepFirst |-> FALSE, host |-> FALSE]
    /\ body = "running"
    /\ returned = FALSE
    /\ outcome = "none"
    /\ outCanceled = FALSE
    /\ outTimedOut = FALSE
    /\ reported = "none"

\* Operation.RequestCancellation on the step: nothing once it completed or started cancelling.
CancelStep(timedOut) ==
    IF step.completed \/ step.started THEN step
    ELSE [step EXCEPT !.started = TRUE, !.timedOut = timedOut]

\* Operation.RequestCancellation on the scenario: it also asks every other operation to stop.
CancelScenario(timedOut) ==
    IF scen.started
    THEN UNCHANGED <<scen, step>>
    ELSE /\ scen' = [started |-> TRUE, timedOut |-> timedOut]
         /\ step' = CancelStep(FALSE)

----------------------------------------------------------------------------
\* The host: MTP's token, cancelled by the test or by the platform.

HostCancels ==
    /\ ~hostFlag
    /\ hostFlag' = TRUE
    /\ hostCallback' = "pending"
    /\ UNCHANGED <<scen, step, scenPassed, stepPassed, order, wd, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

\* The registration's callback; RequestCancellation takes the gate.
HostCallbackRuns ==
    /\ hostCallback = "pending"
    /\ wd = "idle"
    /\ hostCallback' = "ran"
    /\ CancelScenario(FALSE)
    /\ UNCHANGED <<hostFlag, scenPassed, stepPassed, order, wd, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

----------------------------------------------------------------------------
\* Real time. A deadline passes once; which passed first is what decides ownership.

StepDeadlinePasses ==
    /\ ~stepPassed
    /\ stepPassed' = TRUE
    /\ order' = IF order = "none" THEN "step" ELSE order
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, wd, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

ScenarioDeadlinePasses ==
    /\ ~scenPassed
    /\ scenPassed' = TRUE
    /\ order' = IF order = "none" THEN "scenario" ELSE order
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, stepPassed, wd, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

BothDeadlinesPass ==
    /\ ~scenPassed /\ ~stepPassed
    /\ scenPassed' = TRUE /\ stepPassed' = TRUE
    /\ order' = "tie"
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, wd, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

----------------------------------------------------------------------------
\* The watchdog thread: CancellationWatchdog.Supervise, one pass under the gate.

\* lock (gate) { var now = timestamp(); var runCancelled = hostToken.IsCancellationRequested; ... }
PassStarts ==
    /\ wd = "idle"
    /\ wd' = "timeOutSteps"
    /\ snap' = [scen |-> scenPassed, step |-> stepPassed, stepFirst |-> order = "step", host |-> hostFlag]
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, body, returned, outcome, outCanceled, outTimedOut, reported>>

\* TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios(current, now, runCancelled)
TimeOutSteps ==
    /\ wd = "timeOutSteps"
    /\ wd' = "expireScenario"
    /\ step' = IF ~snap.host /\ ~scen.started /\ snap.scen
                  /\ ~step.completed /\ ~step.started /\ snap.step /\ snap.stepFirst
               THEN CancelStep(TRUE)
               ELSE step
    /\ UNCHANGED <<hostFlag, hostCallback, scen, scenPassed, stepPassed, order, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

\* Expire(current, now, runCancelled) on the scenario.
ExpireScenario ==
    /\ wd = "expireScenario"
    /\ wd' = "expireStep"
    /\ LET cancelScenario == snap.host \/ scen.started
       IN IF cancelScenario \/ snap.scen
          THEN CancelScenario(~cancelScenario)
          ELSE UNCHANGED <<scen, step>>
    /\ UNCHANGED <<hostFlag, hostCallback, scenPassed, stepPassed, order, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

\* foreach (operation in operations): Expire(operation, now, runCancelled) on the step.
ExpireStep ==
    /\ wd = "expireStep"
    /\ wd' = "idle"
    /\ LET cancelScenario == snap.host \/ scen.started
       IN step' = IF ~step.completed /\ (cancelScenario \/ step.started \/ snap.step)
                  THEN CancelStep(~cancelScenario /\ ~step.started)
                  ELSE step
    /\ UNCHANGED <<hostFlag, hostCallback, scen, scenPassed, stepPassed, order, snap, body, returned, outcome, outCanceled, outTimedOut, reported>>

----------------------------------------------------------------------------
\* The step: RunNodeAsync. Its body may return at any time, and throws once its token is cancelled
\* (await Task.Delay(Timeout.Infinite, ctx.CancellationToken)).

BodyReturns ==
    /\ body = "running"
    /\ body' = "completing"
    /\ returned' = TRUE
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, wd, snap, outcome, outCanceled, outTimedOut, reported>>

BodyThrowsOnCancel ==
    /\ body = "running"
    /\ step.started
    /\ body' = "completing"
    /\ returned' = FALSE
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, wd, snap, outcome, outCanceled, outTimedOut, reported>>

\* finally { await operationWatch.CompleteAsync(); }, first lock: if (!CancellationStarted) Completed = true;
CompleteMarks ==
    /\ body = "completing"
    /\ wd = "idle"
    /\ body' = "awaitCallbacks"
    /\ step' = IF step.started THEN step ELSE [step EXCEPT !.completed = TRUE]
    /\ UNCHANGED <<hostFlag, hostCallback, scen, scenPassed, stepPassed, order, wd, snap, returned, outcome, outCanceled, outTimedOut, reported>>

\* CompleteAsync after `await cancellation`, second lock: Completed = true;
CompleteFinishes ==
    /\ body = "awaitCallbacks"
    /\ wd = "idle"
    /\ body' = "classify"
    /\ step' = [step EXCEPT !.completed = TRUE]
    /\ UNCHANGED <<hostFlag, hostCallback, scen, scenPassed, stepPassed, order, wd, snap, returned, outcome, outCanceled, outTimedOut, reported>>

\* After the finally: the checks after a return, or the catch filters after a throw.
\* catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested && !operationWatch.TimedOut)
Classify ==
    /\ body = "classify"
    /\ body' = "done"
    /\ outcome' =
         IF returned
         THEN IF ~step.timedOut /\ step.started /\ scen.started THEN "Skipped"
              ELSE IF step.timedOut THEN "StepTimeout"
              ELSE "Passed"
         ELSE IF scen.started /\ ~step.timedOut THEN "Skipped"
              ELSE IF step.timedOut THEN "StepTimeout"
              ELSE "FailedCanceled"
    /\ outCanceled' = step.started
    /\ outTimedOut' = step.timedOut
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, wd, snap, returned, reported>>

\* ApplyFinishedAsync: if (scenarioWatch.TimedOut && outcome.Canceled && !outcome.StepTimedOut) ApplyTimedOutAsync
Apply ==
    /\ body = "done"
    /\ body' = "reported"
    /\ reported' = IF scen.timedOut /\ outCanceled /\ ~outTimedOut THEN "ScenarioTimeout" ELSE outcome
    /\ UNCHANGED <<hostFlag, hostCallback, scen, step, scenPassed, stepPassed, order, wd, snap, returned, outcome, outCanceled, outTimedOut>>

----------------------------------------------------------------------------

Next ==
    \/ HostCancels \/ HostCallbackRuns
    \/ StepDeadlinePasses \/ ScenarioDeadlinePasses \/ BothDeadlinesPass
    \/ PassStarts \/ TimeOutSteps \/ ExpireScenario \/ ExpireStep
    \/ BodyReturns \/ BodyThrowsOnCancel \/ CompleteMarks \/ CompleteFinishes \/ Classify \/ Apply

Spec == Init /\ [][Next]_vars

----------------------------------------------------------------------------
\* The rules.

\* A step that stopped because the run or the scenario was cancelled is skipped, never failed.
NeverFailedForCancellation == reported # "FailedCanceled"

\* When both deadlines pass, the one that passed first owns the timeout; on a tie the scenario does.
TimeoutHasOneOwner ==
    /\ reported = "StepTimeout" => order = "step"
    /\ reported = "ScenarioTimeout" => order \in {"scenario", "tie"}

=============================================================================
