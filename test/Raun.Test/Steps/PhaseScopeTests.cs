using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace Raun.Test;

public class PhaseScopeTests
{
    [Fact]
    public void World_independent_bases_are_public_and_typed_bases_inherit_them()
    {
        var assembly = typeof(Scenarios<>).Assembly;
        var scenarios = assembly.GetType("Raun.Scenarios");
        var phase = assembly.GetType("Raun.Phase");
        Assert.NotNull(scenarios);
        Assert.NotNull(phase);
        Assert.True(scenarios.IsPublic);
        Assert.True(phase.IsPublic);
        Assert.Equal(scenarios, typeof(Scenarios<>).BaseType);
        Assert.Equal(phase, typeof(Phase<>).BaseType);
        foreach (var name in new[] { "Given", "When", "Then" })
        {
            var builtIn = assembly.GetType($"Raun.{name}");
            Assert.NotNull(builtIn);
            Assert.Equal(phase, builtIn.BaseType);
        }
        Assert.Null(phase.GetProperty("World", BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(phase.GetProperty("Context", BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void Steps_share_one_cache_for_nested_groups_and_constructor_dependencies()
    {
        using var services = new ServiceCollection()
            .AddSingleton<Dependency>().BuildServiceProvider();
        var scope = new ScenarioScope<TestWorld>(new TestWorld(), services);
        var group = scope.Steps<IndependentSteps>();

        Assert.Same(group.Nested, scope.Steps<NestedSteps>());
        Assert.Same(group.Nested.Dependency, services.GetRequiredService<Dependency>());
        Assert.Same(group, scope.Steps<IndependentSteps>());
        Assert.Same(scope.World, scope.Steps<TypedSteps>().ReadWorld());
        Assert.Same(group.Nested, scope.Steps<TypedSteps>().Nested);
        var other = new ScenarioScope<TestWorld>(new TestWorld(), services);
        Assert.NotSame(group, other.Steps<IndependentSteps>());
    }

    [Fact]
    public async Task Typed_steps_reject_a_different_world_when_resolved()
    {
        var scope = new ScenarioScope<NoWorld>(await NoWorld.CreateAsync(ContextFor("setup")), null);
        Assert.Throws<InvalidOperationException>(() => scope.Steps<TypedSteps>());
        Assert.Throws<InvalidOperationException>(() => scope.Steps<TypedSteps>());
        Assert.NotNull(scope.Steps<IndependentSteps>());
        Assert.Same(scope.World, scope.Steps<LegacySteps>().ReadWorld());
    }

    [Fact]
    public void Unbound_steps_and_direct_scenario_properties_explain_the_required_runtime()
    {
        Assert.Throws<InvalidOperationException>(() => new IndependentSteps().Nested);
        Assert.Throws<InvalidOperationException>(() => new IndependentSteps().ReadContext());
        Assert.Throws<InvalidOperationException>(() => new TypedSteps().ReadWorld());
        Assert.Throws<InvalidOperationException>(() => new IndependentScenarios().Given);
        Assert.Throws<InvalidOperationException>(() => new TypedScenarios().Given);
    }

    [Fact]
    public async Task Shared_world_independent_steps_observe_each_parallel_flows_context()
    {
        var scope = new ScenarioScope<TestWorld>(new TestWorld(), null);
        var steps = scope.Steps<IndependentSteps>();
        var first = ContextFor("first");
        var second = ContextFor("second");
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task Observe(ScenarioContext context)
        {
            ScenarioContext.SetCurrent(context);
            try
            {
                if (Interlocked.Increment(ref started) == 2)
                    bothStarted.SetResult();
                await bothStarted.Task;
                Assert.Same(context, steps.ReadContext());
            }
            finally
            {
                ScenarioContext.SetCurrent(null);
            }
        }

        await Task.WhenAll(Observe(first), Observe(second));
        Assert.Null(ScenarioContext.Current);
    }

    private static ScenarioContext ContextFor(string id) => new(id, id, null, CancellationToken.None);

    public sealed class Dependency;

    public sealed class TestWorld : IScenarioWorld<TestWorld>
    {
        public static ValueTask<TestWorld> CreateAsync(ScenarioContext context) => ValueTask.FromResult(new TestWorld());
    }

    public sealed class NestedSteps(Dependency dependency) : Then
    {
        public Dependency Dependency { get; } = dependency;
    }

    public sealed class IndependentSteps : Given
    {
        public NestedSteps Nested => Steps<NestedSteps>();
        public ScenarioContext ReadContext() => Context;
    }

    public sealed class TypedSteps : When<TestWorld>
    {
        public NestedSteps Nested => Steps<NestedSteps>();
        public TestWorld ReadWorld() => World;
    }

    public sealed class LegacySteps : Given<NoWorld>
    {
        public NoWorld ReadWorld() => World;
    }

    public sealed class IndependentScenarios : Scenarios
    {
        public IndependentSteps Given => Steps<IndependentSteps>();
    }

    public sealed class TypedScenarios : Scenarios<TestWorld>
    {
        public IndependentSteps Given => Steps<IndependentSteps>();
    }

}
