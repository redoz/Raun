using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Preflight is the run-level mirror of teardown: setup that must happen once, before any scenario,
/// reported as its own node so a failure is a failing test rather than a process that exits before
/// anything reports.
/// </summary>
public class PreflightTests
{
    private static ScenarioNode Node(
        int index,
        string stepId,
        Func<IStepInputs, ScenarioContext, Task<object?>>? invoke = null) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = [],
        Invoke = invoke ?? ((_, _) => Task.FromResult<object?>(null)),
    };

    private static ScenarioDefinition Definition(string id, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = id,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
    };

    private sealed class Recorder : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];

        public ValueTask PublishAsync(RunEvent evt)
        {
            lock (Events) { Events.Add(evt); }
            return default;
        }

        public IEnumerable<StepFinished> Finished => Events.OfType<StepFinished>();
    }

    private static async Task<Recorder> Run(
        Func<ScenarioContext, Task>? preflight,
        params ScenarioDefinition[] definitions)
    {
        var sink = new Recorder();
        await new RunLoop(() => definitions, preflight: preflight)
            .RunAsync(selector: null, sink, CancellationToken.None);
        return sink;
    }

    [Fact]
    public async Task Preflight_runs_once_before_any_scenario_step()
    {
        var order = new List<string>();

        await Run(
            _ => { order.Add("preflight"); return Task.CompletedTask; },
            Definition("a", Node(0, "x", (_, _) => { order.Add("a"); return Task.FromResult<object?>(null); })),
            Definition("b", Node(0, "y", (_, _) => { order.Add("b"); return Task.FromResult<object?>(null); })));

        Assert.Equal(["preflight", "a", "b"], order);
    }

    [Fact]
    public async Task Preflight_runs_once_regardless_of_scenario_count()
    {
        var runs = 0;

        await Run(
            _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; },
            Definition("a", Node(0, "x")),
            Definition("b", Node(0, "y")),
            Definition("c", Node(0, "z")));

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Preflight_reports_a_node_with_a_stable_uid()
    {
        var sink = await Run(_ => Task.CompletedTask, Definition("a", Node(0, "x")));

        var preflight = Assert.Single(sink.Finished, e => e.Result.Node.StepId == "preflight");
        Assert.Equal("raun", preflight.Definition.ScenarioId);
        Assert.Equal(StepStatus.Passed, preflight.Result.Status);
    }

    [Fact]
    public async Task No_preflight_delegate_reports_no_preflight_node()
    {
        var sink = await Run(preflight: null, Definition("a", Node(0, "x")));

        Assert.DoesNotContain(sink.Finished, e => e.Result.Node.StepId == "preflight");
    }

    [Fact]
    public async Task Logs_written_during_preflight_land_on_its_node()
    {
        var sink = await Run(
            ctx => { ctx.Log("starting AppHost"); ctx.Log("postgres healthy"); return Task.CompletedTask; },
            Definition("a", Node(0, "x")));

        var preflight = Assert.Single(sink.Finished, e => e.Result.Node.StepId == "preflight");
        Assert.Equal(["starting AppHost", "postgres healthy"], preflight.Result.Logs);
    }

    [Fact]
    public async Task The_scenario_context_is_ambient_during_preflight()
    {
        // This is what lets an ILogger routed through RaunLoggerProvider be collected into the
        // preflight node without any extra wiring.
        string? seen = null;

        await Run(
            _ => { seen = ScenarioContext.Current?.StepId; return Task.CompletedTask; },
            Definition("a", Node(0, "x")));

        Assert.Equal("preflight", seen);
    }

    [Fact]
    public async Task A_failing_preflight_fails_its_node_and_skips_every_scenario_step()
    {
        var scenarioRan = false;

        var sink = await Run(
            _ => throw new InvalidOperationException("postgres never became healthy"),
            Definition("a",
                Node(0, "x", (_, _) => { scenarioRan = true; return Task.FromResult<object?>(null); }),
                Node(1, "y")));

        Assert.False(scenarioRan);

        var preflight = Assert.Single(sink.Finished, e => e.Result.Node.StepId == "preflight");
        Assert.Equal(StepStatus.Failed, preflight.Result.Status);
        Assert.Contains("postgres never became healthy", preflight.Result.Exception!.ToString(), StringComparison.Ordinal);

        var steps = sink.Finished.Where(e => e.Result.Node.StepId != "preflight").ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, e => Assert.Equal(StepStatus.Skipped, e.Result.Status));
        Assert.All(steps, e => Assert.Contains("preflight failed", e.Result.SkipReason!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_preflight_still_reports_every_scenario_and_finishes_the_run()
    {
        // The run must complete so the report is whole; the failure is attributed to a row rather
        // than to an exit code.
        var sink = await Run(
            _ => throw new InvalidOperationException("boom"),
            Definition("a", Node(0, "x")),
            Definition("b", Node(0, "y")));

        Assert.Contains(sink.Events, e => e is RunFinished);

        // Three, not two: preflight is itself scenario-shaped in the event stream, so it reports its
        // own ScenarioStarted/Finished pair alongside the two real scenarios.
        var finished = sink.Events.OfType<ScenarioFinished>().ToList();
        Assert.Equal(3, finished.Count);
        Assert.Equal(["a", "b", "raun"], finished.Select(e => e.Definition.ScenarioId).Order());
    }
}
