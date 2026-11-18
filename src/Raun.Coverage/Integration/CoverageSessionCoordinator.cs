using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;

namespace Raun.Mtp.Coverage;

internal sealed class CoverageSessionCoordinator(
    Func<FileInfo, CancellationToken, Task<ReportCoverage>> analyze, TimeSpan? analysisTimeout = null) : IDisposable
{
    /// <summary>
    /// How long one analysis may take. The collector's delivery is awaited before the test host may
    /// exit, so an analysis without a bound (it opens the solution and starts <c>dotnet msbuild</c>
    /// per project) is a test run that hangs at its end with nothing on the screen.
    /// </summary>
    internal static readonly TimeSpan DefaultAnalysisTimeout = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _analysisTimeout = analysisTimeout ?? DefaultAnalysisTimeout;
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
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                using var stream = file.OpenRead();
                var checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (state.Artifacts.Contains(checksum))
                {
                    return;
                }
                budget.CancelAfter(_analysisTimeout);

                // WaitAsync as well: an analysis that ignores its token must not hold the run either.
                coverage = await analyze(file, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
                state.Artifacts.Add(checksum);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                coverage = new ReportCoverage
                {
                    Status = "unavailable",
                    Diagnostics = [$"Coverage analysis did not finish within {_analysisTimeout} and was stopped."],
                };
                Raun.Diagnostics.DebugTrace.Write("coverage-analysis-timeout", _analysisTimeout.ToString());
            }
            catch (Exception error)
            {
                coverage = new ReportCoverage { Status = "unavailable", Diagnostics = [error.Message] };
                Raun.Diagnostics.DebugTrace.Write("coverage-analysis-failure", error.ToString());
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
                    Raun.Diagnostics.DebugTrace.Write("coverage-report-failure", error.ToString());
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

    /// <summary>
    /// Ends the run for coverage: waits for a delivery in progress, then tells every report that
    /// asked for coverage and received none that none is coming. Without this such a report says
    /// "waiting for coverage data" forever.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Coverage enrichment is best effort and must preserve test results.")]
    public async Task CompleteAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var state in _sessions.Values)
            {
                if (state.Report is null || state.Coverage is not null)
                {
                    continue;
                }

                state.Coverage = new ReportCoverage
                {
                    Status = "unavailable",
                    Diagnostics = ["No coverage data was received before the run ended."],
                };
                try
                {
                    await state.Report.SetCoverageAsync(state.Coverage, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Raun.Diagnostics.DebugTrace.Write("coverage-report-failure", error.ToString());
                }
            }
        }
        finally { _gate.Release(); }
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
