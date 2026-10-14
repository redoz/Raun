using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>Invocation lifetime owns scenario scope and contention, including timeout grace.</summary>
public class TimeoutOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_and_exclusive_admission_wait_for_the_timed_out_body(bool scenarioTimeout)
    {
        using var provider = new ServiceCollection().AddScoped(_ => new Scoped()).BuildServiceProvider();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scoped? captured = null;
        var cleaned = false;
        var first = Definition("first", async (_, ctx) =>
        {
            captured = ctx.Services!.GetRequiredService<Scoped>();
            using var registration = ctx.CancellationToken.Register(() => canceled.TrySetResult());
            ctx.OnTeardown(Cleanup.Required, () => { Assert.False(captured.Disposed); cleaned = true; return Task.CompletedTask; });
            await release.Task;
            Assert.False(captured.Disposed);
            return null;
        }, scenarioTimeout ? TimeSpan.FromMilliseconds(100) : null, scenarioTimeout ? null : TimeSpan.FromMilliseconds(100));
        var second = Definition("second", (_, _) =>
        {
            Assert.True(cleaned);
            Assert.True(captured!.Disposed);
            successor.TrySetResult();
            return Task.FromResult<object?>(null);
        });
        var loop = new RunLoop(() => [first, second], services: provider, maxParallelScenarios: 2,
            cancellationGracePeriod: TimeSpan.FromSeconds(5));
        var run = loop.RunAsync(null, new Sink(), CancellationToken.None).AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(successor.Task, Task.Delay(100));
            Assert.False(captured!.Disposed);
            Assert.False(cleaned);
            Assert.False(successor.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(successor.Task.IsCompleted);
    }

    [Fact]
    public async Task Cancellation_callbacks_keep_ownership_after_the_body_returns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        CancellationTokenRegistration registration = default;
        var definition = Definition("callback", async (_, ctx) =>
        {
            registration = ctx.CancellationToken.Register(() =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            });
            ctx.OnTeardown(Cleanup.Required, () => { cleaned = true; return Task.CompletedTask; });
            await entered.Task;
            stopped.TrySetResult();
            return null;
        }, stepTimeout: TimeSpan.FromMilliseconds(50));
        var run = new ScenarioScheduler().RunAsync(definition);
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cleaned);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        registration.Dispose();
        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Host_cancellation_wins_over_a_later_scenario_deadline()
    {
        using var host = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Definition("host", async (_, ctx) =>
        {
            using var registration = ctx.CancellationToken.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            return null;
        }, scenarioTimeout: TimeSpan.FromMilliseconds(150));
        var run = new ScenarioScheduler().RunAsync(definition, cancellationToken: host.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.CancelAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StepStatus.Skipped, results[0].Status);
        Assert.Equal(ScenarioScheduler.CanceledSkipReason, results[0].SkipReason);
    }

    [Fact]
    public async Task Step_timeout_wins_over_later_host_cancellation()
    {
        using var host = new CancellationTokenSource();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Definition("step-first", async (_, ctx) =>
        {
            using var registration = ctx.CancellationToken.Register(() => canceled.TrySetResult());
            await release.Task;
            return null;
        }, stepTimeout: TimeSpan.FromMilliseconds(50));
        var run = new ScenarioScheduler().RunAsync(definition, cancellationToken: host.Token);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.CancelAsync();
            await Task.Delay(30);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.IsType<TimeoutException>(results[0].Exception);
    }

    [Fact]
    public async Task Step_timeout_wins_over_a_later_scenario_timeout()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Definition("step-before-scenario", async (_, ctx) =>
        {
            using var registration = ctx.CancellationToken.Register(() => canceled.TrySetResult());
            await release.Task;
            return null;
        }, scenarioTimeout: TimeSpan.FromMilliseconds(150), stepTimeout: TimeSpan.FromMilliseconds(50));
        var run = new ScenarioScheduler().RunAsync(definition);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(250);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StepStatus.Failed, results[0].Status);
        var error = Assert.IsType<TimeoutException>(results[0].Exception);
        Assert.StartsWith("Step '", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scenario_cancellation_propagates_before_latching_child_deadlines()
    {
        using var root = new CancellationTokenSource();
        using var child = new CancellationTokenSource();
        using var watchdog = new CancellationWatchdog(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var scenario = watchdog.Watch(root, TimeSpan.FromMilliseconds(1), "scenario", isScenario: true);
        using var operation = watchdog.Watch(child, TimeSpan.FromMilliseconds(1), "child");
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = child.Token.Register(() => canceled.TrySetResult());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scenario.TimedOut);
        Assert.False(operation.TimedOut, "scenario cancellation was already requested before the child deadline was processed");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void Invalid_grace_is_rejected(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScenarioScheduler(cancellationGracePeriod: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLoop(() => [], cancellationGracePeriod: TimeSpan.FromMilliseconds(milliseconds)));
    }

    private static ScenarioDefinition Definition(string id, Func<IStepInputs, ScenarioContext, Task<object?>> body,
        TimeSpan? scenarioTimeout = null, TimeSpan? stepTimeout = null) => new()
    {
        ScenarioId = id, DisplayName = id, MethodName = id, Timeout = scenarioTimeout,
        Uses = [new ContendedResourceUse(typeof(Database), LockMode.Exclusive)],
        Nodes =
        [
            new ScenarioNode
            {
                Index = 0, StepId = id + "-body", Phase = "Given", OperationName = "Body",
                DisplayNameTemplate = id, DependsOn = [], Timeout = stepTimeout, Invoke = body,
            },
            new ScenarioNode
            {
                Index = 1, StepId = id + "-cleanup", Phase = "Then", OperationName = "Teardown",
                DisplayNameTemplate = "Teardown", DependsOn = [], IsTeardown = true,
                Invoke = (_, _) => Task.FromResult<object?>(null),
            },
        ],
    };

    [ExclusiveResource]
    private readonly struct Database : IContendedResource;
    private sealed class Scoped : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Sink : IRunEventSink
    {
        public ValueTask PublishAsync(RunEvent evt) => ValueTask.CompletedTask;
    }
}
