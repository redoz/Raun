using Raun.Reporting;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;
using Xunit;

namespace Raun.Test;

public sealed class CoverageEnrichmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "raun-enrichment-" + Guid.NewGuid().ToString("N"));
    public CoverageEnrichmentTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { Directory.Delete(_directory, true); GC.SuppressFinalize(this); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Enriches_in_either_arrival_order_and_preserves_run_timestamp(bool coverageFirst)
    {
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System, coverageRequested: true);
        var coverage = new ReportCoverage { Status = "available", Diagnostics = ["classified fixture"] };
        await sink.PublishAsync(new RunStarted(0));
        if (coverageFirst) { await sink.SetCoverageAsync(coverage); }
        await sink.PublishAsync(new RunFinished());
        var initial = await File.ReadAllTextAsync(path);
        var timestamp = initial.Split("\"generatedAtUtc\": \"", StringSplitOptions.None)[1].Split('"')[0];
        if (!coverageFirst) { await sink.SetCoverageAsync(coverage); }
        var enriched = await File.ReadAllTextAsync(path);
        Assert.Contains("classified fixture", enriched, StringComparison.Ordinal);
        Assert.Contains(timestamp, enriched, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"available\"", enriched, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Failed_enrichment_preserves_the_original_report_and_cleans_temporary_file()
    {
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System);
        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());
        var original = await File.ReadAllBytesAsync(path);
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => sink.SetCoverageAsync(new ReportCoverage { Status = "available" }));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        await sink.SetCoverageAsync(new ReportCoverage { Status = "available" });
        Assert.Contains("\"status\": \"available\"", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_updates_write_complete_documents()
    {
        var path = Path.Combine(_directory, "report.html");
        var sink = new HtmlReportSink(path, TimeProvider.System);
        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => sink.SetCoverageAsync(
            new ReportCoverage { Status = "available", Diagnostics = ["iteration " + i] })));
        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__RAUN_REPORT_JSON__", html, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
    [Fact]
    public async Task Controller_enrichment_preserves_existing_payload_and_escapes_source_text()
    {
        var path = Path.Combine(_directory, "report.html");
        const string original = """
        <html><script id="model" type="application/json">{"generatedAtUtc":"keep","summary":{"passed":16},"future":{"keep":true},"coverage":{"status":"pending"}}</script><p>unchanged shell</p></html>
        """;
        await File.WriteAllTextAsync(path, original);
        await HtmlReportSink.EnrichFileAsync(path, new ReportCoverage { Status = "available", Diagnostics = ["</script><script>attack</script>"] });
        var html = await File.ReadAllTextAsync(path);
        var start = html.IndexOf('>') + 1;
        var jsonStart = html.IndexOf('>', start) + 1;
        using var json = System.Text.Json.JsonDocument.Parse(html[jsonStart..html.IndexOf("</script>", StringComparison.Ordinal)]);
        Assert.Equal("keep", json.RootElement.GetProperty("generatedAtUtc").GetString());
        Assert.True(json.RootElement.GetProperty("future").GetProperty("keep").GetBoolean());
        Assert.Equal(16, json.RootElement.GetProperty("summary").GetProperty("passed").GetInt32());
        Assert.Equal("available", json.RootElement.GetProperty("coverage").GetProperty("status").GetString());
        Assert.DoesNotContain("<script>attack", html, StringComparison.Ordinal);
        Assert.EndsWith("</script><p>unchanged shell</p></html>", html, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("<html>unrelated</html>")]
    [InlineData("<script id=\"model\" type=\"application/json\">broken</script>")]
    public async Task Controller_enrichment_rejects_invalid_reports_without_overwriting_them(string original)
    {
        var path = Path.Combine(_directory, "report.html");
        await File.WriteAllTextAsync(path, original);
        var failure = await Record.ExceptionAsync(() => HtmlReportSink.EnrichFileAsync(path, new ReportCoverage { Status = "available" }));
        Assert.NotNull(failure);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
}
