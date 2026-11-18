using System.Reflection;
using Microsoft.Testing.Platform.Capabilities.TestFramework;

namespace Raun.Mtp;

/// <summary>
/// Declares that Raun can stop gracefully, which is what makes the platform offer
/// <c>--maximum-failed-tests</c>: its option provider is enabled only for a framework with this
/// capability.
/// </summary>
#pragma warning disable TPEXP // IGracefulStopTestExecutionCapability and IBannerMessageOwnerCapability are experimental Microsoft.Testing.Platform APIs.
internal sealed class RaunGracefulStopCapability : IGracefulStopTestExecutionCapability
{
    private readonly RunStopRelay _relay;

    public RaunGracefulStopCapability(RunStopRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        _relay = relay;
    }

    /// <summary>Stops admitting new scenarios in the runs in progress; in-flight scenarios run to
    /// completion and still report. A run that starts afterwards is a new run.</summary>
    public Task StopTestExecutionAsync(CancellationToken cancellationToken)
    {
        _relay.Request();
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
