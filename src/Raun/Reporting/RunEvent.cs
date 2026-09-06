using Raun.Model;
using Raun.Scheduling;

namespace Raun.Reporting;

/// <summary>Base type for the runner-neutral run-event stream (design §3.A).</summary>
public abstract record RunEvent;

/// <summary>Raised once at the start of a run, before any scenario. <paramref name="Scenarios"/> is
/// the run's canonical order (preflight first when present, then the selected scenarios in
/// registration order) so a sink can lay scenarios out deterministically whatever order admission
/// launches them in; null when the publisher has no ordering to offer.</summary>
public sealed record RunStarted(int ScenarioCount, IReadOnlyList<ScenarioDefinition>? Scenarios = null) : RunEvent;

/// <summary>Raised when a scenario begins; carries the definition so a session-scoped sink can
/// attribute every following step to its scenario. <paramref name="Waited"/> is how long admission
/// held the scenario back behind a contended resource (<paramref name="WaitedFor"/> names the first
/// refusing one); zero and null when it was admitted the moment a slot was free.</summary>
public sealed record ScenarioStarted(ScenarioDefinition Definition, TimeSpan Waited = default, Type? WaitedFor = null) : RunEvent;

/// <summary>Raised when a step is about to run (or, for a skipped step, just before its finish).</summary>
public sealed record StepStarted(ScenarioDefinition Definition, StepContext Context) : RunEvent;

/// <summary>Raised when a step reaches a terminal status; the result is self-contained (carries
/// StartedAt, duration, logs, effects, exception/skip reason).</summary>
public sealed record StepFinished(ScenarioDefinition Definition, StepResult Result) : RunEvent;

/// <summary>Raised when a scenario's steps have all reached terminal status.</summary>
public sealed record ScenarioFinished(
    ScenarioDefinition Definition, IReadOnlyList<StepResult> Results) : RunEvent;

/// <summary>Raised once at the end of a run, after the last scenario.</summary>
public sealed record RunFinished : RunEvent;
