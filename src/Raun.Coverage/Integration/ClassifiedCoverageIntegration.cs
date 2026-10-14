using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Services;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;
using Raun.Coverage;

namespace Raun.Mtp.Coverage;

internal sealed class ClassifiedCoverageIntegration : IRaunCoverageIntegration
{
    private readonly CoverageSessionCoordinator _coordinator;
    private CoverletReportEnricher? _coverletReport;

    public ClassifiedCoverageIntegration(ITestApplicationBuilder builder)
    {
        var invocationDirectory = Directory.GetCurrentDirectory();
        var factories = CoverageRegistration.For(builder).Factories.ToArray();
        var sourceConfiguration = new CoverageSourceConfiguration();
        _coordinator = new CoverageSessionCoordinator((file, token) => sourceConfiguration.Error is { } error
            ? Task.FromResult(new ReportCoverage { Status = "unavailable", Diagnostics = [error] })
            : ClassifiedCoverageAnalyzer.AnalyzeAsync(file.FullName, invocationDirectory, sourceConfiguration.Scope, factories, token));
        builder.TestHost.AddDataConsumer(services =>
        {
            var options = services.GetCommandLineOptions();
            var enabled = options.IsOptionSet("report-html") && (options.IsOptionSet("coverage") || options.IsOptionSet("coverlet"));
            if (enabled) { sourceConfiguration.Initialize(services, invocationDirectory); }
            return new CoverageArtifactConsumer(_coordinator, enabled);
        });

        builder.TestHostControllers.AddProcessLifetimeHandler(services =>
        {
            var options = services.GetCommandLineOptions();
            var reportPath = options.IsOptionSet("coverlet") ? Raun.Mtp.HtmlReport.HtmlReportPath.Resolve(services) : null;
            if (reportPath is not null) { sourceConfiguration.Initialize(services, invocationDirectory); }
            return _coverletReport = new CoverletReportEnricher(
                () => (services.GetService(typeof(ITestCoverageResult)) as ITestCoverageResult)?.Reports ?? [], reportPath,
                (file, token) => sourceConfiguration.Error is { } error
                    ? Task.FromResult(new ReportCoverage { Status = "unavailable", Diagnostics = [error] })
                    : ClassifiedCoverageAnalyzer.AnalyzeAsync(file.FullName, invocationDirectory, sourceConfiguration.Scope, factories, token));
        });

    }

    public Task AttachReportAsync(string session, HtmlReportSink report) => _coordinator.AttachReportAsync(session, report);

    public async Task CompleteAsync()
    {
        if (_coverletReport is not null) { await _coverletReport.EnrichAsync().ConfigureAwait(false); }
        await _coordinator.DrainAsync().ConfigureAwait(false);
    }

    public void Dispose() => _coordinator.Dispose();
}
