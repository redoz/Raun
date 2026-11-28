// Words for the /statechart chart of SchedulerCancellation.tla.

type V = { [name: string]: any };

const results: { [k: string]: string } = {
  Passed: "Step passed",
  Skipped: "Step skipped as cancelled",
  StepTimeout: "Step failed: its own timeout",
  ScenarioTimeout: "Step failed: scenario timeout",
  FailedCanceled: "Step failed with a cancellation exception",
};

export const title = "Why a running step stopped";

export const state = (v: V) => {
  if (v.reported !== "none") return results[v.reported];
  const live = v.step.started && !v.scen.started ? ", scenario token still live" : "";
  if (v.body === "running") return v.step.started ? `Step asked to stop${live}` : "Step running";
  return `Step finishing${live}`;
};

export const group = (v: V) => (v.reported === "FailedCanceled" ? "wrong" : v.reported === "none" ? "running" : "reported");

export const colors = { running: "blue", reported: "teal", wrong: "orange" };

const steps: { [k: string]: string } = {
  HostCancels: "the test platform cancels the run",
  HostCallbackRuns: "the run's cancellation reaches the scenario",
  StepDeadlinePasses: "the step's timeout passes",
  ScenarioDeadlinePasses: "the scenario's timeout passes",
  BothDeadlinesPass: "both timeouts pass at once",
  PassStarts: "the watchdog starts a pass",
  TimeOutSteps: "the watchdog checks which deadline came first",
  ExpireScenario: "the watchdog checks the scenario",
  ExpireStep: "the watchdog checks the step",
  BodyReturns: "the step body returns",
  BodyThrowsOnCancel: "the step body throws on its cancelled token",
  CompleteMarks: "the step marks itself stopping",
  CompleteFinishes: "the step's cancellation callbacks finish",
  Classify: "the step decides its result",
  Apply: "the scheduler records the result",
};

export const step = (action: string) => steps[action] ?? action;
