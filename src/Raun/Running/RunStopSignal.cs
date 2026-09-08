namespace Raun.Running;

/// <summary>
/// A "stop admitting work" flag an adapter creates once and hands to the <see cref="RunLoop"/>. The
/// host asks for a graceful stop (under Microsoft.Testing.Platform, when <c>--maximum-failed-tests</c>
/// is crossed) and the loop honours it at its admission scan, so nothing in flight is killed. It is
/// not scoped to a single run: a stop request also suppresses admission on whatever runs follow in
/// the same process — harmless for a command-line invocation, since the process exits after the one
/// run, but worth knowing for a host that reuses the process.
/// </summary>
public sealed class RunStopSignal
{
    private int _requested;

    /// <summary>True once a stop has been asked for; never returns to false within a run.</summary>
    public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Asks the run to stop admitting scenarios. Idempotent.</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);
}
