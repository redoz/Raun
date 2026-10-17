using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp.HtmlReport;

/// <summary>Resolves the absolute HTML report path from MTP's command-line options + configuration,
/// or returns <see langword="null"/> when <c>--report-html</c> is absent (design §3.E).</summary>
internal static class HtmlReportPath
{
    // MTP's well-known results-directory configuration key (PlatformConfigurationConstants is internal).
    private const string ResultsDirectoryKey = "platformOptions:resultDirectory";

    /// <summary>Pure resolution from the three inputs. Falls back to the current directory when MTP
    /// did not supply a results directory.</summary>
    public static string? Resolve(bool enabled, string? filename, string? resultsDirectory)
    {
        if (!enabled)
        {
            return null;
        }

        var dir = string.IsNullOrEmpty(resultsDirectory) ? Directory.GetCurrentDirectory() : resultsDirectory;
        var name = string.IsNullOrWhiteSpace(filename) ? HtmlReportOptionsProvider.DefaultFilename : filename;
        return Path.Combine(dir, name);
    }

    internal static Raun.Reporting.Html.HtmlReportSourceOptions? ResolveSourceOptions(IServiceProvider? services)
    {
        if (services is null) { return null; }
        var options = services.GetCommandLineOptions();
        if (!options.TryGetOptionArgumentList(HtmlReportOptionsProvider.SourceBaseUrlOption, out var values)
            || values.Length == 0) { return null; }
        var root = options.TryGetOptionArgumentList(HtmlReportOptionsProvider.SourceRootOption, out var roots)
            && roots.Length > 0 ? roots[0] : null;
        return new Raun.Reporting.Html.HtmlReportSourceOptions { BaseUrl = new Uri(values[0]), SourceRoot = root };
    }

    /// <summary>Reads the inputs off the framework's MTP service provider.</summary>
    public static string? Resolve(IServiceProvider? services)
    {
        if (services is null)
        {
            return null;
        }

        ICommandLineOptions options = services.GetCommandLineOptions();
        if (!options.IsOptionSet(HtmlReportOptionsProvider.EnableOption))
        {
            return null;
        }

        string? filename = options.TryGetOptionArgumentList(HtmlReportOptionsProvider.FilenameOption, out var args)
            && args.Length > 0
            ? args[0]
            : null;

        IConfiguration configuration = services.GetConfiguration();
        return Resolve(enabled: true, filename, resultsDirectory: configuration[ResultsDirectoryKey]);
    }
}
