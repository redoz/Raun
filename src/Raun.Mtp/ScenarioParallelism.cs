using System.Globalization;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp;

/// <summary>Resolves how many scenarios may run at once: the command line wins over the code default.</summary>
internal static class ScenarioParallelism
{
    /// <summary>Accepts exactly one argument that is a non-negative integer (no sign, no decimals).</summary>
    public static bool TryParse(string[]? arguments, out int degree)
    {
        degree = 0;
        return arguments is { Length: 1 }
            && int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out degree);
    }

    /// <summary>
    /// The degree for this run: <c>--max-parallel-scenarios</c> when given and valid, otherwise
    /// <paramref name="codeDefault"/> (what <c>RaunTestApplication.RunAsync</c> was called with).
    /// A null <paramref name="services"/> is the unit-test path with no MTP host.
    /// </summary>
    public static int Resolve(IServiceProvider? services, int codeDefault)
    {
        if (services is null)
        {
            return codeDefault;
        }

        ICommandLineOptions options = services.GetCommandLineOptions();
        return options.TryGetOptionArgumentList(RunOptionsProvider.MaxParallelScenariosOption, out var arguments)
            && TryParse(arguments, out var degree)
            ? degree
            : codeDefault;
    }
}
