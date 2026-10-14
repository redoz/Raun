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

/// <summary>Registers an optional coverage integration without making analysis a framework dependency.</summary>
public static class CoverageIntegrationExtensions
{
    private static readonly ConditionalWeakTable<ITestApplicationBuilder, Registration> Registrations = new();

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
