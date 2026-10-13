using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;

namespace Raun.Mtp.Coverage;

internal sealed class CoverageSessionCoordinator(Func<FileInfo, CancellationToken, Task<ReportCoverage>> analyze) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public async Task AttachReportAsync(string session, HtmlReportSink report)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = State(session);
            state.Report = report;
            if (state.Coverage is not null)
            {
                await report.SetCoverageAsync(state.Coverage).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Coverage enrichment is best effort and must preserve test results even when project analysis fails.")]
    public async Task AcceptArtifactAsync(string session, FileInfo file, CancellationToken cancellationToken)
    {
        try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            var state = State(session);
            ReportCoverage coverage;
            try
            {
                using var stream = file.OpenRead();
                var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (state.Artifacts.Contains(checksum))
                {
                    return;
                }
                coverage = await analyze(file, cancellationToken).ConfigureAwait(false);
                state.Artifacts.Add(checksum);
            }
            catch (Exception error)
            {
                coverage = new ReportCoverage { Status = "unavailable", Diagnostics = [error.Message] };
                NodeDiagnostics.Log("coverage-analysis-failure", error.ToString());
            }
            state.Coverage = coverage;
            if (state.Report is not null)
            {
                try
                {
                    await state.Report.SetCoverageAsync(coverage, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    NodeDiagnostics.Log("coverage-report-failure", error.ToString());
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DrainAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    private SessionState State(string session)
    {
        if (!_sessions.TryGetValue(session, out var state))
        {
            state = new SessionState();
            _sessions.Add(session, state);
        }
        return state;
    }

    public void Dispose() { _gate.Dispose(); _sessions.Clear(); }

    private sealed class SessionState
    {
        public HtmlReportSink? Report { get; set; }
        public ReportCoverage? Coverage { get; set; }
        public HashSet<string> Artifacts { get; } = new(StringComparer.Ordinal);
    }
}

