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
        Namespace = "", TypeName = "",
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

    private static ScenarioDefinition Chain() => Definition(
        "chain",
        Node(0, "first"),
        new ScenarioNode
        {
            Index = 1, StepId = "second", Phase = "When", OperationName = "Op1", DisplayNameTemplate = "op 1",
            DependsOn = [0], Invoke = (_, _) => Task.FromResult<object?>(null),
        },
        new ScenarioNode
        {
            Index = 2, StepId = "third", Phase = "Then", OperationName = "Op2", DisplayNameTemplate = "op 2",
            DependsOn = [1], Invoke = (_, _) => Task.FromResult<object?>(null),
        });

    [Fact]
    public async Task A_failed_preflight_reports_only_the_steps_the_run_selected()
    {
        // Running one step from an IDE with a broken preflight: that step and what it needs are
        // skipped naming preflight. The step after it was never part of the run and must not flip.
        var sink = new Recorder();
        var chain = Chain();

        await new RunLoop(() => [chain], preflight: _ => throw new InvalidOperationException("no container runtime"))
            .RunAsync(new UidNodeSelector(new HashSet<string> { "chain:second" }), sink, CancellationToken.None);

        Assert.Equal(
            ["first", "second"],
            sink.Finished.Where(e => e.Definition == chain).Select(e => e.Result.Node.StepId));
        var finished = Assert.Single(sink.Events.OfType<ScenarioFinished>(), e => e.Definition == chain);
        Assert.Equal(
            [Preflight.FailedSkipReason, Preflight.FailedSkipReason, Raun.Scheduling.ScenarioScheduler.NotSelectedSkipReason],
            finished.Results.Select(r => r.SkipReason));
    }

    [Fact]
    public async Task A_cancelled_preflight_skips_the_scenarios_as_cancelled_not_as_a_preflight_failure()
    {
        // The host stops the run while preflight is starting the environment. Nothing failed.
        var sink = new Recorder();
        using var host = new CancellationTokenSource();

        await new RunLoop(() => [Definition("a", Node(0, "x"))], preflight: async ctx =>
            {
                await host.CancelAsync();
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            })
            .RunAsync(selector: null, sink, host.Token);

        var step = Assert.Single(sink.Finished, e => e.Result.Node.StepId == "x");
        Assert.Equal(StepStatus.Skipped, step.Result.Status);
        Assert.Equal(Raun.Scheduling.ScenarioScheduler.CanceledSkipReason, step.Result.SkipReason);
        var scenario = Assert.Single(sink.Events.OfType<ScenarioFinished>(), e => e.Definition.ScenarioId == "a");
        Assert.Equal(ScenarioStatus.Canceled, scenario.Outcome.Status);
    }

    [Fact]
    public async Task Preflight_runs_once_before_any_scenario_step()
    {
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await Run(
            _ => { order.Enqueue("preflight"); return Task.CompletedTask; },
            Definition("a", Node(0, "x", (_, _) => { order.Enqueue("a"); return Task.FromResult<object?>(null); })),
            Definition("b", Node(0, "y", (_, _) => { order.Enqueue("b"); return Task.FromResult<object?>(null); })));

        // The two scenarios run side by side, so only preflight's place is fixed.
        Assert.Equal("preflight", order.First());
        Assert.Equal(["a", "b"], order.Skip(1).Order());
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
    public async Task A_filter_that_selects_nothing_does_not_run_preflight()
    {
        // Otherwise a mistyped filter brings the environment up for nothing and reports one passing test.
        var ran = false;
        var sink = new Recorder();

        await new RunLoop(() => [Definition("a", Node(0, "x"))], preflight: _ => { ran = true; return Task.CompletedTask; })
            .RunAsync(new UidNodeSelector(new HashSet<string> { "no-such-scenario:no-such-step" }), sink, CancellationToken.None);

        Assert.False(ran);
        Assert.Empty(sink.Finished);
        Assert.Empty(Assert.Single(sink.Events.OfType<RunStarted>()).Scenarios!);
    }

    [Fact]
    public async Task A_filter_that_selects_a_step_still_runs_preflight()
    {
        var ran = false;
        var definition = Definition("a", Node(0, "x"));
        var sink = new Recorder();

        await new RunLoop(() => [definition], preflight: _ => { ran = true; return Task.CompletedTask; })
            .RunAsync(new UidNodeSelector(new HashSet<string> { StepUid.Of(definition, definition.Nodes[0]) }), sink, CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_cleanup_registered_in_preflight_fails_it_instead_of_being_dropped()
    {
        // Preflight has no teardown node, so nothing would ever call the cleanup.
        var sink = await Run(
            ctx => { ctx.OnTeardown(Cleanup.Required, () => Task.CompletedTask); return Task.CompletedTask; },
            Definition("a", Node(0, "x")));

        var preflight = Assert.Single(sink.Finished, e => e.Result.Node.StepId == "preflight");
        Assert.Equal(StepStatus.Failed, preflight.Result.Status);
        Assert.Contains("no teardown", preflight.Result.Exception!.Message, StringComparison.Ordinal);
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
        Assert.All(steps, e => Assert.Null(e.Result.StartedAt)); // they never reached the scheduler
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

    [Fact]
    public void The_preflight_definition_states_its_own_identity()
    {
        // Core must not lean on an adapter's fallback split of MethodName for its own node.
        var def = Preflight.Definition(_ => Task.CompletedTask);

        Assert.Equal("Raun", def.Namespace);
        Assert.Equal("Preflight", def.TypeName);
        Assert.Equal("Raun.Preflight", def.MethodName);
    }

    [Fact]
    public void A_null_preflight_delegate_is_rejected_up_front()
        => Assert.Throws<ArgumentNullException>(() => Preflight.Definition(null!));
}
