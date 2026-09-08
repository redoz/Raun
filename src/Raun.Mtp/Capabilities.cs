using System.Reflection;
using Microsoft.Testing.Platform.Capabilities.TestFramework;

namespace Raun.Mtp;

/// <summary>
/// A "stop admitting work" flag, created once in <see cref="RaunTestApplication.RunAsync"/> and held
/// for the lifetime of the process — not scoped to a single run. The platform asks for a graceful stop
/// (today when <c>--maximum-failed-tests</c> is crossed) and the run loop honours it at its admission
/// scan, so nothing in flight is killed. Created in the bootstrap because the capability and the
/// framework are built by different factories and need to meet. Because it outlives any one run, a
/// stop request also suppresses admission on whatever runs follow in the same process; harmless for a
/// command-line invocation today, since the process exits after the one run, but worth knowing if a
/// future host reuses the process for more than one run.
/// </summary>
internal sealed class RunStopSignal
{
    private int _requested;

    /// <summary>True once a stop has been asked for; never returns to false within a run.</summary>
    public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Asks the run to stop admitting scenarios. Idempotent.</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);
}

/// <summary>
/// Declares that Raun can stop gracefully, which is what makes the platform offer
/// <c>--maximum-failed-tests</c>: its option provider is enabled only for a framework with this
/// capability.
/// </summary>
#pragma warning disable TPEXP // IGracefulStopTestExecutionCapability and IBannerMessageOwnerCapability are experimental Microsoft.Testing.Platform APIs.
internal sealed class RaunGracefulStopCapability : IGracefulStopTestExecutionCapability
{
    private readonly RunStopSignal _signal;

    public RaunGracefulStopCapability(RunStopSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _signal = signal;
    }

    /// <summary>Stops admitting new scenarios; in-flight ones run to completion and still report.</summary>
    public Task StopTestExecutionAsync(CancellationToken cancellationToken)
    {
        _signal.Request();
        return Task.CompletedTask;
    }
}

/// <summary>Names Raun and its version in the platform's banner instead of the platform's default.</summary>
internal sealed class RaunBannerCapability : IBannerMessageOwnerCapability
{
    public Task<string?> GetBannerMessageAsync()
    {
        var version = typeof(RaunBannerCapability).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var trimmed = string.IsNullOrEmpty(version) ? "0.0.0" : version!.Split('+')[0];
        return Task.FromResult<string?>($"Raun v{trimmed}");
    }
}
#pragma warning restore TPEXP
