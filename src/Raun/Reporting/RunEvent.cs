using Raun.Model;
using Raun.Scheduling;

namespace Raun.Reporting;

/// <summary>Base type for the runner-neutral run-event stream.</summary>
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

/// <summary>Raised when a step is about to run (or, for a step skipped because of what came before
/// it, just before its finish). Always followed by exactly one <see cref="StepFinished"/> for the
/// same step, unless the test host is terminated first.</summary>
public sealed record StepStarted(ScenarioDefinition Definition, StepContext Context) : RunEvent;

/// <summary>Raised when a step reaches a terminal status; the result is self-contained (carries
/// StartedAt, duration, logs, effects, exception/skip reason). It may arrive without a
/// <see cref="StepStarted"/>: for a branch that was not taken, for a merge, and for every step of a
/// scenario that did not run because preflight did not pass. A sink that pairs the two pairs on the
/// start and accepts a finish that had none.</summary>
public sealed record StepFinished(ScenarioDefinition Definition, StepResult Result) : RunEvent;

/// <summary>Raised when a scenario's steps have all reached terminal status.</summary>
public sealed record ScenarioFinished(
    ScenarioDefinition Definition, IReadOnlyList<StepResult> Results) : RunEvent
{
    /// <summary>How the scenario ended, decided from <see cref="Results"/>: read this instead of
    /// folding the step results again.</summary>
    public ScenarioOutcome Outcome { get; } = ScenarioOutcome.Of(Results);
}

/// <summary>Raised once at the end of a run, after the last scenario.</summary>
public sealed record RunFinished : RunEvent
{
    /// <summary>
    /// Why the run ended before its scenarios did, or <see langword="null"/> for a run that finished.
    /// Set when the test host is about to be terminated because a step or cleanup did not stop
    /// within the cancellation grace: this is then the last event, raised so that a report sink can
    /// still write what it has. Steps that were running have no result.
    /// </summary>
    public string? AbortReason { get; init; }
}
