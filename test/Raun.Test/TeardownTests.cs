using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Cleanup is registered by the step that created the thing, so the closure captures both the object
/// and the connection. The log is scenario-scoped and written concurrently by parallel steps.
/// </summary>
public class TeardownTests
{
    private static ScenarioContext Context(string stepId, TeardownLog log, int stepIndex)
    {
        var ctx = new ScenarioContext(stepId, stepId, services: null, CancellationToken.None);
        ctx.AttachTeardown(log, stepIndex);
        return ctx;
    }

    [Fact]
    public void Registrations_record_their_owning_step_and_sequence()
    {
        var log = new TeardownLog();
        var ctx = Context("a", log, stepIndex: 3);

        ctx.OnTeardown(() => Task.CompletedTask);
        ctx.OnTeardown(Cleanup.Required, () => Task.CompletedTask);

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal(3, e.OwningStepIndex));
        Assert.Equal(Cleanup.Optional, log.Entries[0].Kind);
        Assert.Equal(Cleanup.Required, log.Entries[1].Kind);
        Assert.True(log.Entries[1].Sequence > log.Entries[0].Sequence);
    }

    [Fact]
    public void A_context_with_no_log_attached_ignores_registration()
    {
        // A context built outside the scheduler (a DSL method under unit test) must not throw.
        var ctx = new ScenarioContext("a", "a", services: null, CancellationToken.None);

        ctx.OnTeardown(() => Task.CompletedTask);   // must not throw
    }

    [Fact]
    public void Concurrent_registration_keeps_every_entry()
    {
        var log = new TeardownLog();

        Parallel.For(0, 200, i =>
        {
            var ctx = Context("s" + i, log, i);
            ctx.OnTeardown(() => Task.CompletedTask);
        });

        Assert.Equal(200, log.Entries.Count);
        Assert.Equal(200, log.Entries.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public void Node_is_not_a_teardown_node_by_default()
    {
        var node = new ScenarioNode
        {
            Index = 0,
            StepId = "s",
            Phase = "Given",
            OperationName = "Op",
            DisplayNameTemplate = "op",
            DependsOn = [],
            Invoke = (_, _) => Task.FromResult<object?>(null),
        };

        Assert.False(node.IsTeardown);
    }

    [Fact]
    public void Definition_defaults_to_running_teardown_always()
    {
        var def = new ScenarioDefinition
        {
            ScenarioId = "s",
            DisplayName = "s",
            MethodName = "Ns.S",
            Nodes = [],
        };

        Assert.Equal(Run.Always, def.TeardownPolicy);
    }

    private static ScenarioNode Step(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[]? dependsOn = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn ?? [],
        Invoke = invoke,
    };

    private static ScenarioNode TeardownNode(int index) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Then",
        OperationName = "Teardown",
        DisplayNameTemplate = "Teardown",
        DependsOn = [],
        IsTeardown = true,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def(Run policy, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        TeardownPolicy = policy,
        Nodes = nodes,
    };

    [Fact]
    public async Task Cleanups_run_in_reverse_topological_order()
    {
        var order = new List<string>();
        var def = Def(Run.Always,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => { lock (order) { order.Add("first"); } return Task.CompletedTask; }); return Task.FromResult<object?>(null); }),
            Step(1, (_, ctx) => { ctx.OnTeardown(() => { lock (order) { order.Add("second"); } return Task.CompletedTask; }); return Task.FromResult<object?>(null); }, [0]),
            TeardownNode(2));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(["second", "first"], order);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task A_cleanup_receives_the_teardown_context_and_its_output_lands_on_the_teardown_node()
    {
        var def = Def(Run.Always,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(teardown =>
                {
                    teardown.Log("released the hold");
                    teardown.AddAttachment("released", "slot 14");
                    return Task.CompletedTask;
                });
                return Task.FromResult<object?>(null);
            }),
            TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        var teardown = results[1];
        Assert.Equal(StepStatus.Passed, teardown.Status);
        Assert.Equal(["released the hold", "cleaned up: Op0"], teardown.Logs);
        Assert.Equal("slot 14", teardown.Attachments["released"]);
        Assert.Empty(results[0].Logs); // nothing leaks back onto the step that registered it
    }

    [Fact]
    public async Task The_teardown_context_is_the_teardown_node_with_the_scenario_services_and_no_cancellation()
    {
        var services = new StubServices();
        using var cts = new CancellationTokenSource();
        ScenarioContext? handed = null;
        var def = Def(Run.Always,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(Cleanup.Required, teardown =>
                {
                    handed = teardown;
                    return Task.CompletedTask;
                });
                cts.Cancel(); // the scenario is cancelled after registration; the required cleanup still runs
                return Task.FromResult<object?>(null);
            }),
            TeardownNode(1));

        await new ScenarioScheduler().RunAsync(def, services, cancellationToken: cts.Token);

        Assert.NotNull(handed);
        Assert.Equal("step-1", handed.StepId);
        Assert.Equal("Teardown", handed.StepDisplayName);
        Assert.Same(services, handed.Services);
        Assert.False(handed.CancellationToken.IsCancellationRequested);
        Assert.False(handed.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task ScenarioContext_Current_is_the_teardown_context_while_cleanups_run()
    {
        ScenarioContext? handed = null;
        ScenarioContext? ambient = null;
        var def = Def(Run.Always,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(teardown =>
                {
                    handed = teardown;
                    ambient = ScenarioContext.Current;
                    return Task.CompletedTask;
                });
                return Task.FromResult<object?>(null);
            }),
            TeardownNode(1));

        await new ScenarioScheduler().RunAsync(def);

        Assert.NotNull(handed);
        Assert.Same(handed, ambient);
        Assert.Null(ScenarioContext.Current);
    }

    [Fact]
    public async Task Cleanup_resource_verbs_trace_on_the_teardown_node_and_are_never_a_conflict()
    {
        // The teardown node has no DependsOn edges, yet it runs after everything; a cleanup deleting
        // what a step created must trace as a Delete on the teardown node, not fail as a conflict.
        var user = new Resources.User("jane@x");
        var def = Def(Run.Always,
            Step(0, async (_, ctx) =>
            {
                await ctx.Resources.Create(user);
                ctx.OnTeardown(teardown => teardown.Resources.Delete(user).AsTask());
                return null;
            }),
            TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(StepStatus.Passed, results[1].Status);
        var effect = Assert.Single(results[1].Effects);
        Assert.Equal(LifecycleVerb.Delete, effect.Verb);
        Assert.Equal("User:jane@x", effect.Identity.ToString());
    }

    [Fact]
    public async Task The_parameterless_form_still_registers_and_runs()
    {
        var log = new TeardownLog();
        var ctx = Context("a", log, stepIndex: 0);
        var ran = false;

        ctx.OnTeardown(() => { ran = true; return Task.CompletedTask; });
        var entry = Assert.Single(log.Entries);
        await entry.Cleanup(new ScenarioContext("t", "Teardown", services: null, CancellationToken.None));

        Assert.True(ran);
    }

    [Fact]
    public async Task Steps_a_filter_left_out_do_not_count_against_Run_OnSuccess()
    {
        // Step 1 is unrelated to the target and is left out of the filtered run. It is recorded as
        // skipped ("not selected"), but that is not a failure, so OnSuccess must still run the cleanup.
        var cleaned = false;
        var def = Def(Run.OnSuccess,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(() => { cleaned = true; return Task.CompletedTask; });
                return Task.FromResult<object?>(null);
            }),
            Step(1, (_, _) => throw new InvalidOperationException("never runs")),
            TeardownNode(2));

        var results = await new ScenarioScheduler().RunAsync(def, targets: new HashSet<int> { 0 });

        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.True(cleaned);
        Assert.Equal(StepStatus.Passed, results[2].Status);
        Assert.DoesNotContain(results[2].Logs, line => line.StartsWith("skipped", StringComparison.Ordinal));
    }

    private sealed class StubServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task Within_one_step_cleanups_run_in_reverse_registration_order()
    {
        var order = new List<string>();
        var def = Def(Run.Always,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(() => { order.Add("a"); return Task.CompletedTask; });
                ctx.OnTeardown(() => { order.Add("b"); return Task.CompletedTask; });
                return Task.FromResult<object?>(null);
            }),
            TeardownNode(1));

        await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(["b", "a"], order);
    }

    [Fact]
    public async Task OnSuccess_skips_optional_cleanups_when_a_step_failed()
    {
        var ran = false;
        var def = Def(Run.OnSuccess,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => { ran = true; return Task.CompletedTask; }); return Task.FromResult<object?>(null); }),
            Step(1, (_, _) => throw new InvalidOperationException("boom"), [0]),
            TeardownNode(2));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.False(ran);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task OnSuccess_runs_optional_cleanups_when_every_step_passed()
    {
        var ran = false;
        var def = Def(Run.OnSuccess,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => { ran = true; return Task.CompletedTask; }); return Task.FromResult<object?>(null); }),
            TeardownNode(1));

        await new ScenarioScheduler().RunAsync(def);

        Assert.True(ran);
    }

    [Fact]
    public async Task Required_cleanups_run_even_under_Run_Never()
    {
        var optional = false;
        var required = false;
        var def = Def(Run.Never,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(() => { optional = true; return Task.CompletedTask; });
                ctx.OnTeardown(Cleanup.Required, () => { required = true; return Task.CompletedTask; });
                return Task.FromResult<object?>(null);
            }),
            TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.False(optional);
        Assert.True(required);
        Assert.Equal(StepStatus.Passed, results[1].Status);
    }

    [Fact]
    public async Task A_throwing_cleanup_does_not_stop_the_rest()
    {
        var later = false;
        var def = Def(Run.Always,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => { later = true; return Task.CompletedTask; }); return Task.FromResult<object?>(null); }),
            Step(1, (_, ctx) => { ctx.OnTeardown(() => throw new InvalidOperationException("cleanup boom")); return Task.FromResult<object?>(null); }, [0]),
            TeardownNode(2));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.True(later);
        Assert.Equal(StepStatus.Failed, results[2].Status);
        Assert.Contains("cleanup boom", results[2].Exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teardown_node_passes_when_nothing_was_registered()
    {
        // The status answers "did teardown succeed", nothing more. Anything else would put a
        // non-passing node in every suite that never uses teardown.
        var def = Def(Run.Always, Step(0, (_, _) => Task.FromResult<object?>(null)), TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.Contains("no cleanup registered", Assert.Single(results[1].Logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suppressed_cleanups_still_pass_and_are_detailed_in_the_log()
    {
        // Skipping is information, not a verdict: Run.Never is a deliberate choice, not a failure.
        var def = Def(Run.Never,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => Task.CompletedTask); return Task.FromResult<object?>(null); }),
            TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(StepStatus.Passed, results[1].Status);
        var line = Assert.Single(results[1].Logs);
        Assert.Contains("skipped 1 optional cleanup(s)", line, StringComparison.Ordinal);
        Assert.Contains("Never", line, StringComparison.Ordinal);
        Assert.Contains("Op0", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_names_the_step_each_cleanup_came_from()
    {
        var def = Def(Run.Always,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => Task.CompletedTask); return Task.FromResult<object?>(null); }),
            TeardownNode(1));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Contains("cleaned up: Op0", Assert.Single(results[1].Logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnSuccess_skip_after_a_failure_explains_itself_in_the_log()
    {
        var def = Def(Run.OnSuccess,
            Step(0, (_, ctx) => { ctx.OnTeardown(() => Task.CompletedTask); return Task.FromResult<object?>(null); }),
            Step(1, (_, _) => throw new InvalidOperationException("boom"), [0]),
            TeardownNode(2));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(StepStatus.Passed, results[2].Status);
        Assert.Contains("the scenario failed", Assert.Single(results[2].Logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_step_in_an_untaken_branch_registers_nothing()
    {
        var ran = false;
        var cond = new ScenarioNode
        {
            Index = 0,
            StepId = "cond",
            Phase = "Given",
            OperationName = "Cond",
            DisplayNameTemplate = "cond",
            DependsOn = [],
            Invoke = (_, _) => Task.FromResult<object?>(false),
            SelectArm = static inputs => inputs.Get<bool>(0) ? 0 : 1,
            Arms = ["if", "else"],
        };
        var arm = new ScenarioNode
        {
            Index = 1,
            StepId = "arm",
            Phase = "When",
            OperationName = "Arm",
            DisplayNameTemplate = "arm",
            DependsOn = [0],
            Guards = [new Guard(0, 0)],
            Invoke = (_, ctx) => { ctx.OnTeardown(() => { ran = true; return Task.CompletedTask; }); return Task.FromResult<object?>(null); },
        };

        var results = await new ScenarioScheduler().RunAsync(Def(Run.Always, cond, arm, TeardownNode(2)));

        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.False(ran);
        Assert.Equal(StepStatus.Passed, results[2].Status);   // nothing was registered to clean up
    }

    [Fact]
    public async Task Required_cleanups_run_after_the_scenario_is_cancelled()
    {
        // The case that matters most: a cancelled or timed-out scenario is exactly when a container
        // leaks, so the cancelled token must not suppress the cleanup that prevents it.
        var released = false;
        using var cts = new CancellationTokenSource();
        var def = Def(Run.Always,
            Step(0, (_, ctx) =>
            {
                ctx.OnTeardown(Cleanup.Required, () => { released = true; return Task.CompletedTask; });
                cts.Cancel();
                return Task.FromResult<object?>(null);
            }),
            Step(1, (_, _) => Task.FromResult<object?>(null), [0]),
            TeardownNode(2));

        await new ScenarioScheduler().RunAsync(def, cancellationToken: cts.Token);

        Assert.True(released);
    }
}
