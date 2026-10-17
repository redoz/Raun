using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Raun.Mtp.HtmlReport;

/// <summary>
/// Registers Raun's HTML-report command-line options with Microsoft.Testing.Platform:
/// <c>--report-html</c> (a flag) and <c>--report-html-filename &lt;name&gt;</c> (default
/// <c>raun-report.html</c>). The report is written under MTP's <c>--results-directory</c>
/// (design §3.E). The optional <c>--report-source-base-url</c> and
/// <c>--report-source-root</c> options add repository source links. Generic names — Raun owns its loaded extension set, so collision risk is low.
/// </summary>
internal sealed class HtmlReportOptionsProvider : ICommandLineOptionsProvider
{
    internal const string EnableOption = "report-html";
    internal const string FilenameOption = "report-html-filename";
    internal const string SourceBaseUrlOption = "report-source-base-url";
    internal const string SourceRootOption = "report-source-root";
    internal const string DefaultFilename = "raun-report.html";

    public string Uid => "raun.mtp.htmlreport";
    public string Version => "1.0.0";
    public string DisplayName => "Raun HTML report";
    public string Description => "Writes a self-contained raun-report.html (per-scenario step list with logs, effects and resource lineage).";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new CommandLineOption(EnableOption,
            "Write a self-contained HTML run report under the results directory.",
            ArgumentArity.Zero, isHidden: false),
        new CommandLineOption(FilenameOption,
            $"Filename for the HTML report (default '{DefaultFilename}').",
            ArgumentArity.ExactlyOne, isHidden: false),
        new CommandLineOption(SourceBaseUrlOption,
            "Repository file-view URL, including the branch or commit, for HTML report source links.",
            ArgumentArity.ExactlyOne, isHidden: false),
        new CommandLineOption(SourceRootOption,
            "Build checkout root for repository-relative source links; defaults to the source file's Git root.",
            ArgumentArity.ExactlyOne, isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
    {
        if (commandOption.Name == FilenameOption
            && (arguments.Length != 1 || string.IsNullOrWhiteSpace(arguments[0])))
        {
            return ValidationResult.InvalidTask($"'--{FilenameOption}' requires a non-empty filename.");
        }

        if (commandOption.Name == SourceBaseUrlOption
            && (arguments.Length != 1 || !Uri.TryCreate(arguments[0], UriKind.Absolute, out var address)
                || address.Scheme is not ("http" or "https")))
        {
            return ValidationResult.InvalidTask($"'--{SourceBaseUrlOption}' requires an absolute HTTP or HTTPS URL.");
        }
        if (commandOption.Name == SourceRootOption
            && (arguments.Length != 1 || string.IsNullOrWhiteSpace(arguments[0])))
        {
            return ValidationResult.InvalidTask($"'--{SourceRootOption}' requires a non-empty checkout root.");
        }
        return ValidationResult.ValidTask;
    }

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
        => ValidationResult.ValidTask;
}
