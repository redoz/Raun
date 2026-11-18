using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.TestHost;
using System.Diagnostics.CodeAnalysis;
using Raun.Coverage;
using Raun.Mtp.Coverage;
using Raun.Reporting;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;
using Xunit;

namespace Raun.Mtp.Test;

public sealed class CoveragePipelineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "raun-delivery-" + Guid.NewGuid().ToString("N"));
    public CoveragePipelineTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { Directory.Delete(_directory, true); GC.SuppressFinalize(this); }

    [Fact]
    public async Task External_middleware_registration_is_additive_and_application_scoped()
    {
        var first = await TestApplication.CreateBuilderAsync([]);
        var second = await TestApplication.CreateBuilderAsync([]);
        first.UseBranchClassificationMiddleware(() => new ProjectRule());
        first.UseBranchClassificationMiddleware(() => new ProjectRule());
        Assert.Equal(2, CoverageRegistration.For(first).Factories.Count);
        Assert.Empty(CoverageRegistration.For(second).Factories);
        Assert.IsAssignableFrom<IBranchClassificationMiddleware>(CoverageRegistration.For(first).Factories[0]());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delivery_is_awaited_in_either_order_and_duplicates_do_not_reanalyze(bool artifactFirst)
    {
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new CoverageSessionCoordinator(async (_, _) =>
        {
            calls++;
            started.TrySetResult();
            await gate.Task;
            return new ReportCoverage { Status = "available", Diagnostics = ["project rule applied"] };
        });
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System, true);
        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());
        if (!artifactFirst) { await coordinator.AttachReportAsync("session", sink); }
        var file = Path.Combine(_directory, "coverage.xml");
        await File.WriteAllTextAsync(file, "<results><modules /></results>");
        var delivery = coordinator.AcceptArtifactAsync("session", new FileInfo(file), TestContext.Current.CancellationToken);
        await started.Task;
        Assert.False(delivery.IsCompleted);
        gate.TrySetResult();
        await delivery;
        if (artifactFirst) { await coordinator.AttachReportAsync("session", sink); }
        await coordinator.AcceptArtifactAsync("session", new FileInfo(file), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Contains("project rule applied", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_analysis_preserves_report_and_exposes_unavailable_diagnostic()
    {
        using var coordinator = new CoverageSessionCoordinator((_, _) => throw new InvalidDataException("invalid XML"));
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System);
        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());
        await coordinator.AttachReportAsync("session", sink);
        var file = Path.Combine(_directory, "coverage.xml");
        await File.WriteAllTextAsync(file, "bad");
        await coordinator.AcceptArtifactAsync("session", new FileInfo(file), TestContext.Current.CancellationToken);
        Assert.Contains("invalid XML", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Contains("Raun run report", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifact_consumer_accepts_verified_uid_and_ignores_foreign_producers()
    {
        var calls = 0;
        using var coordinator = new CoverageSessionCoordinator((_, _) => { calls++; return Task.FromResult(new ReportCoverage()); });
        var consumer = new CoverageArtifactConsumer(coordinator, true);
        var file = Path.Combine(_directory, "coverage.xml");
        await File.WriteAllTextAsync(file, "<results><modules /></results>");
        var artifact = new SessionFileArtifact(new SessionUid("session"), new FileInfo(file), "Code Coverage", "coverage");
        await consumer.ConsumeAsync(new Producer("foreign"), artifact, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        await consumer.ConsumeAsync(new Producer("TestingPlatformCoverageDynamicTestSessionLifetimeHandler"), artifact, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.False(await new CoverageArtifactConsumer(coordinator, false).IsEnabledAsync());
    }

    [Fact]
    public void Controller_uses_exact_correlated_artifact_and_rejects_multiple_runs()
    {
        static Microsoft.Testing.Platform.Services.CoverageReportReference Reference(string producer, string path)
            => new(new SessionUid("coverage-id"), path, CoverageReportFormat.Custom, producer, "coverlet.json");
        var expected = Reference("CoverletExtension", "/actual/run.json");
        Assert.Same(expected, CoverletReportEnricher.SelectArtifact([Reference("foreign", "/stale.json"), expected]));
        Assert.Null(CoverletReportEnricher.SelectArtifact([Reference("foreign", "/stale.json")]));
        Assert.Throws<InvalidDataException>(() => CoverletReportEnricher.SelectArtifact([expected, Reference("CoverletExtension", "/other.json")]));
    }

    [Fact]
    public async Task Cancellation_before_delivery_does_not_escape_to_collector()
    {
        using var coordinator = new CoverageSessionCoordinator((_, _) => throw new InvalidOperationException("must not analyze"));
        await coordinator.AcceptArtifactAsync("session", new FileInfo(Path.Combine(_directory, "missing")), new CancellationToken(true));
        await coordinator.DrainAsync();
    }

    private async Task<(HtmlReportSink Sink, string Path)> FinishedReportAsync()
    {
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System, true);
        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());
        return (sink, path);
    }

    [Fact]
    public async Task An_analysis_that_does_not_finish_in_time_leaves_the_run_and_says_coverage_is_unavailable()
    {
        // The collector waits for this delivery before the host may exit. An analysis that hangs
        // (a child msbuild that never returns, say) must cost the report its coverage view, not the
        // run its exit.
        using var coordinator = new CoverageSessionCoordinator(
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new ReportCoverage(); },
            analysisTimeout: TimeSpan.FromMilliseconds(100));
        var (sink, path) = await FinishedReportAsync();
        await coordinator.AttachReportAsync("session", sink);
        var file = Path.Combine(_directory, "coverage.xml");
        await File.WriteAllTextAsync(file, "<results><modules /></results>", TestContext.Current.CancellationToken);

        await coordinator.AcceptArtifactAsync("session", new FileInfo(file), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":\"unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("did not finish within", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_that_never_received_coverage_data_says_so_when_the_run_completes()
    {
        // Cancelled before the delivery could start, or a collector that produced nothing: the
        // report must not go on saying it is waiting for coverage data.
        using var coordinator = new CoverageSessionCoordinator((_, _) => throw new InvalidOperationException("must not analyze"));
        var (sink, path) = await FinishedReportAsync();
        await coordinator.AttachReportAsync("session", sink);
        await coordinator.AcceptArtifactAsync("session", new FileInfo(Path.Combine(_directory, "missing")), new CancellationToken(true));

        await coordinator.CompleteAsync();

        var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":\"unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("No coverage data was received", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completing_the_run_leaves_received_coverage_alone()
    {
        using var coordinator = new CoverageSessionCoordinator(
            (_, _) => Task.FromResult(new ReportCoverage { Status = "available", Diagnostics = ["analysed"] }));
        var (sink, path) = await FinishedReportAsync();
        await coordinator.AttachReportAsync("session", sink);
        var file = Path.Combine(_directory, "coverage.xml");
        await File.WriteAllTextAsync(file, "<results><modules /></results>", TestContext.Current.CancellationToken);
        await coordinator.AcceptArtifactAsync("session", new FileInfo(file), TestContext.Current.CancellationToken);

        await coordinator.CompleteAsync();

        var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"status\":\"available\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("No coverage data was received", html, StringComparison.Ordinal);
    }

    private sealed class Producer(string uid) : IDataProducer
    {
        [SuppressMessage("Design", "CA1819:Properties should not return arrays", Justification = "Required by MTP.")]
        public Type[] DataTypesProduced => [typeof(SessionFileArtifact)];
        public string Uid => uid;
        public string Version => "1.0";
        public string DisplayName => "coverage";
        public string Description => "test";
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);
    }

    private sealed class ProjectRule : IBranchClassificationMiddleware
    {
        public BranchCategory Classify(BranchClassificationContext context, BranchClassificationNext next) => next();
    }
}
