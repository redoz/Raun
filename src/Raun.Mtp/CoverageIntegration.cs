using System.Runtime.CompilerServices;
using Microsoft.Testing.Platform.Builder;
using Raun.Reporting.Html;

namespace Raun.Mtp;

/// <summary>Optional classified coverage integration owned by one Raun application.</summary>
public interface IRaunCoverageIntegration : IDisposable
{
    /// <summary>Connects a session's HTML report to coverage delivery.</summary>
    Task AttachReportAsync(string session, HtmlReportSink report);
    /// <summary>Completes collector enrichment and drains outstanding delivery after the application run.</summary>
    Task CompleteAsync();
}

/// <summary>
/// The source projects a run hosts: code that runs because of the tests but in a process of its
/// own, which nothing in the test project's references has to lead to. An API that an Aspire
/// AppHost starts by path is one. The run is what knows them, so whatever starts such a process
/// names its project here, and a coverage integration reads the list when it analyses.
/// </summary>
/// <remarks><c>Raun.Aspire</c> fills this from the application it starts. Fill it yourself, in
/// preflight, for processes you start by other means.</remarks>
public sealed class CoverageSourceProjects
{
    private readonly Lock _gate = new();
    private readonly List<string> _paths = [];

    /// <summary>Names a hosted project by the path of its project file.</summary>
    public void Add(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var full = Path.GetFullPath(projectPath);
        lock (_gate)
        {
            if (!_paths.Contains(full, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                _paths.Add(full);
            }
        }
    }

    /// <summary>The project files named so far, in the order they were named.</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_gate)
            {
                return [.. _paths];
            }
        }
    }
}

/// <summary>Registers an optional coverage integration without making analysis a framework dependency.</summary>
public static class CoverageIntegrationExtensions
{
    private static readonly ConditionalWeakTable<ITestApplicationBuilder, Registration> Registrations = new();
    private static readonly ConditionalWeakTable<ITestApplicationBuilder, CoverageSourceProjects> SourceProjects = new();

    /// <summary>The application's list of hosted source projects. One per application: whoever
    /// starts a process and whoever analyses coverage reach the same list through the builder.</summary>
    public static CoverageSourceProjects GetCoverageSourceProjects(this ITestApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return SourceProjects.GetValue(builder, _ => new CoverageSourceProjects());
    }

    /// <summary>Registers an application-scoped integration factory. The last registration wins.</summary>
    public static ITestApplicationBuilder UseCoverageIntegration(this ITestApplicationBuilder builder,
        Func<ITestApplicationBuilder, IRaunCoverageIntegration> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        Registrations.GetValue(builder, _ => new Registration()).Factory = factory;
        return builder;
    }

    internal static IRaunCoverageIntegration? Create(ITestApplicationBuilder builder)
        => Registrations.TryGetValue(builder, out var registration) ? registration.Factory!(builder) : null;

    private sealed class Registration
    {
        public Func<ITestApplicationBuilder, IRaunCoverageIntegration>? Factory { get; set; }
    }
}
