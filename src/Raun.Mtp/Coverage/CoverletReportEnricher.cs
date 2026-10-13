using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestHostControllers;
using Microsoft.Testing.Platform.Services;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;

namespace Raun.Mtp.Coverage;

// Coverlet publishes on the controller bus after the instrumented test host exits.
// The public platform read model supplies exact artifact paths; its coverage session ID
// is not the framework session ID, so this whole-run hand-off updates the saved report.
internal sealed class CoverletReportEnricher(
    Func<IReadOnlyList<CoverageReportReference>> reports,
    string? reportPath,
    Func<FileInfo, CancellationToken, Task<ReportCoverage>> analyze) : ITestHostProcessLifetimeHandler
{
    private DateTime? _previousReportWrite;
    public string Uid => "raun.mtp.coverletreport";
    public string Version => "1.0.0";
    public string DisplayName => "Raun Coverlet report enrichment";
    public string Description => "Enriches the completed HTML report from the controller's correlated coverage artifact.";
    public Task<bool> IsEnabledAsync() => Task.FromResult(reportPath is not null);
    public Task BeforeTestHostProcessStartAsync(CancellationToken cancellationToken)
    {
        _previousReportWrite = reportPath is not null && File.Exists(reportPath) ? File.GetLastWriteTimeUtc(reportPath) : null;
        return Task.CompletedTask;
    }
    public Task OnTestHostProcessStartedAsync(ITestHostProcessInformation testHostProcessInformation, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnTestHostProcessExitedAsync(ITestHostProcessInformation testHostProcessInformation, CancellationToken cancellationToken) => Task.CompletedTask;

    internal static CoverageReportReference? SelectArtifact(IReadOnlyList<CoverageReportReference> reports)
    {
        var selected = reports.Where(report => report.ProducerId == "CoverletExtension"
            && report.Format == CoverageReportFormat.Custom && report.CustomFormatName == "coverlet.json").ToArray();
        return selected.Length switch
        {
            0 => null,
            1 => selected[0],
            _ => throw new InvalidDataException("Several Coverlet JSON artifacts were published; whole-run coverage cannot be correlated unambiguously."),
        };
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Coverage enrichment must preserve the test exit code and completed report.")]
    public async Task EnrichAsync(CancellationToken cancellationToken = default)
    {
        if (reportPath is null || !File.Exists(reportPath)
            || _previousReportWrite is { } previous && File.GetLastWriteTimeUtc(reportPath) <= previous) { return; }
        ReportCoverage coverage;
        try
        {
            var artifact = SelectArtifact(reports());
            coverage = artifact is null
                ? new ReportCoverage { Status = "unavailable", Diagnostics = ["Coverlet published no JSON report artifact. Use --coverlet-output-format json."] }
                : await analyze(new FileInfo(artifact.Path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            NodeDiagnostics.Log("coverage-analysis-failure", error.ToString());
            coverage = new ReportCoverage { Status = "unavailable", Diagnostics = [error.Message] };
        }
        try
        {
            await HtmlReportSink.EnrichFileAsync(reportPath, coverage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { NodeDiagnostics.Log("coverage-report-failure", error.ToString()); }
    }
}
