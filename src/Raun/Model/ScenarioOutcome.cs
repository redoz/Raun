using Raun.Scheduling;

namespace Raun.Model;

/// <summary>How a scenario ended, as one of five answers.</summary>
public enum ScenarioStatus
{
    /// <summary>At least one of its steps ran and passed, and nothing failed or stopped it.</summary>
    Passed,

    /// <summary>A step, the setup or the teardown failed.</summary>
    Failed,

    /// <summary>A step or the scenario ran out of time.</summary>
    TimedOut,

    /// <summary>The host cancelled the run before the scenario reached its end.</summary>
    Canceled,

    /// <summary>None of its steps ran: preflight failed, or the run selected none of them.</summary>
    NotRun,
}

/// <summary>
/// How a scenario ended, decided from its step results in one place. Everything that reports a
/// scenario as a whole reads this: the test node of a scenario reported as one test, the HTML
/// report, the scenario's trace span, and the teardown policy. They used to decide separately, and
/// a cancelled scenario was passed in one, skipped in another and aborted in a third.
/// </summary>
/// <param name="Status">The answer.</param>
/// <param name="Decisive">The step that decided it, when one did: the first to time out, else the
/// first to fail. It carries the exception.</param>
/// <param name="Reason">Why, for an outcome no single step decided: the skip reason of a scenario
/// that did not run, was cancelled, or ran out of time between steps.</param>
public sealed record ScenarioOutcome(ScenarioStatus Status, StepResult? Decisive = null, string? Reason = null)
{
    /// <summary>
    /// True when nothing failed, timed out or was cancelled. A scenario of which nothing ran did not
    /// fail either; this is what a teardown policy of "on success" asks.
    /// </summary>
    public bool Succeeded => Status is ScenarioStatus.Passed or ScenarioStatus.NotRun;

    /// <summary>
    /// Decides the outcome of a scenario from the results of its steps.
    /// </summary>
    /// <remarks>
    /// In order: a step that timed out decides, even after an earlier failure; else the first
    /// failure; else a scenario deadline that left steps unrun; else a cancellation. A scenario
    /// stopped short did not pass, whatever finished before the stop. Otherwise it passed when at
    /// least one of its own steps did. Setup and teardown run around the scenario and cannot pass
    /// it (teardown passes even after a cancellation), though their failure fails it. Plumbing
    /// results, which no one wrote as a step, take no part. A step skipped because the run did not
    /// select it, and a branch that was not taken, count neither for nor against.
    /// </remarks>
    public static ScenarioOutcome Of(IEnumerable<StepResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        StepResult? failure = null;
        StepResult? timeout = null;
        var passed = false;
        var canceled = false;
        var deadline = false;
        string? skipReason = null;

        foreach (var step in results.Where(r => !r.Node.IsSynthetic).OrderBy(r => r.StartedAt).ThenBy(r => r.Node.Index))
        {
            switch (step.Status)
            {
                case StepStatus.Passed:
                    passed |= !(step.Node.IsSetup || step.Node.IsTeardown);
                    break;
                case StepStatus.Skipped:
                    canceled |= step.SkipReason == ScenarioScheduler.CanceledSkipReason;
                    deadline |= step.SkipReason == ScenarioScheduler.TimedOutSkipReason;
                    skipReason ??= step.SkipReason;
                    break;
                case StepStatus.NotTaken:
                    skipReason ??= step.SkipReason;
                    break;
                case StepStatus.Failed when step.Exception is TimeoutException:
                    timeout ??= step;
                    break;
                default:
                    failure ??= step;
                    break;
            }
        }

        if (timeout is not null)
        {
            return new ScenarioOutcome(ScenarioStatus.TimedOut, timeout);
        }

        if (failure is not null)
        {
            return new ScenarioOutcome(ScenarioStatus.Failed, failure);
        }

        if (deadline)
        {
            return new ScenarioOutcome(ScenarioStatus.TimedOut, Reason: ScenarioScheduler.TimedOutSkipReason);
        }

        if (canceled)
        {
            return new ScenarioOutcome(ScenarioStatus.Canceled, Reason: ScenarioScheduler.CanceledSkipReason);
        }

        return passed
            ? new ScenarioOutcome(ScenarioStatus.Passed)
            : new ScenarioOutcome(ScenarioStatus.NotRun, Reason: skipReason ?? "skipped");
    }
}
