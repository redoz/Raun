using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The DAG scheduler is the heart of the runtime and is tested entirely independently of xUnit:
/// source-order sequencing, fork/join parallelism, the max-parallelism gate, dataflow between
/// steps, fail-then-skip-dependents, cancellation, per-step timeout, and observer callbacks.
/// </summary>
public class SchedulerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static ScenarioNode Node(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[]? dependsOn = null,
        TimeSpan? timeout = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn ?? [],
        Timeout = timeout,
        Invoke = invoke,
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    private static Func<IStepInputs, ScenarioContext, Task<object?>> Pass(object? output = null)
        => (_, _) => Task.FromResult(output);


    private static ScenarioNode Cond(int index, bool value, int[]? dependsOn = null, Guard[]? guards = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Cond{index}",
        DisplayNameTemplate = $"cond {index}",
        DependsOn = dependsOn ?? [],
        Guards = guards ?? [],
        Invoke = (_, _) => Task.FromResult<object?>(value),
        EvaluateCondition = static o => (bool)o!,
    };

    private static ScenarioNode ThrowingCond(int index, int[]? dependsOn = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Cond{index}",
        DisplayNameTemplate = $"cond {index}",
        DependsOn = dependsOn ?? [],
        Invoke = (_, _) => throw new InvalidOperationException("boom"),
        EvaluateCondition = static o => (bool)o!,
    };

    private static ScenarioNode Arm(
        int index,
        Guard[] guards,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        params int[] dependsOn) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn,
        Guards = guards,
        Invoke = invoke,
    };

    private static ScenarioNode MergeNode(int index, params int[] sources) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = "Merge",
        DisplayNameTemplate = "«merge»",
        DependsOn = [],
        MergeSources = sources,
        IsSynthetic = true,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous));
        Assert.True(done == task, "operation did not complete within the test timeout");
        return await task;
    }

    [Fact]
    public async Task Runs_chained_nodes_in_source_order()
    {
        var order = new List<int>();
        Func<int, Func<IStepInputs, ScenarioContext, Task<object?>>> rec =
            i => (_, _) => { lock (order) order.Add(i); return Task.FromResult<object?>(null); };

        var def = Def(Node(0, rec(0)), Node(1, rec(1), [0]), Node(2, rec(2), [1]));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal([0, 1, 2], order);
    }

    [Fact]
    public async Task Runs_independent_ready_nodes_concurrently_and_joins()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        Func<IStepInputs, ScenarioContext, Task<object?>> parallel = async (_, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
            await release.Task;
            return null;
        };

        var joined = false;
        var def = Def(
            Node(0, Pass()),
            Node(1, parallel, [0]),
            Node(2, parallel, [0]),
            Node(3, (_, _) => { joined = true; return Task.FromResult<object?>(null); }, [1, 2]));

        var run = new ScenarioScheduler().RunAsync(def);

        // Both siblings must be in-flight together before we let either finish.
        await WithTimeout(Task.WhenAny(bothEntered.Task, Task.Delay(Generous))
            .ContinueWith(_ => bothEntered.Task.IsCompleted));
        Assert.True(bothEntered.Task.IsCompleted, "siblings did not run concurrently");

        release.SetResult();
        var results = await WithTimeout(run);

        Assert.True(joined);
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Max_parallelism_one_serializes_siblings()
    {
        var sync = new object();
        var current = 0;
        var peak = 0;

        Func<IStepInputs, ScenarioContext, Task<object?>> tracked = async (_, _) =>
        {
            lock (sync) { current++; peak = Math.Max(peak, current); }
            await Task.Delay(25);
            lock (sync) { current--; }
            return null;
        };

        var def = Def(Node(0, Pass()), Node(1, tracked, [0]), Node(2, tracked, [0]));

        await WithTimeout(new ScenarioScheduler(maxParallelism: 1).RunAsync(def));

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Flows_typed_output_to_dependent_step()
    {
        var def = Def(
            Node(0, Pass(42)),
            Node(1, (inputs, _) => Task.FromResult<object?>(inputs.Get<int>(0) + 1), [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Failure_skips_dependents_but_independent_branch_continues()
    {
        var independentRan = false;
        var def = Def(
            Node(0, Pass()),
            Node(1, (_, _) => throw new InvalidOperationException("boom"), [0]),
            Node(2, Pass(), [1]),                                   // depends on the failure
            Node(3, (_, _) => { independentRan = true; return Task.FromResult<object?>(null); }, [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Failed, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
        Assert.Contains("dependency failed", results[2].SkipReason);
        Assert.Contains("Op1", results[2].SkipReason);
        Assert.Equal(StepStatus.Passed, results[3].Status);
        Assert.True(independentRan);
    }

    [Fact]
    public async Task Multiple_dependency_failures_are_summarized()
    {
        var def = Def(
            Node(0, Pass()),
            Node(1, (_, _) => throw new InvalidOperationException(), [0]),
            Node(2, (_, _) => throw new InvalidOperationException(), [0]),
            Node(3, Pass(), [1, 2]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Skipped, results[3].Status);
        Assert.Contains("Op1", results[3].SkipReason);
        Assert.Contains("Op2", results[3].SkipReason);
    }

    [Fact]
    public async Task Transitive_dependents_are_skipped()
    {
        var def = Def(
            Node(0, (_, _) => throw new InvalidOperationException("boom")),
            Node(1, Pass(), [0]),
            Node(2, Pass(), [1]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
    }

    [Fact]
    public async Task Pre_canceled_scenario_skips_all_steps()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ran = false;
        var def = Def(Node(0, (_, _) => { ran = true; return Task.FromResult<object?>(null); }));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def, cancellationToken: cts.Token));

        Assert.False(ran);
        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Contains("cancel", results[0].SkipReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_mid_run_stops_pending_steps()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();

        var def = Def(
            Node(0, async (_, ctx) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                return null;
            }),
            Node(1, Pass(), [0]));

        var run = new ScenarioScheduler().RunAsync(def, cancellationToken: cts.Token);
        await started.Task;
        cts.Cancel();

        var results = await WithTimeout(run);

        Assert.NotEqual(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
    }

    [Fact]
    public async Task Step_exceeding_its_timeout_fails_with_timeout_exception()
    {
        var def = Def(Node(0,
            async (_, _) => { await Task.Delay(Generous); return null; },
            timeout: TimeSpan.FromMilliseconds(40)));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.IsType<TimeoutException>(results[0].Exception);
    }

    [Fact]
    public async Task Observer_sees_one_start_and_finish_per_node_with_status()
    {
        var observer = new RecordingObserver();
        var def = Def(
            Node(0, Pass()),
            Node(1, (_, _) => throw new InvalidOperationException(), [0]),
            Node(2, Pass(), [1]));

        await WithTimeout(new ScenarioScheduler().RunAsync(def, observer: observer));

        Assert.Equal(3, observer.Started.Count);
        Assert.Equal(
            [StepStatus.Passed, StepStatus.Failed, StepStatus.Skipped],
            observer.Finished.OrderBy(r => r.Node.Index).Select(r => r.Status));
    }

    [Fact]
    public async Task Run_validates_the_graph()
    {
        var def = Def(Node(0, Pass(), [1]), Node(1, Pass(), [0]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ScenarioScheduler().RunAsync(def));
    }

    [Fact]
    public async Task StartedAt_comes_from_the_injected_time_provider()
    {
        var baseInstant = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(baseInstant);
        var def = Def(Node(0, Pass()));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(baseInstant, results[0].StartedAt);
    }

    [Fact]
    public async Task Concurrent_group_steps_get_distinct_started_at()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var def = Def(
            Node(0, Pass()),
            Node(1, Pass(), [0]),
            Node(2, Pass(), [0]));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        // Each concurrent sibling got its own StartedAt from the advancing clock (no shared anchor).
        Assert.NotEqual(results[1].StartedAt, results[2].StartedAt);
    }

    [Fact]
    public async Task Skipped_step_carries_started_at_and_zero_duration()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var def = Def(
            Node(0, (_, _) => throw new InvalidOperationException("boom")),
            Node(1, Pass(), [0]));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.NotEqual(default, results[1].StartedAt);
        Assert.Equal(TimeSpan.Zero, results[1].Duration);
    }


    [Fact]
    public async Task True_condition_runs_the_if_arm_and_leaves_the_else_arm_not_taken()
    {
        var ifRan = false;
        var elseRan = false;
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], (_, _) => { ifRan = true; return Task.FromResult<object?>(null); }, 0),
            Arm(2, [new Guard(0, false)], (_, _) => { elseRan = true; return Task.FromResult<object?>(null); }, 0));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.True(ifRan);
        Assert.False(elseRan);
        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.Equal(StepStatus.NotTaken, results[2].Status);
        Assert.Contains("not taken", results[2].SkipReason);
    }

    [Fact]
    public async Task False_condition_runs_the_else_arm()
    {
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass(), 0),
            Arm(2, [new Guard(0, false)], Pass(), 0));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task Nested_guards_all_must_hold()
    {
        // Guarded on cond0 == true AND cond1 == false; cond1 is true, so the node is not taken.
        var def = Def(
            Cond(0, true),
            Cond(1, true, [0]),
            Arm(2, [new Guard(0, true), new Guard(1, false)], Pass(), 1));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[2].Status);
    }

    [Fact]
    public async Task Condition_that_throws_skips_both_arms_rather_than_marking_them_not_taken()
    {
        // Load-bearing: a blown-up condition chose no branch. Reporting an arm as "not taken" would
        // disguise a failure as a routine decision.
        var def = Def(
            ThrowingCond(0),
            Arm(1, [new Guard(0, true)], Pass(), 0),
            Arm(2, [new Guard(0, false)], Pass(), 0));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
        Assert.Contains("dependency failed", results[1].SkipReason);
    }

    [Fact]
    public async Task Merge_passes_with_the_output_of_the_single_passing_source()
    {
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass("if-value"), 0),
            Arm(2, [new Guard(0, false)], Pass("else-value"), 0),
            MergeNode(3, 1, 2),
            new ScenarioNode
            {
                Index = 4,
                StepId = "step-4",
                Phase = "Then",
                OperationName = "Consume",
                DisplayNameTemplate = "consume",
                DependsOn = [3],
                Invoke = (inputs, _) => Task.FromResult<object?>(inputs.Get<string>(3)),
            });

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Passed, results[3].Status);
        Assert.Equal(StepStatus.Passed, results[4].Status);
    }

    [Fact]
    public async Task Merge_is_not_taken_when_every_source_is_not_taken()
    {
        // Both arms sit inside an outer branch that was not taken.
        var def = Def(
            Cond(0, false),
            Cond(1, true, [0], [new Guard(0, true)]),
            Arm(2, [new Guard(0, true), new Guard(1, true)], Pass("a"), 1),
            Arm(3, [new Guard(0, true), new Guard(1, false)], Pass("b"), 1),
            MergeNode(4, 2, 3));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[4].Status);
    }

    [Fact]
    public async Task Merge_is_skipped_when_a_source_failed()
    {
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], (_, _) => throw new InvalidOperationException("boom"), 0),
            Arm(2, [new Guard(0, false)], Pass("b"), 0),
            MergeNode(3, 1, 2));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[3].Status);
        Assert.Contains("Op1", results[3].SkipReason);
    }

    [Fact]
    public async Task Single_source_merge_passes_the_parent_definition_through()
    {
        // The bare-`if` pass-through: a one-source merge is an alias for that source.
        var def = Def(
            Node(0, Pass("parent")),
            MergeNode(1, 0),
            new ScenarioNode
            {
                Index = 2,
                StepId = "step-2",
                Phase = "Then",
                OperationName = "Consume",
                DisplayNameTemplate = "consume",
                DependsOn = [1],
                Invoke = (inputs, _) => Task.FromResult<object?>(inputs.Get<string>(1)),
            });

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task Dependent_of_a_not_taken_node_is_not_taken_not_skipped()
    {
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass(), 0),
            Node(2, Pass(), [1]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.Equal(StepStatus.NotTaken, results[2].Status);
        Assert.Contains("not taken", results[2].SkipReason);
    }

    [Fact]
    public async Task Not_taken_step_carries_started_at_and_zero_duration()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass(), 0));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.NotEqual(default, results[1].StartedAt);
        Assert.Equal(TimeSpan.Zero, results[1].Duration);
    }

    [Fact]
    public async Task Not_taken_nodes_do_not_raise_a_step_starting_callback()
    {
        // A branch that was never chosen never "started"; the MTP sink relies on this to take the
        // node straight from discovered to skipped without ever showing it InProgress.
        var observer = new RecordingObserver();
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass(), 0));

        await WithTimeout(new ScenarioScheduler().RunAsync(def, observer: observer));

        Assert.Single(observer.Started);
        Assert.Equal(2, observer.Finished.Count);
        Assert.Contains(observer.Finished, r => r.Status == StepStatus.NotTaken);
    }

    [Fact]
    public async Task Targets_run_only_their_predecessor_closure_and_report_nothing_else()
    {
        var ran = new HashSet<int>();
        Func<int, Func<IStepInputs, ScenarioContext, Task<object?>>> rec =
            i => (_, _) => { lock (ran) ran.Add(i); return Task.FromResult<object?>(null); };
        var observer = new RecordingObserver();

        // 0 -> 1 -> 2, and 3 hangs off 0 on its own branch. Target 1.
        var def = Def(Node(0, rec(0)), Node(1, rec(1), [0]), Node(2, rec(2), [1]), Node(3, rec(3), [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def, observer: observer, targets: new HashSet<int> { 1 }));

        Assert.Equal([0, 1], ran.Order());
        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
        Assert.Equal(ScenarioScheduler.NotSelectedSkipReason, results[2].SkipReason);
        Assert.Equal(StepStatus.Skipped, results[3].Status);
        Assert.Equal(ScenarioScheduler.NotSelectedSkipReason, results[3].SkipReason);

        // The observer never hears about the left-out nodes, in either direction.
        Assert.Equal([0, 1], observer.Started.Select(s => s.Node.Index).Order());
        Assert.Equal([0, 1], observer.Finished.Select(r => r.Node.Index).Order());
    }

    [Fact]
    public async Task Targets_pull_in_merge_sources_and_guard_conditions()
    {
        // condition 0; arms 1/2 guarded on it; merge 3 over the arms; 4 consumes the merge. Target 4.
        var observer = new RecordingObserver();
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], Pass("urgent"), 0),
            Arm(2, [new Guard(0, false)], Pass("standard"), 0),
            MergeNode(3, 1, 2),
            Node(4, (inputs, _) => Task.FromResult<object?>(inputs.Get<string>(3)), [3]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def, observer: observer, targets: new HashSet<int> { 4 }));

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.Equal(StepStatus.NotTaken, results[2].Status);
        Assert.Equal(StepStatus.Passed, results[3].Status);
        Assert.Equal(StepStatus.Passed, results[4].Status);
        Assert.DoesNotContain(observer.Finished, r => r.SkipReason == ScenarioScheduler.NotSelectedSkipReason);
    }

    [Fact]
    public async Task Null_targets_run_the_whole_scenario()
    {
        var def = Def(Node(0, Pass()), Node(1, Pass()), Node(2, Pass(), [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def, targets: null));

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    private static ScenarioNode Waiting(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[] dependsOn,
        int[] waitsFor) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Then",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn,
        WaitsFor = waitsFor,
        Invoke = invoke,
    };

    [Fact]
    public async Task WaitsFor_holds_a_node_until_the_awaited_node_is_terminal()
    {
        // 0 = condition (true); 1 = arm, guarded, slow; 2 = the statement after the if: depends on the
        // condition only, waits for the arm. It must not start until the arm has finished.
        var armFinished = false;
        var startedAfterArm = false;
        var gate = new TaskCompletionSource();
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], async (_, _) => { await gate.Task; armFinished = true; return null; }, 0),
            Waiting(2, (_, _) => { startedAfterArm = armFinished; return Task.FromResult<object?>(null); }, [0], [1]));

        var run = new ScenarioScheduler().RunAsync(def);
        await Task.Delay(50);
        Assert.False(armFinished);
        gate.SetResult();
        var results = await WithTimeout(run);

        Assert.True(startedAfterArm);
        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task A_not_taken_WaitsFor_predecessor_does_not_cascade()
    {
        // The arm is not taken. The statement after the if still runs and passes.
        var def = Def(
            Cond(0, false),
            Arm(1, [new Guard(0, true)], Pass(), 0),
            Waiting(2, Pass(), [0], [1]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task A_failed_WaitsFor_predecessor_skips_the_node()
    {
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], (_, _) => throw new InvalidOperationException("arm failed"), 0),
            Waiting(2, Pass(), [0], [1]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[1].Status);
        Assert.Equal(StepStatus.Skipped, results[2].Status);
        Assert.Contains("Op1", results[2].SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simulated_start_of_a_waiting_node_is_the_arm_finish()
    {
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], (_, ctx) => { ctx.SimulateElapsed(TimeSpan.FromSeconds(3)); return Task.FromResult<object?>(null); }, 0),
            Waiting(2, Pass(), [0], [1]));

        var results = await WithTimeout(new ScenarioScheduler(simulatedTime: true).RunAsync(def));

        Assert.Equal(results[1].StartedAt + results[1].Duration, results[2].StartedAt);
    }

    [Fact]
    public async Task Targets_pull_in_WaitsFor_predecessors()
    {
        var ran = new HashSet<int>();
        Func<int, Func<IStepInputs, ScenarioContext, Task<object?>>> rec =
            i => (_, _) => { lock (ran) ran.Add(i); return Task.FromResult<object?>(null); };
        var def = Def(
            Cond(0, true),
            Arm(1, [new Guard(0, true)], rec(1), 0),
            Waiting(2, rec(2), [0], [1]));

        await WithTimeout(new ScenarioScheduler().RunAsync(def, targets: new HashSet<int> { 2 }));

        Assert.Equal([1, 2], ran.Order());
    }

    [Fact]
    public async Task Log_offsets_are_measured_from_the_scenario_start_on_the_simulated_timeline()
    {
        // Step 0 runs 2s then logs; step 1 starts when 0 finishes, runs 1s, then logs. Its line is
        // stamped 3s into the scenario, not 1s into the step.
        var def = Def(
            Node(0, (_, ctx) => { ctx.SimulateElapsed(TimeSpan.FromSeconds(2)); ctx.Log("first done"); return Task.FromResult<object?>(null); }),
            Node(1, (_, ctx) => { ctx.SimulateElapsed(TimeSpan.FromSeconds(1)); ctx.Log("second done"); return Task.FromResult<object?>(null); }, [0]));

        var results = await WithTimeout(new ScenarioScheduler(simulatedTime: true).RunAsync(def));

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(results[0].LogEntries).Elapsed);
        var second = Assert.Single(results[1].LogEntries);
        Assert.Equal(TimeSpan.FromSeconds(3), second.Elapsed);
        Assert.Equal("+3.000s second done", second.ToString());
        Assert.Equal(["second done"], results[1].Logs);
    }

    private sealed class RecordingObserver : IStepObserver
    {
        private readonly object _sync = new();
        public List<(ScenarioNode Node, string Name)> Started { get; } = [];
        public List<StepResult> Finished { get; } = [];

        public Task OnStepStartingAsync(StepContext context)
        {
            lock (_sync) Started.Add((context.Node, context.DisplayName));
            return Task.CompletedTask;
        }

        public Task OnStepFinishedAsync(StepResult result)
        {
            lock (_sync) Finished.Add(result);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Reading_an_input_its_step_does_not_depend_on_fails_that_step_loudly()
    {
        // Only a generator bug reaches this: a consumer reads producer 0 without depending on it,
        // so the slot is not filled yet. A null cast would pass silently (or throw a bare
        // NullReferenceException for a value type); the failure has to name what went wrong.
        var def = Def(
            Node(0, Pass("value"), dependsOn: [1]),
            Node(1, (inputs, _) => Task.FromResult<object?>(inputs.Get<string>(0))));

        var results = await new ScenarioScheduler().RunAsync(def, cancellationToken: TestContext.Current.CancellationToken);

        var reader = results.Single(r => r.Node.Index == 1);
        Assert.Equal(StepStatus.Failed, reader.Status);
        var ex = Assert.IsType<InvalidOperationException>(reader.Exception);
        Assert.Contains("0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("depend", ex.Message, StringComparison.Ordinal);
    }
}
