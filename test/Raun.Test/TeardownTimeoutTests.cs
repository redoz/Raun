using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>Cleanup callbacks have independent deadlines, cancellation, and retained output.</summary>
public class TeardownTimeoutTests
{
    [Fact]
    public async Task Timed_out_cleanup_drains_before_next_callback_and_preserves_both_contexts()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScenarioContext? first = null;
        ScenarioContext? second = null;
        var definition = Definition(ctx =>
        {
            ctx.OnTeardown(Cleanup.Required, async cleanup =>
            {
                second = cleanup;
                Assert.Same(cleanup, ScenarioContext.Current);
                Assert.False(cleanup.CancellationToken.IsCancellationRequested);
                cleanup.Log("second");
                cleanup.AddAttachment("second", "kept");
                await Task.CompletedTask.Note("second note");
                await cleanup.Resources.Read("resource");
            });
            ctx.OnTeardown(Cleanup.Required, async cleanup =>
            {
                first = cleanup;
                Assert.Same(cleanup, ScenarioContext.Current);
                using var registration = cleanup.CancellationToken.Register(() => canceled.TrySetResult());
                cleanup.Log("first");
                cleanup.AddAttachment("first", "kept");
                await release.Task;
                await Task.CompletedTask.Note("first note");
            });
        });
        var run = new ScenarioScheduler(cleanupTimeout: TimeSpan.FromMilliseconds(100),
            cancellationGracePeriod: TimeSpan.FromSeconds(5)).RunAsync(definition);
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(second);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        var teardown = results[1];
        Assert.Equal(StepStatus.Failed, teardown.Status);
        var aggregate = Assert.IsType<AggregateException>(teardown.Exception);
        var timeout = Assert.IsType<TimeoutException>(Assert.Single(aggregate.InnerExceptions));
        Assert.Contains("registration #1", timeout.Message, StringComparison.Ordinal);
        Assert.Contains("Create", timeout.Message, StringComparison.Ordinal);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.Contains("first", teardown.Logs);
        Assert.Contains("second", teardown.Logs);
        Assert.Equal("kept", teardown.Attachments["first"]);
        Assert.Equal("kept", teardown.Attachments["second"]);
        Assert.Equal(["first note", "second note"], teardown.Notes);
        Assert.Single(teardown.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cooperative_cleanup_timeout_fails_but_attempts_the_next_required_cleanup(bool simulated)
    {
        var next = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Definition(ctx =>
        {
            ctx.OnTeardown(Cleanup.Required, () => { next = true; return Task.CompletedTask; });
            ctx.OnTeardown(Cleanup.Required, cleanup => release.Task.WaitAsync(cleanup.CancellationToken));
        });
        var run = new ScenarioScheduler(cleanupTimeout: TimeSpan.FromMilliseconds(50), simulatedTime: simulated).RunAsync(definition);
        IReadOnlyList<StepResult> results;
        try { results = await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        Assert.True(next);
        Assert.Equal(StepStatus.Failed, results[1].Status);
        Assert.IsType<TimeoutException>(Assert.Single(Assert.IsType<AggregateException>(results[1].Exception).InnerExceptions));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void Cleanup_timeout_cannot_be_zero_negative_or_unbounded(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScenarioScheduler(cleanupTimeout: TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RunLoop(() => [], cleanupTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task Cleanup_cancellation_callbacks_are_drained_before_advancing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = false;
        CancellationTokenRegistration registration = default;
        var definition = Definition(ctx =>
        {
            ctx.OnTeardown(Cleanup.Required, () => { next = true; return Task.CompletedTask; });
            ctx.OnTeardown(Cleanup.Required, async cleanup =>
            {
                registration = cleanup.CancellationToken.Register(() =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                });
                await entered.Task;
                stopped.TrySetResult();
            });
        });
        var run = new ScenarioScheduler(cleanupTimeout: TimeSpan.FromMilliseconds(50),
            cancellationGracePeriod: TimeSpan.FromSeconds(5)).RunAsync(definition);
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(next);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var results = await run.WaitAsync(TimeSpan.FromSeconds(5));
        registration.Dispose();
        Assert.True(next);
        Assert.Equal(StepStatus.Failed, results[1].Status);
    }

    [Fact]
    public async Task Required_cleanup_after_scenario_timeout_gets_a_fresh_deadline_token()
    {
        var cleaned = false;
        var definition = Definition(ctx => ctx.OnTeardown(Cleanup.Required, cleanup =>
        {
            Assert.True(cleanup.CancellationToken.CanBeCanceled);
            Assert.False(cleanup.CancellationToken.IsCancellationRequested);
            cleaned = true;
            return Task.CompletedTask;
        }), scenarioTimeout: TimeSpan.FromMilliseconds(50), body: ctx => Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken));
        var results = await new ScenarioScheduler().RunAsync(definition).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.Equal(StepStatus.Passed, results[1].Status);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Cleanup_grace_retains_scope_and_exclusive_admission()
    {
        using var provider = new ServiceCollection().AddScoped(_ => new Scoped()).BuildServiceProvider();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scoped? captured = null;
        var first = Definition(ctx =>
        {
            captured = ctx.Services!.GetRequiredService<Scoped>();
            ctx.OnTeardown(Cleanup.Required, async cleanup =>
            {
                using var registration = cleanup.CancellationToken.Register(() => canceled.TrySetResult());
                await release.Task;
                Assert.False(captured.Disposed);
            });
        }, uses: [new ContendedResourceUse(typeof(Database), LockMode.Exclusive)]);
        var second = new ScenarioDefinition
        {
            ScenarioId = "successor", DisplayName = "successor", MethodName = "Successor.Run",
            Uses = [new ContendedResourceUse(typeof(Database), LockMode.Exclusive)],
            Nodes = Definition(_ =>
            {
                Assert.True(captured!.Disposed);
                successor.TrySetResult();
            }).Nodes,
        };
        var run = new RunLoop(() => [first, second], services: provider, maxParallelScenarios: 2,
            cleanupTimeout: TimeSpan.FromMilliseconds(100), cancellationGracePeriod: TimeSpan.FromSeconds(5))
            .RunAsync(null, new Sink(), CancellationToken.None).AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAny(successor.Task, Task.Delay(100));
            Assert.False(captured!.Disposed);
            Assert.False(successor.Task.IsCompleted);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(successor.Task.IsCompleted);
    }

    [ExclusiveResource]
    private readonly struct Database : IContendedResource;
    private sealed class Scoped : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    private sealed class Sink : IRunEventSink
    {
        public ValueTask PublishAsync(RunEvent evt) => ValueTask.CompletedTask;
    }

    internal static ScenarioDefinition Definition(Action<ScenarioContext> register, TimeSpan? scenarioTimeout = null,
        Func<ScenarioContext, Task>? body = null, IReadOnlyList<ContendedResourceUse>? uses = null) => new()
    {
        ScenarioId = "cleanup-timeout", DisplayName = "cleanup scenario", MethodName = "Cleanup.Run",
        Timeout = scenarioTimeout, Uses = uses ?? [],
        Nodes =
        [
            new ScenarioNode
            {
                Index = 0, StepId = "create", Phase = "Given", OperationName = "Create", DisplayNameTemplate = "create",
                DependsOn = [], Invoke = async (_, ctx) => { register(ctx); if (body is not null) await body(ctx); return null; },
            },
            new ScenarioNode
            {
                Index = 1, StepId = "cleanup", Phase = "Then", OperationName = "Teardown", DisplayNameTemplate = "Teardown",
                DependsOn = [], IsTeardown = true, Invoke = (_, _) => Task.FromResult<object?>(null),
            },
        ],
    };
}
