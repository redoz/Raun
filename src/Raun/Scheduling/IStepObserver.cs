using Raun.Model;

namespace Raun.Scheduling;

/// <summary>
/// Callbacks the scheduler raises around each step so a host (e.g. a runner adapter) can report
/// progress without the core taking a dependency on any runner. The scheduler <c>await</c>s each
/// callback and raises them serially — one completes before the next begins — so implementations
/// need not guard against concurrent calls (though a continuation may resume on a different thread
/// across an await). Making these asynchronous lets a host await a genuinely async sink (e.g. the
/// MTP message bus) instead of blocking sync-over-async.
/// </summary>
/// <remarks>
/// <para>
/// What an implementation may rely on: <b>a start is followed by exactly one finish for the same
/// node, and a node finishes at most once.</b> What it may not rely on: a finish does not imply a
/// start. Pair on the start, and accept a finish that had none.
/// </para>
/// <para>A finish arrives without a start for:</para>
/// <list type="bullet">
/// <item>a branch that was not taken (<see cref="StepStatus.NotTaken"/>): it never began, and a
/// reporter can leave its node as discovered instead of showing it in progress;</item>
/// <item>a merge (<see cref="NodeKind.Merge"/>): plumbing that is resolved, not run.</item>
/// </list>
/// <para>
/// A step the run did not select raises neither. The one way a start goes without its finish is
/// the end of the process: a step that does not stop within the cancellation grace gets the test
/// host terminated.
/// </para>
/// <para>
/// An exception from a callback does not stop the scenario. The scenario runs to its end, teardown
/// included, the remaining callbacks are still raised, and <see cref="ScenarioScheduler.RunAsync"/>
/// then throws what the callback threw.
/// </para>
/// </remarks>
public interface IStepObserver
{
    /// <summary>Raised when a step is about to run, or, for a step skipped because of what came
    /// before it, immediately before its finish. Not raised for a branch not taken or a merge.</summary>
    Task OnStepStartingAsync(StepContext context);

    /// <summary>Raised once a step has reached a terminal status. May arrive without a start; see
    /// the remarks on <see cref="IStepObserver"/>.</summary>
    Task OnStepFinishedAsync(StepResult result);
}
