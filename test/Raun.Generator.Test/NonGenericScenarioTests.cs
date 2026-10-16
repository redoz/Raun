using System.Globalization;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public sealed class NonGenericScenarioTests
{
    private const string Source = """
        using System;
        using System.Threading.Tasks;
        using Raun;
        namespace Worldless;
        public sealed record Item(string Id) : IResource<Item>
        {
            public static ResourceKey KeyFor(Item item) => item.Id;
        }
        public sealed class ItemSteps : Given
        {
            private readonly Guid _instance = Guid.NewGuid();
            [StepName("create {value}")]
            [return: Created]
            public Task<Item> Create(string value)
            {
                Context.Log(_instance.ToString());
                return Task.FromResult(new Item(value));
            }
            [StepName("read {item}")]
            public Task Read([Read] Item item)
            {
                Context.Log(_instance.ToString());
                return Task.CompletedTask;
            }
        }
        public sealed class Groups : Given { public ItemSteps Items => Steps<ItemSteps>(); }
        [PhaseName("Check")]
        public sealed class Checks : Phase
        {
            public Task Confirm(string actual, string expected)
            {
                if (actual != expected) throw new InvalidOperationException(actual);
                return Task.CompletedTask;
            }
        }
        public abstract class Suite : Scenarios
        {
            public Groups Given => Steps<Groups>();
            public Checks Check => Steps<Checks>();
        }
        public sealed class Cases : Suite
        {
            [Scenario("worldless")]
            [ScenarioCase("one", "alpha")]
            [ScenarioCase("two", "beta")]
            public async Task Run(string value)
            {
                var item = await Given.Items.Create(value).Note($"seed {value}");
                await Given.Items.Read(item).Note($"read {item.Id}");
                await Check.Confirm(item.Id, value);
            }
        }
        """;

    [Fact]
    public async Task Non_generic_suites_bind_nested_groups_and_isolate_cases_with_notes_and_resources()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Source));
        var definitions = generated.Definitions();
        Assert.Equal(2, definitions.Count);
        var instances = new List<string>();
        foreach (var definition in definitions)
        {
            Assert.Equal(5, definition.Nodes.Count);
            var results = await definition.RunAsync();
            Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
            Assert.Single(results, result => result.Node.IsSetup);
            Assert.Single(results, result => result.Node.IsTeardown);
            var created = results.Single(result => result.Node.OperationName == "Create");
            var read = results.Single(result => result.Node.OperationName == "Read");
            var instance = Assert.Single(created.Logs, log => Guid.TryParse(log, out _));
            Assert.Equal(instance, Assert.Single(read.Logs, log => Guid.TryParse(log, out _)));
            instances.Add(instance);
            Assert.Equal(Assert.Single(created.Effects).Identity, Assert.Single(read.Effects).Identity);
            Assert.Single(created.Notes);
            Assert.Single(read.Notes);
            Assert.StartsWith("create ", created.DisplayName, StringComparison.Ordinal);
            Assert.Equal("Check", results.Single(result => result.Node.OperationName == "Confirm").Node.Phase);
        }
        Assert.Equal(2, instances.Distinct().Count());
    }

    [Fact]
    public async Task Worldful_suites_can_mix_independent_groups_and_typed_world_steps()
    {
        var source = Source + """
            public sealed class World : IScenarioWorld<World>
            {
                public string Value => "world";
                public static ValueTask<World> CreateAsync(ScenarioContext context) => ValueTask.FromResult(new World());
            }
            public sealed class TypedSteps : When<World>
            {
                public ItemSteps Items => Steps<ItemSteps>();
                public Task<string> Value() => Task.FromResult(World.Value);
            }
            public sealed class TypedSuite : Scenarios<World>
            {
                public TypedSteps When => Steps<TypedSteps>();
                public Checks Check => Steps<Checks>();
                [Scenario] public async Task Mixed()
                {
                    var value = await When.Value();
                    var item = await When.Items.Create(value);
                    await Check.Confirm(item.Id, value);
                }
            }
            """;
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var definition = generated.Definitions().Single(d => d.MethodName.EndsWith(".Mixed", StringComparison.Ordinal));
        var results = await definition.RunAsync();
        Assert.Equal(5, results.Count);
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
    }

    [Theory]
    [InlineData("NoWorld", false)]
    [InlineData("OtherWorld", false)]
    [InlineData("NoWorld", true)]
    [InlineData("OtherWorld", true)]
    public async Task Explicit_generic_world_mismatches_are_rejected_even_through_independent_groups(string stepWorld, bool boundGroup)
    {
        var source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed class World : IScenarioWorld<World>
            {
                public static ValueTask<World> CreateAsync(ScenarioContext context) => ValueTask.FromResult(new World());
            }
            public sealed class OtherWorld : IScenarioWorld<OtherWorld>
            {
                public static ValueTask<OtherWorld> CreateAsync(ScenarioContext context) => ValueTask.FromResult(new OtherWorld());
            }
            public sealed class BoundSteps : Given<STEPWORLD> { public Task Act() => Task.CompletedTask; }
            public sealed class SharedGroup : Given { public BoundSteps Bound => Steps<BoundSteps>(); }
            public sealed class Suite : Scenarios<World>
            {
                public SharedGroup Given => Steps<SharedGroup>();
                [Scenario] public async Task Bad() { await Given.Bound.Act(); }
            }
            """.Replace("STEPWORLD", stepWorld, StringComparison.Ordinal);
        if (boundGroup)
        {
            source = source.Replace("BoundSteps : Given<" + stepWorld + ">", "BoundSteps : Given", StringComparison.Ordinal)
                .Replace("SharedGroup : Given", "SharedGroup : Given<" + stepWorld + ">", StringComparison.Ordinal);
        }
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        var diagnostic = Assert.Single(diagnostics, d => d.Id == "RAUN021");
        Assert.Contains(stepWorld, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_generic_steps_retain_mutable_state_and_step_argument_diagnostics()
    {
        const string source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed class Checks : Given
            {
                public int Count;
                public Task<int> Value() => Task.FromResult(42);
                public Task Observe(Task<int> value) => Task.CompletedTask;
            }
            public sealed class Suite : Scenarios
            {
                public Checks Given => Steps<Checks>();
                [Scenario] public async Task Bad() { await Given.Observe(Given.Value()); }
            }
            """;
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Single(diagnostics, d => d.Id == "RAUN022");
        Assert.Single(diagnostics, d => d.Id == "RAUN007");
    }

    [Fact]
    public async Task An_unnamed_non_generic_phase_reports_the_receiver_diagnostic()
    {
        const string source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed class Unnamed : Phase { public Task Act() => Task.CompletedTask; }
            public sealed class Suite : Scenarios
            {
                public Unnamed Given => Steps<Unnamed>();
                [Scenario] public async Task Bad() { await Given.Act(); }
            }
            """;
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        var diagnostic = Assert.Single(diagnostics, d => d.Id == "RAUN019");
        Assert.Contains("[PhaseName]", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain("Phase<NoWorld>", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
