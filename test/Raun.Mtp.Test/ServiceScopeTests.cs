using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Raun.Mtp.Test;

// CA1812 cannot see instantiation through the DI container — that is the whole point of these types.
#pragma warning disable CA1812

/// <summary>
/// The consumer owns the service provider (built in their own <c>Main</c>); Raun opens one scope
/// per scenario over it. That gives both lifetimes through ordinary .NET semantics — a singleton for
/// something expensive like an Aspire AppHost, a scoped registration for per-scenario state — with
/// no Raun-specific vocabulary.
/// </summary>
public class ServiceScopeTests
{
    private sealed class Scoped : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Singleton;

    private static ScenarioNode Node(
        int index,
        string stepId,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = [],
        Invoke = invoke,
    };

    private static ScenarioDefinition Definition(string id, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = id,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
    };

    private sealed class Sink : IRunEventSink
    {
        public ValueTask PublishAsync(RunEvent evt) => default;
    }

    private static Task Run(IServiceProvider? services, params ScenarioDefinition[] definitions)
        => new RaunRunLoop(() => definitions, services: services)
            .RunAsync(selector: null, new Sink(), CancellationToken.None).AsTask();

    [Fact]
    public async Task A_supplied_provider_reaches_the_step_context()
    {
        var provider = new ServiceCollection().AddSingleton<Singleton>().BuildServiceProvider();
        Singleton? seen = null;

        await Run(provider, Definition("a", Node(0, "x", (_, ctx) =>
        {
            seen = ctx.Services!.GetRequiredService<Singleton>();
            return Task.FromResult<object?>(null);
        })));

        Assert.NotNull(seen);
    }

    [Fact]
    public async Task A_scoped_registration_is_shared_within_one_scenario()
    {
        var provider = new ServiceCollection().AddScoped<Scoped>().BuildServiceProvider();
        Scoped? first = null;
        Scoped? second = null;

        await Run(provider, Definition("a",
            Node(0, "x", (_, ctx) => { first = ctx.Services!.GetRequiredService<Scoped>(); return Task.FromResult<object?>(null); }),
            Node(1, "y", (_, ctx) => { second = ctx.Services!.GetRequiredService<Scoped>(); return Task.FromResult<object?>(null); })));

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Each_scenario_gets_its_own_scope()
    {
        var provider = new ServiceCollection().AddScoped<Scoped>().BuildServiceProvider();
        Scoped? fromA = null;
        Scoped? fromB = null;

        await Run(provider,
            Definition("a", Node(0, "x", (_, ctx) => { fromA = ctx.Services!.GetRequiredService<Scoped>(); return Task.FromResult<object?>(null); })),
            Definition("b", Node(0, "y", (_, ctx) => { fromB = ctx.Services!.GetRequiredService<Scoped>(); return Task.FromResult<object?>(null); })));

        Assert.NotNull(fromA);
        Assert.NotNull(fromB);
        Assert.NotSame(fromA, fromB);
    }

    [Fact]
    public async Task A_singleton_is_shared_across_scenarios()
    {
        var provider = new ServiceCollection().AddSingleton<Singleton>().BuildServiceProvider();
        Singleton? fromA = null;
        Singleton? fromB = null;

        await Run(provider,
            Definition("a", Node(0, "x", (_, ctx) => { fromA = ctx.Services!.GetRequiredService<Singleton>(); return Task.FromResult<object?>(null); })),
            Definition("b", Node(0, "y", (_, ctx) => { fromB = ctx.Services!.GetRequiredService<Singleton>(); return Task.FromResult<object?>(null); })));

        Assert.Same(fromA, fromB);
    }

    [Fact]
    public async Task The_scenario_scope_is_disposed_after_the_scenario()
    {
        var provider = new ServiceCollection().AddScoped<Scoped>().BuildServiceProvider();
        Scoped? captured = null;

        await Run(provider, Definition("a", Node(0, "x", (_, ctx) =>
        {
            captured = ctx.Services!.GetRequiredService<Scoped>();
            return Task.FromResult<object?>(null);
        })));

        Assert.True(captured!.Disposed);
    }

    [Fact]
    public async Task The_scope_is_still_alive_while_the_scenario_runs()
    {
        // Guards the ordering the teardown design reserved: cleanups may resolve from the scope, so
        // it must outlive the scenario's own execution.
        var provider = new ServiceCollection().AddScoped<Scoped>().BuildServiceProvider();
        var disposedDuringRun = true;

        await Run(provider, Definition("a", Node(0, "x", (_, ctx) =>
        {
            disposedDuringRun = ctx.Services!.GetRequiredService<Scoped>().Disposed;
            return Task.FromResult<object?>(null);
        })));

        Assert.False(disposedDuringRun);
    }

    [Fact]
    public async Task No_provider_leaves_services_null_and_the_step_still_runs()
    {
        var ran = false;

        await Run(null, Definition("a", Node(0, "x", (_, ctx) =>
        {
            Assert.Null(ctx.Services);
            ran = true;
            return Task.FromResult<object?>(null);
        })));

        Assert.True(ran);
    }

    [Fact]
    public async Task A_provider_without_a_scope_factory_is_used_directly()
    {
        // A hand-rolled IServiceProvider has no IServiceScopeFactory; it must be used as-is rather
        // than throwing.
        var provider = new HandRolled();
        object? seen = null;

        await Run(provider, Definition("a", Node(0, "x", (_, ctx) =>
        {
            seen = ctx.Services!.GetService(typeof(Singleton));
            return Task.FromResult<object?>(null);
        })));

        Assert.IsType<Singleton>(seen);
    }

    private sealed class HandRolled : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(Singleton) ? new Singleton() : null;
    }
}
