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
    private readonly CoverageSourceProjects _hostedByThisProcess;
    private CoverletReportEnricher? _coverletReport;
    private WarningScreen? _testHostScreen;

    public ClassifiedCoverageIntegration(ITestApplicationBuilder builder)
    {
        var invocationDirectory = Directory.GetCurrentDirectory();
        var factories = CoverageRegistration.For(builder).Factories.ToArray();
        var sourceConfiguration = new CoverageSourceConfiguration();
        _hostedByThisProcess = builder.GetCoverageSourceProjects();
        var hostedByTestHost = new HostedProjectsHandoff();
        builder.TestHostControllers.AddEnvironmentVariableProvider(_ => hostedByTestHost);

        async Task<ReportCoverage> AnalyzeAsync(
            FileInfo file, IReadOnlyCollection<string> hostedProjectFiles, WarningScreen? screen, CancellationToken token)
        {
            if (sourceConfiguration.Error is { } error)
            {
                return new ReportCoverage { Status = "unavailable", Diagnostics = [error] };
            }

            var analysis = await ClassifiedCoverageAnalyzer.AnalyzeWithWarningsAsync(
                file.FullName, invocationDirectory, sourceConfiguration.Scope, factories, hostedProjectFiles, token).ConfigureAwait(false);
            if (screen is not null)
            {
                foreach (var warning in analysis.Warnings)
                {
                    await screen.Output.DisplayAsync(screen.Producer, new WarningMessageOutputDeviceData("Raun coverage: " + warning), token).ConfigureAwait(false);
                }
            }

            return analysis.Report;
        }

        _coordinator = new CoverageSessionCoordinator((file, token) => AnalyzeAsync(file, _hostedByThisProcess.Paths, _testHostScreen, token));
        builder.TestHost.AddDataConsumer(services =>
        {
            var options = services.GetCommandLineOptions();
            var enabled = options.IsOptionSet("report-html") && (options.IsOptionSet("coverage") || options.IsOptionSet("coverlet"));
            if (enabled) { sourceConfiguration.Initialize(services, invocationDirectory); }
            var consumer = new CoverageArtifactConsumer(_coordinator, enabled);
            _testHostScreen = WarningScreen.From(services, consumer);
            return consumer;
        });

        builder.TestHostControllers.AddProcessLifetimeHandler(services =>
        {
            var options = services.GetCommandLineOptions();
            var reportPath = options.IsOptionSet("coverlet") ? Raun.Mtp.HtmlReport.HtmlReportPath.Resolve(services) : null;
            if (reportPath is not null) { sourceConfiguration.Initialize(services, invocationDirectory); }
            WarningScreen? controllerScreen = null;
            var enricher = new CoverletReportEnricher(
                () => (services.GetService(typeof(ITestCoverageResult)) as ITestCoverageResult)?.Reports ?? [], reportPath,
                (file, token) => AnalyzeAsync(file, hostedByTestHost.Take(), controllerScreen, token));
            controllerScreen = WarningScreen.From(services, enricher);
            return _coverletReport = enricher;
        });
    }

    public Task AttachReportAsync(string session, HtmlReportSink report) => _coordinator.AttachReportAsync(session, report);

    public async Task CompleteAsync()
    {
        if (HostedProjectsHandoff.FileNamedByController is { } file)
        {
            HostedProjectsHandoff.Leave(file, _hostedByThisProcess.Paths);
        }

        if (_coverletReport is not null) { await _coverletReport.EnrichAsync().ConfigureAwait(false); }
        await _coordinator.CompleteAsync().ConfigureAwait(false);
    }

    public void Dispose() => _coordinator.Dispose();

    private sealed record WarningScreen(IOutputDevice Output, IOutputDeviceDataProducer Producer)
    {
        public static WarningScreen? From(IServiceProvider services, IOutputDeviceDataProducer producer)
            => services.GetService(typeof(IOutputDevice)) is IOutputDevice output ? new WarningScreen(output, producer) : null;
    }
}
