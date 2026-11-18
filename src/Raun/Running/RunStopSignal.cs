namespace Raun.Running;

/// <summary>
/// A "stop admitting work" flag an adapter hands to one <see cref="RunLoop"/> run. The host asks for
/// a graceful stop (under Microsoft.Testing.Platform, when <c>--maximum-failed-tests</c> is crossed)
/// and the loop honours it at its admission scan, so nothing in flight is killed. The flag never
/// resets, so it belongs to a single run: a host that serves several runs in one process creates a
/// new signal for each, or a stop in one would keep every later run from admitting anything.
/// </summary>
public sealed class RunStopSignal
{
    private int _requested;

    /// <summary>True once a stop has been asked for; never returns to false within a run.</summary>
    public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Asks the run to stop admitting scenarios. Idempotent.</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);
}
