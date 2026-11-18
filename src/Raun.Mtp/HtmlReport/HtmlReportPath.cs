using System.Globalization;
using System.Reflection;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp.HtmlReport;

/// <summary>Resolves the absolute HTML report path from MTP's command-line options + configuration,
/// or returns <see langword="null"/> when <c>--report-html</c> is absent.</summary>
public static class HtmlReportPath
{
    /// <summary>Pure resolution from its inputs. Falls back to the current directory when MTP
    /// did not supply a results directory, and to <see cref="DefaultFilename"/> when no filename
    /// was asked for.</summary>
    internal static string? Resolve(
        bool enabled, string? filename, string? resultsDirectory, string? assemblyName = null, string? targetFrameworkName = null)
    {
        if (!enabled)
        {
            return null;
        }

        var dir = string.IsNullOrEmpty(resultsDirectory) ? Directory.GetCurrentDirectory() : resultsDirectory;
        var name = string.IsNullOrWhiteSpace(filename) ? DefaultFilename(assemblyName, targetFrameworkName) : filename;
        return Path.Combine(dir, name);
    }

    /// <summary>
    /// The report's name when none was asked for: the test assembly and its target framework, for
    /// example <c>Clinic.Tests_net10.0.html</c>. Several test projects, or one project built for
    /// several frameworks, commonly share a results directory (<c>dotnet test --results-directory</c>);
    /// one fixed name would leave only the report of whichever finished last.
    /// </summary>
    /// <param name="assemblyName">The test application's assembly name; unknown falls back to <c>raun-report</c>.</param>
    /// <param name="targetFrameworkName">A framework name as in <see cref="AppContext.TargetFrameworkName"/>
    /// (<c>.NETCoreApp,Version=v10.0</c>); left out of the filename when unknown.</param>
    internal static string DefaultFilename(string? assemblyName, string? targetFrameworkName)
    {
        var name = string.IsNullOrWhiteSpace(assemblyName) ? "raun-report" : assemblyName;
        return ShortFrameworkName(targetFrameworkName) is { } framework
            ? $"{name}_{framework}.html"
            : $"{name}.html";
    }

    /// <summary><c>.NETCoreApp,Version=v10.0</c> as <c>net10.0</c>; null for anything else.</summary>
    internal static string? ShortFrameworkName(string? targetFrameworkName)
    {
        const string prefix = ".NETCoreApp,Version=v";
        if (targetFrameworkName is null || !targetFrameworkName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return Version.TryParse(targetFrameworkName.AsSpan(prefix.Length), out var version)
            ? string.Create(CultureInfo.InvariantCulture, $"net{version.Major}.{version.Minor}")
            : null;
    }

    internal static Raun.Reporting.Html.HtmlReportSourceOptions? ResolveSourceOptions(IServiceProvider services)
    {
        var options = services.GetCommandLineOptions();
        if (!options.TryGetOptionArgumentList(HtmlReportOptionsProvider.SourceBaseUrlOption, out var values)
            || values.Length == 0) { return null; }
        var root = options.TryGetOptionArgumentList(HtmlReportOptionsProvider.SourceRootOption, out var roots)
            && roots.Length > 0 ? roots[0] : null;
        return new Raun.Reporting.Html.HtmlReportSourceOptions { BaseUrl = new Uri(values[0]), SourceRoot = root };
    }

    /// <summary>Where this run writes its HTML report, read off the platform's service provider
    /// (command-line options and the results directory); <see langword="null"/> when
    /// <c>--report-html</c> was not given. For an extension that adds to the report file.</summary>
    public static string? Resolve(IServiceProvider services)
    {
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
        return Resolve(
            enabled: true,
            filename,
            resultsDirectory: configuration[RaunAttachments.ResultsDirectoryKey],
            assemblyName: Assembly.GetEntryAssembly()?.GetName().Name,
            targetFrameworkName: AppContext.TargetFrameworkName);
    }
}
