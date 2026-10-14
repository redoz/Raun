using Microsoft.Testing.Platform.Services;
using Raun.Scheduling;

namespace Raun.Mtp;

/// <summary>Resolves a finite per-cleanup deadline from the host option.</summary>
internal static class CleanupTimeout
{
    internal const string Option = "cleanup-timeout-ms";

    internal static bool TryParse(string[]? arguments, out int milliseconds)
        => CancellationGrace.TryParse(arguments, out milliseconds) && milliseconds > 0;

    internal static TimeSpan Resolve(IServiceProvider? services)
        => services is not null
            && services.GetCommandLineOptions().TryGetOptionArgumentList(Option, out var arguments)
            && TryParse(arguments, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : ScenarioScheduler.DefaultCleanupTimeout;
}
