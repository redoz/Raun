using System.Text.Json;
using Raun.Model;
using Raun.Reporting;
using Raun.Reporting.Html;
using Xunit;

namespace Raun.Test;

public sealed class HtmlReportSinkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raun-report-test-" + Guid.NewGuid().ToString("N"));

    public HtmlReportSinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static ScenarioNode Node(int i, string id, string phase, string t) => new()
    {
        Index = i, StepId = id, Phase = phase, OperationName = "Op" + i, DisplayNameTemplate = t,
        DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def() => new()
    {
        ScenarioId = "scn", DisplayName = "books", MethodName = "Ns.Booking", Namespace = "", TypeName = "Ns",
        Nodes = [Node(0, "a", "Given", "Given patient Jane exists")],
    };

    private static StepResult Passed(ScenarioNode n) => new()
    {
        Node = n, TitleMessage = StepMessage.FromText(n.DisplayNameTemplate), Status = StepStatus.Passed,
        StartedAt = T0, Duration = TimeSpan.FromMilliseconds(42),
    };

    [Fact]
    public async Task Writes_a_self_contained_html_file_on_run_finished()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();

        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));

        Assert.False(File.Exists(path)); // not written until RunFinished

        await sink.PublishAsync(new RunFinished());

        Assert.True(File.Exists(path));
        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("books", html, StringComparison.Ordinal); // scenario name present
        Assert.Contains("\"scenarioId\":\"scn\"", html, StringComparison.Ordinal); // JSON blob present
        Assert.DoesNotContain("__RAUN_REPORT_JSON__", html, StringComparison.Ordinal); // token replaced
        Assert.Contains("Raun run report", html, StringComparison.Ordinal); // restyled shell present
        Assert.Contains("id=\"report\"", html, StringComparison.Ordinal); // interactive report host present
        Assert.Contains("data-theme", html, StringComparison.Ordinal); // theme override wiring present
    }

    [Fact]
    public async Task Unicode_stays_readable_and_captured_text_cannot_close_the_json_script()
    {
        var path = Path.Combine(_dir, "unicode.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        const string message = "Åsa + Zoë </script><script>alert(1)</script>";
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0], TitleMessage = StepMessage.FromText(message), Status = StepStatus.Passed, StartedAt = T0,
            LogEntries = [new LogEntry(TimeSpan.Zero, message)],
        }));
        await sink.PublishAsync(new RunFinished());
        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("Åsa + Zoë", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("\\u003C/script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_run_still_writes_a_valid_report()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));

        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_write_failure_is_recorded_on_the_bus_not_thrown()
    {
        // Target a path whose directory does not exist and cannot be created (a file as a dir segment).
        var fileAsDir = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(fileAsDir, "x");
        var badPath = Path.Combine(fileAsDir, "nested", "report.html");
        var bus = new RunEventBus([new HtmlReportSink(badPath, new TestTimeProviderUtc(T0))]);

        await bus.PublishAsync(new RunStarted(0));
        var ex = await Record.ExceptionAsync(async () => await bus.PublishAsync(new RunFinished()));

        Assert.Null(ex);                 // never thrown into the run
        Assert.Single(bus.Failures);     // recorded for the framework to log
    }

    [Fact]
    public async Task A_report_written_as_the_host_is_terminated_shows_nothing_cut_short_as_passed()
    {
        var path = Path.Combine(_dir, "aborted.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        ScenarioDefinition Scenario(string id) => new()
        {
            ScenarioId = id, DisplayName = id, MethodName = "Ns." + id, Namespace = "", TypeName = "Ns",
            Nodes = [Node(0, id + "-a", "Given", "first"), Node(1, id + "-b", "When", "second")],
        };
        var hung = Scenario("hung");
        var betweenSteps = Scenario("between");
        var done = Scenario("done");

        await sink.PublishAsync(new RunStarted(3));
        foreach (var scenario in new[] { hung, betweenSteps, done })
        {
            await sink.PublishAsync(new ScenarioStarted(scenario));
            await sink.PublishAsync(new StepStarted(scenario, new() { Node = scenario.Nodes[0], DisplayName = "first" }));
            await sink.PublishAsync(new StepFinished(scenario, Passed(scenario.Nodes[0])));
        }
        await sink.PublishAsync(new StepStarted(hung, new() { Node = hung.Nodes[1], DisplayName = "second, as formatted" }));
        await sink.PublishAsync(new StepStarted(done, new() { Node = done.Nodes[1], DisplayName = "second" }));
        await sink.PublishAsync(new StepFinished(done, Passed(done.Nodes[1])));
        await sink.PublishAsync(new ScenarioFinished(done, [Passed(done.Nodes[0]), Passed(done.Nodes[1])]));

        await sink.PublishAsync(new RunFinished { AbortReason = "step 'second' did not stop" });

        var html = await File.ReadAllTextAsync(path);
        const string marker = "<script id=\"model\" type=\"application/json\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        using var model = JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]);
        var scenarios = model.RootElement.GetProperty("scenarios").EnumerateArray()
            .ToDictionary(s => s.GetProperty("scenarioId").GetString()!);

        // The step that was running has no result: it is shown as the failure it is.
        Assert.Equal("failed", scenarios["hung"].GetProperty("status").GetString());
        var unfinished = scenarios["hung"].GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("index").GetInt32() == 1);
        Assert.Equal("failed", unfinished.GetProperty("status").GetString());
        Assert.Equal("second, as formatted", unfinished.GetProperty("displayName").GetString());
        Assert.Contains("did not finish", unfinished.GetProperty("exception").GetString(), StringComparison.Ordinal);
        // Stopped between two steps: what ran passed, the scenario did not.
        Assert.Equal("skipped", scenarios["between"].GetProperty("status").GetString());
        Assert.Equal("passed", scenarios["done"].GetProperty("status").GetString());
        Assert.Equal(1, model.RootElement.GetProperty("summary").GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task Report_omits_graph_fonts_and_links_no_external_assets()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        // The retired graph was the only consumer of the embedded serif fonts.
        Assert.DoesNotContain("@font-face", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Source Serif 4", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data:font/woff2;base64,", html, StringComparison.Ordinal);
        // self-contained: no external asset references
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\" href=\"http", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=\"http", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renders_the_resource_matrix_without_retired_graph_renderers()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("resource-matrix", html, StringComparison.Ordinal);
        Assert.DoesNotContain("buildActivityDiagram", html, StringComparison.Ordinal);
        Assert.DoesNotContain("buildFlowOverlay", html, StringComparison.Ordinal); // old overlay gone
        Assert.DoesNotContain("flow-svg", html, StringComparison.Ordinal);
        // preserved shell (already asserted elsewhere, re-checked here for this render path)
        Assert.Contains("function buildScenarioDrill", html, StringComparison.Ordinal);
        Assert.Contains("data-theme", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_default_drill_uses_matrix_resources_and_preserves_log_chips()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("SHOW_DIAGRAM", html, StringComparison.Ordinal);
        Assert.DoesNotContain("buildObjectFlow", html, StringComparison.Ordinal);
        Assert.Contains("buildResourceMatrix", html, StringComparison.Ordinal);
        Assert.Contains("drill.classList.add(\"open\");", html, StringComparison.Ordinal);
        // resource mentions in logs and effects render as clickable chips keyed by identity
        Assert.Contains("class=\"res-chip\"", html, StringComparison.Ordinal);
        Assert.Contains("data-res=", html, StringComparison.Ordinal);
        Assert.Contains("function logHtml(", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenarios_are_one_compact_list_that_expands_in_place()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        // Rendering and expansion are exercised by the real-template Node suite.
        // The sink must preserve the canonical scenario and step records consumed by that UI.
        const string marker = "<script id=\"model\" type=\"application/json\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        using var data = JsonDocument.Parse(html[start..end]);
        var scenario = Assert.Single(data.RootElement.GetProperty("scenarios").EnumerateArray());
        Assert.Equal(def.ScenarioId, scenario.GetProperty("scenarioId").GetString());
        Assert.Equal(def.MethodName, scenario.GetProperty("methodName").GetString());
        var step = Assert.Single(scenario.GetProperty("steps").EnumerateArray());
        Assert.Equal("passed", step.GetProperty("status").GetString());
        Assert.Equal("Given patient Jane exists", step.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Attachments_are_preserved_as_captured_data()
    {
        var path = Path.Combine(_dir, "raun-report-att.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();
        const string captured = "first\n<script>unsafe</script>";
        var result = new StepResult
        {
            Node = def.Nodes[0], TitleMessage = StepMessage.FromText(def.Nodes[0].DisplayNameTemplate), Status = StepStatus.Passed,
            StartedAt = T0, Attachments = new Dictionary<string, string> { ["response"] = captured },
        };
        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, result));
        await sink.PublishAsync(new ScenarioFinished(def, [result]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        const string marker = "<script id=\"model\" type=\"application/json\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        using var data = JsonDocument.Parse(html[start..end]);
        var attachment = Assert.Single(data.RootElement.GetProperty("scenarios")[0].GetProperty("steps")[0]
            .GetProperty("attachments").EnumerateArray());
        Assert.Equal("response", attachment.GetProperty("name").GetString());
        Assert.Equal(captured, attachment.GetProperty("value").GetString());
        Assert.DoesNotContain("<script>unsafe", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wait_reaches_the_json_and_the_template_renders_it()
    {
        var path = Path.Combine(_dir, "raun-report-wait.html");
        var sink = new HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();

        await sink.PublishAsync(new RunStarted(1, [def]));
        await sink.PublishAsync(new ScenarioStarted(def, TimeSpan.FromMilliseconds(250), typeof(ExclusiveDb)));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("\"waitedMs\":250", html, StringComparison.Ordinal);
        Assert.Contains("\"waitedFor\":\"ExclusiveDb\"", html, StringComparison.Ordinal);
        Assert.Contains("Queued", html, StringComparison.Ordinal); // the header line's template text
    }

    private sealed class TestTimeProviderUtc(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
