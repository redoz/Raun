using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The scenario-wide timeout from <c>[Scenario(Timeout = …)]</c>. Unlike a per-step timeout it has to
/// retain ownership until a cancelled body stops, and leave a failure behind, not a skip,
/// so the run reports red. Teardown still runs, because a timed-out scenario is the case where leaked
/// resources hurt most.
/// </summary>
public class ScenarioTimeoutTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static ScenarioNode Node(
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

    private static ScenarioDefinition Def(TimeSpan? timeout, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Timeout = timeout,
        Nodes = nodes,
    };

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous));
        Assert.True(done == task, "operation did not complete within the test timeout");
        return await task;
    }

    [Fact]
    public async Task A_scenario_timeout_keeps_ownership_until_the_body_stops()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateCleanup = false;
        var def = Def(
            TimeSpan.FromMilliseconds(100),
            Node(0, async (_, ctx) =>
            {
                using var registration = ctx.CancellationToken.Register(() => canceled.TrySetResult());
                ctx.OnTeardown(Cleanup.Required, () => { cleanup.TrySetResult(); return Task.CompletedTask; });
                await release.Task;
                ctx.OnTeardown(Cleanup.Required, () => { lateCleanup = true; return Task.CompletedTask; });
                return null;
            }),
            Node(1, (_, _) => Task.FromResult<object?>(null), dependsOn: [0]),
            TeardownNode(2));

        var run = new ScenarioScheduler().RunAsync(def);
        try
        {
            await canceled.Task.WaitAsync(Generous);
            await Task.WhenAny(cleanup.Task, Task.Delay(100));
            Assert.False(cleanup.Task.IsCompleted, "cleanup ran while the timed-out body was alive");
            Assert.False(run.IsCompleted, "scenario released ownership while its body was alive");
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await WithTimeout(run);
        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.IsType<TimeoutException>(results[0].Exception);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(ScenarioScheduler.TimedOutSkipReason, results[1].SkipReason);
        Assert.Equal(StepStatus.Passed, results[2].Status);
        Assert.True(cleanup.Task.IsCompleted);
        Assert.True(lateCleanup, "cleanup registered during cancellation grace was lost");
    }

    [Fact]
    public async Task A_cooperative_step_canceled_by_the_timeout_still_fails()
    {
        var def = Def(
            TimeSpan.FromMilliseconds(200),
            Node(0, async (_, ctx) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.CancellationToken);
                return null;
            }),
            TeardownNode(1));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.IsType<TimeoutException>(results[0].Exception);
    }

    [Fact]
    public async Task A_scenario_inside_its_timeout_runs_normally()
    {
        var def = Def(
            TimeSpan.FromSeconds(10),
            Node(0, (_, _) => Task.FromResult<object?>(null)),
            Node(1, (_, _) => Task.FromResult<object?>(null), dependsOn: [0]),
            TeardownNode(2));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task A_timeout_of_zero_or_less_means_no_timeout()
    {
        // The generator writes whatever the attribute said; 0 is the property's default, i.e. "unset".
        var def = Def(
            TimeSpan.Zero,
            Node(0, async (_, _) => { await Task.Delay(50); return null; }),
            TeardownNode(1));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
    }

    [Fact]
    public async Task Host_cancellation_still_skips_rather_than_fails()
    {
        using var cts = new CancellationTokenSource();
        var def = Def(
            TimeSpan.FromSeconds(30),
            Node(0, async (_, ctx) =>
            {
                await cts.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(10), ctx.CancellationToken);
                return null;
            }),
            Node(1, (_, _) => Task.FromResult<object?>(null), dependsOn: [0]),
            TeardownNode(2));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def, cancellationToken: cts.Token));

        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal(ScenarioScheduler.CanceledSkipReason, results[0].SkipReason);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
    }
}
