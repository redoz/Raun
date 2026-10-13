using System.Text.Json;
using Raun.Reporting;
using Raun.Reporting.Coverage;
using Raun.Reporting.Html;
using Xunit;

namespace Raun.Test;

public sealed class CoverageRenderingTests
{
    [Fact]
    public async Task Report_retains_coverage_payload_without_the_experimental_interface()
    {
        var directory = Path.Combine(Path.GetTempPath(), "raun-coverage-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "report.html");
            var category = new ReportCoverageCategory(BranchCategory.Logic,
                new ReportCoverageCounts { Inventory = 12, Complete = 7, DecisionMeasurable = 10, Unknown = 2 });
            var sink = new HtmlReportSink(path, TimeProvider.System);
            await sink.PublishAsync(new RunStarted(0));
            await sink.PublishAsync(new RunFinished());
            await sink.SetCoverageAsync(new ReportCoverage
            {
                Status = "available", Categories = [category],
                Types = [new ReportCoverageType("Api", "Finance", [category],
                    [new ReportCoverageMethod("CalculateFinanceReport()", [category])])],
            });
            var html = await File.ReadAllTextAsync(path);
            const string marker = "<script id=\"model\" type=\"application/json\">";
            var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            using var payload = JsonDocument.Parse(html[start..end]);
            var data = payload.RootElement.GetProperty("coverage");
            Assert.Equal("available", data.GetProperty("status").GetString());
            Assert.Equal(7, data.GetProperty("categories")[0].GetProperty("counts").GetProperty("complete").GetInt32());
            Assert.Equal("CalculateFinanceReport()", data.GetProperty("types")[0].GetProperty("methods")[0].GetProperty("signature").GetString());
            Assert.Contains("function buildScenarioCard", html, StringComparison.Ordinal);
            Assert.DoesNotContain("function buildCoverageReport", html, StringComparison.Ordinal);
            Assert.DoesNotContain(".resource-inspector{", html, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }
}
