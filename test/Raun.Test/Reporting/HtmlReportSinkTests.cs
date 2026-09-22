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
        ScenarioId = "scn", DisplayName = "books", MethodName = "Ns.Booking",
        Nodes = [Node(0, "a", "Given", "Given patient Jane exists")],
    };

    private static StepResult Passed(ScenarioNode n) => new()
    {
        Node = n, DisplayName = n.DisplayNameTemplate, Status = StepStatus.Passed,
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
        Assert.Contains("\"scenarioId\": \"scn\"", html, StringComparison.Ordinal); // JSON blob present
        Assert.DoesNotContain("__RAUN_REPORT_JSON__", html, StringComparison.Ordinal); // token replaced
        Assert.Contains("Raun run report", html, StringComparison.Ordinal); // restyled shell present
        Assert.Contains("id=\"chips\"", html, StringComparison.Ordinal); // dashboard header chips present
        Assert.Contains("data-theme", html, StringComparison.Ordinal); // theme override wiring present
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
    public async Task Report_embeds_the_serif_font_and_links_no_external_assets()
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
        // Source Serif 4 embedded as base64 woff2 (no CDN/web-font)
        Assert.Contains("@font-face", html, StringComparison.Ordinal);
        Assert.Contains("Source Serif 4", html, StringComparison.Ordinal);
        Assert.Contains("data:font/woff2;base64,", html, StringComparison.Ordinal);
        // self-contained: no external asset references
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link rel=\"stylesheet\" href=\"http", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=\"http", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renders_the_activity_diagram_and_drops_the_old_overlay()
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
        Assert.Contains("class=\"actdiag\"", html, StringComparison.Ordinal);   // new SVG diagram
        Assert.Contains("buildActivityDiagram", html, StringComparison.Ordinal);
        Assert.DoesNotContain("buildFlowOverlay", html, StringComparison.Ordinal); // old overlay gone
        Assert.DoesNotContain("flow-svg", html, StringComparison.Ordinal);
        // preserved shell (already asserted elsewhere, re-checked here for this render path)
        Assert.Contains("class=\"drill", html, StringComparison.Ordinal);
        Assert.Contains("data-theme", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_diagram_is_kept_but_not_rendered_and_resources_become_chips()
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
        // the activity diagram stays in the template but is switched off; the drill opens by default instead
        Assert.Contains("const SHOW_DIAGRAM = false;", html, StringComparison.Ordinal);
        Assert.Contains("if (SHOW_DIAGRAM) card.appendChild(buildActivityDiagram(sc, rerender));", html, StringComparison.Ordinal);
        Assert.Contains("if (!SHOW_DIAGRAM) drill.classList.add(\"open\");", html, StringComparison.Ordinal);
        // resource mentions in logs and effects render as clickable chips keyed by identity
        Assert.Contains("class=\"res-chip\"", html, StringComparison.Ordinal);
        Assert.Contains("data-res=", html, StringComparison.Ordinal);
        Assert.Contains("function logHtml(", html, StringComparison.Ordinal);
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
        Assert.Contains("\"waitedMs\": 250", html, StringComparison.Ordinal);
        Assert.Contains("\"waitedFor\": \"ExclusiveDb\"", html, StringComparison.Ordinal);
        Assert.Contains("waited ", html, StringComparison.Ordinal); // the header line's template text
    }

    private sealed class TestTimeProviderUtc(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
