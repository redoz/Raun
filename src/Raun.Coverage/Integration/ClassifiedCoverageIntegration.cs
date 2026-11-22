using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions.OutputDevice;
using Microsoft.Testing.Platform.OutputDevice;
using Microsoft.Testing.Platform.Services;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;
using Raun.Coverage;

namespace Raun.Mtp.Coverage;

internal sealed class ClassifiedCoverageIntegration : IRaunCoverageIntegration
{
    private readonly CoverageSessionCoordinator _coordinator;
    private readonly CoverageSourceProjects _hosted;
    private CoverletReportEnricher? _coverletReport;

    public ClassifiedCoverageIntegration(ITestApplicationBuilder builder)
    {
        var invocationDirectory = Directory.GetCurrentDirectory();
        var factories = CoverageRegistration.For(builder).Factories.ToArray();
        var sourceConfiguration = new CoverageSourceConfiguration();

        // The projects the run hosts. Read at analysis time, not here: the run names them when it
        // starts them. In the test host that is this list; the controller, which analyses Coverlet's
        // report after the host has exited, is handed the host's list.
        _hosted = builder.GetCoverageSourceProjects();
        var handoff = new HostedProjectsHandoff();
        builder.TestHostControllers.AddEnvironmentVariableProvider(_ => handoff);

        async Task<ReportCoverage> AnalyzeAsync(
            FileInfo file, IReadOnlyCollection<string> hostedProjects, IOutputDevice? output, IOutputDeviceDataProducer producer, CancellationToken token)
        {
            if (sourceConfiguration.Error is { } error)
            {
                return new ReportCoverage { Status = "unavailable", Diagnostics = [error] };
            }

            var analysis = await ClassifiedCoverageAnalyzer.AnalyzeWithWarningsAsync(
                file.FullName, invocationDirectory, sourceConfiguration.Scope, factories, hostedProjects, token).ConfigureAwait(false);
            if (output is not null)
            {
                // Also on the screen: a report nobody opens cannot say a service went unmeasured.
                foreach (var warning in analysis.Warnings)
                {
                    await output.DisplayAsync(producer, new WarningMessageOutputDeviceData("Raun coverage: " + warning), token).ConfigureAwait(false);
                }
            }

            return analysis.Report;
        }

        Func<FileInfo, CancellationToken, Task<ReportCoverage>> inHost = (_, _) => Task.FromResult(new ReportCoverage { Status = "unavailable" });
        _coordinator = new CoverageSessionCoordinator((file, token) => inHost(file, token));
        builder.TestHost.AddDataConsumer(services =>
        {
            var options = services.GetCommandLineOptions();
            var enabled = options.IsOptionSet("report-html") && (options.IsOptionSet("coverage") || options.IsOptionSet("coverlet"));
            if (enabled) { sourceConfiguration.Initialize(services, invocationDirectory); }
            var consumer = new CoverageArtifactConsumer(_coordinator, enabled);
            var output = services.GetService(typeof(IOutputDevice)) as IOutputDevice;
            inHost = (file, token) => AnalyzeAsync(file, _hosted.Paths, output, consumer, token);
            return consumer;
        });

        builder.TestHostControllers.AddProcessLifetimeHandler(services =>
        {
            var options = services.GetCommandLineOptions();
            var reportPath = options.IsOptionSet("coverlet") ? Raun.Mtp.HtmlReport.HtmlReportPath.Resolve(services) : null;
            if (reportPath is not null) { sourceConfiguration.Initialize(services, invocationDirectory); }
            var output = services.GetService(typeof(IOutputDevice)) as IOutputDevice;
            CoverletReportEnricher? enricher = null;
            return _coverletReport = enricher = new CoverletReportEnricher(
                () => (services.GetService(typeof(ITestCoverageResult)) as ITestCoverageResult)?.Reports ?? [], reportPath,
                (file, token) => AnalyzeAsync(file, handoff.Take(), output, enricher!, token));
        });
    }

    public Task AttachReportAsync(string session, HtmlReportSink report) => _coordinator.AttachReportAsync(session, report);

    public async Task CompleteAsync()
    {
        // The test host, ending: what it hosted, for a controller that analyses after it.
        HostedProjectsHandoff.Leave(_hosted.Paths);
        if (_coverletReport is not null) { await _coverletReport.EnrichAsync().ConfigureAwait(false); }
        await _coordinator.CompleteAsync().ConfigureAwait(false);
    }

    public void Dispose() => _coordinator.Dispose();
}
