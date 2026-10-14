using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp;

/// <summary>Resolves the per-scenario step limit, with command-line precedence.</summary>
internal static class StepParallelism
{
    public static bool TryParse(string[]? arguments, out int degree) => ScenarioParallelism.TryParse(arguments, out degree);

    public static int Resolve(IServiceProvider? services, int codeDefault)
        => services is not null
            && services.GetCommandLineOptions().TryGetOptionArgumentList(RunOptionsProvider.MaxParallelStepsOption, out var arguments)
            && TryParse(arguments, out var degree) ? degree : codeDefault;
}
