using Microsoft.Testing.Platform.Services;
using System.Globalization;
using Raun.Scheduling;

namespace Raun.Mtp;

/// <summary>Resolves the runtime grace policy from MTP's command-line override.</summary>
internal static class CancellationGrace
{
    internal const string Option = "cancellation-grace-ms";

    internal static bool TryParse(string[]? arguments, out int milliseconds)
    {
        milliseconds = 0;
        return arguments is { Length: 1 }
            && int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);
    }

    internal static TimeSpan Resolve(IServiceProvider? services)
    {
        return services is not null
            && services.GetCommandLineOptions().TryGetOptionArgumentList(Option, out var arguments)
            && TryParse(arguments, out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : ScenarioScheduler.DefaultCancellationGracePeriod;
    }
}
