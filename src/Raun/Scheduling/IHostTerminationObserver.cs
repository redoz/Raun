namespace Raun.Scheduling;

/// <summary>
/// An <see cref="IStepObserver"/> that also wants a last word when the test host is about to be
/// terminated: a step or cleanup did not stop within the cancellation grace, so the process is
/// killed and nothing after this call runs. The call is bounded
/// (<see cref="CancellationWatchdog.TerminationFlushBudget"/>); what does not finish in time is lost.
/// </summary>
internal interface IHostTerminationObserver
{
    /// <summary>Called once, from the watchdog, with the reason the host is being terminated.</summary>
    Task OnHostTerminatingAsync(string reason);
}
