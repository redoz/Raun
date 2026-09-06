using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Raun.Mtp;

/// <summary>
/// Registers Raun's run-shaping command-line options with Microsoft.Testing.Platform:
/// <c>--max-parallel-scenarios &lt;n&gt;</c> overrides the degree the suite set in code
/// (<c>0</c> = processor count, <c>1</c> = sequential).
/// </summary>
internal sealed class RunOptionsProvider : ICommandLineOptionsProvider
{
    internal const string MaxParallelScenariosOption = "max-parallel-scenarios";

    public string Uid => "raun.mtp.run";
    public string Version => "1.0.0";
    public string DisplayName => "Raun run options";
    public string Description => "Controls how Raun runs scenarios.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new CommandLineOption(MaxParallelScenariosOption,
            "Maximum scenarios running at once. 0 means the processor count; 1 runs scenarios one after another. Overrides the suite's code default.",
            ArgumentArity.ExactlyOne, isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
        => commandOption.Name == MaxParallelScenariosOption && !ScenarioParallelism.TryParse(arguments, out _)
            ? ValidationResult.InvalidTask($"'--{MaxParallelScenariosOption}' requires an integer of 0 or more.")
            : ValidationResult.ValidTask;

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
        => ValidationResult.ValidTask;
}
