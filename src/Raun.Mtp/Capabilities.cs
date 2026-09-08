using System.Reflection;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Raun.Running;

namespace Raun.Mtp;

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
