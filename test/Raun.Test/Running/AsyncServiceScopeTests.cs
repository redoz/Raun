using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Test;

public sealed class AsyncServiceScopeTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task Async_only_services_are_disposed_once_after_required_cleanup(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        var service = new AsyncOnly(() => ValueTask.CompletedTask);
        await using var provider = new ServiceCollection().AddScoped(_ => service).BuildServiceProvider();
        var cleanupRan = false;
        var definition = TeardownTimeoutTests.Definition(ctx =>
        {
            Assert.Same(service, ctx.Services!.GetRequiredService<AsyncOnly>());
            ctx.OnTeardown(Cleanup.Required, cleanup =>
            {
                Assert.Same(service, cleanup.Services!.GetRequiredService<AsyncOnly>());
                Assert.Equal(0, service.Disposals);
                cleanupRan = true;
                return Task.CompletedTask;
            });
        }, body: ctx =>
        {
            if (outcome == "failure") throw new InvalidOperationException("step failed");
            if (outcome == "cancellation")
            {
                cancellation.Cancel();
                ctx.CancellationToken.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        });
        var sink = new Sink();
        await new RunLoop(() => [definition], services: provider)
            .RunAsync(null, sink, cancellation.Token);
        Assert.True(cleanupRan);
        Assert.Equal(1, service.Disposals);
        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public async Task Exclusive_admission_and_run_completion_wait_for_async_disposal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new AsyncOnly(async () => { entered.SetResult(); await release.Task; });
        await using var provider = new ServiceCollection().AddScoped(_ => service).BuildServiceProvider();
        var successorRan = false;
        var first = TeardownTimeoutTests.Definition(ctx => ctx.Services!.GetRequiredService<AsyncOnly>(),
            uses: [new ContendedResourceUse(typeof(Exclusive), LockMode.Exclusive)]);
        var second = new ScenarioDefinition
        {
            ScenarioId = "successor", DisplayName = "successor", MethodName = "Scope.Successor",
            Uses = first.Uses,
            Nodes = [new ScenarioNode
            {
                Index = 0, StepId = "next", Phase = "Then", OperationName = "Next", DisplayNameTemplate = "next",
                DependsOn = [], Invoke = (_, _) => { successorRan = true; return Task.FromResult<object?>(null); },
            }],
        };
        var run = new RunLoop(() => [first, second], services: provider, maxParallelScenarios: 2)
            .RunAsync(null, new Sink(), CancellationToken.None).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(successorRan);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(successorRan);
        Assert.Equal(1, service.Disposals);
    }

    [Fact]
    public async Task Disposal_failure_drains_siblings_and_publishes_RunFinished_before_rethrowing()
    {
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("dispose failed");
        var service = new AsyncOnly(() => { disposalEntered.SetResult(); throw error; });
        await using var provider = new ServiceCollection().AddScoped(_ => service).BuildServiceProvider();
        var first = TeardownTimeoutTests.Definition(ctx => ctx.Services!.GetRequiredService<AsyncOnly>(),
            body: async _ => await siblingEntered.Task);
        var second = new ScenarioDefinition
        {
            ScenarioId = "sibling", DisplayName = "sibling", MethodName = "Scope.Sibling",
            Nodes = [new ScenarioNode
            {
                Index = 0, StepId = "sibling", Phase = "Then", OperationName = "Sibling", DisplayNameTemplate = "sibling",
                DependsOn = [], Invoke = async (_, _) => { siblingEntered.SetResult(); await releaseSibling.Task; return null; },
            }],
        };
        var sink = new Sink();
        var run = new RunLoop(() => [first, second], services: provider, maxParallelScenarios: 2)
            .RunAsync(null, sink, CancellationToken.None).AsTask();
        try
        {
            await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { releaseSibling.TrySetResult(); }
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Same(error, actual);
        Assert.Equal(1, service.Disposals);
        Assert.Contains(sink.Events.OfType<ScenarioFinished>(), e => e.Definition.ScenarioId == "sibling");
        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public async Task Skipped_scenario_formatting_scopes_also_dispose_async_only_services()
    {
        var instances = new List<AsyncOnly>();
        await using var provider = new ServiceCollection()
            .AddScoped(_ =>
            {
                var instance = new AsyncOnly(() => ValueTask.CompletedTask);
                instances.Add(instance);
                return instance;
            })
            .AddScoped(sp =>
            {
                sp.GetRequiredService<AsyncOnly>();
                return new ResourceIdentityResolver();
            }).BuildServiceProvider();
        var definition = TeardownTimeoutTests.Definition(_ => throw new InvalidOperationException("must not run"));
        var sink = new Sink();
        await new RunLoop(() => [definition], services: provider,
            preflight: _ => throw new InvalidOperationException("preflight failed"))
            .RunAsync(null, sink, CancellationToken.None);
        Assert.Equal(2, instances.Count);
        Assert.All(instances, instance => Assert.Equal(1, instance.Disposals));
        Assert.All(sink.Events.OfType<StepFinished>().Where(e => e.Definition == definition),
            e => Assert.Equal(StepStatus.Skipped, e.Result.Status));
    }

    private sealed class AsyncOnly(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return dispose(); }
    }
    [ExclusiveResource]
    public sealed class Exclusive;
    private sealed class Sink : IRunEventSink
    {
        public ConcurrentQueue<RunEvent> Events { get; } = new();
        public ValueTask PublishAsync(RunEvent evt) { Events.Enqueue(evt); return ValueTask.CompletedTask; }
    }
}
