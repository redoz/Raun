using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Generator.Test;

public class StepMessageTitleTests
{
    private const string Source = """
        using System;
        using System.Threading.Tasks;
        using Raun;
        namespace Messages;
        public sealed record Seed(string Key) : IResource<Seed>
        {
            public static ResourceKey KeyFor(Seed seed) => seed.Key;
            public override string ToString() => "data-" + Key;
        }
        public sealed record Plain(string Key);
        public sealed record Holder(Seed? Seed, DateTime Day, decimal Amount);
        public sealed class Steps : Given<NoWorld>
        {
            public Task<Seed> Produce(string key) => Task.FromResult(new Seed(key));
            public Task<StepMessage> Frozen() => Task.FromResult(new StepMessage(new[] { new StepMessagePart("frozen", new ResourceIdentity(typeof(Seed), "frozen")) }));
            public Task Fail() => throw new InvalidOperationException("failed");
            [StepName("Observe {seed} {seed:id} {other:data}")]
            public Task Observe(Seed seed, Seed other) => Task.CompletedTask;
            [StepName("Path {holder.Seed:id} {holder.Day:yyyy-MM-dd} {holder.Amount:F2}")]
            public Task Path(Holder holder) => Task.CompletedTask;
            [StepName("Registered {plain:id} {plain:data}")]
            public Task Registered(Plain plain) => Task.CompletedTask;
        }
        public sealed class Scenarios : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Story()
            {
                var seed = await Given.Produce("one");
                var other = await Given.Produce("two");
                var frozen = await Given.Frozen();
                await Given.Observe(seed, other)
                    .Note($"Bare {seed} {seed:id} {other:data}")
                    .Note((FormattableString)$"Explicit {seed:id}")
                    .Note(frozen).Note("plain").Note($"Opaque {(object)seed}")
                    .Note($"Culture {12.5m:F2}").Note((FormattableString)$"Invariant {12.5m:F2}");
                await Given.Path(new Holder(null, new DateTime(2026, 10, 13), 12.5m));
            }
            [Scenario] public async Task Skipped()
            {
                var seed = await Given.Produce("one");
                await Given.Fail();
                await Given.Observe(seed, seed).Note($"Available {seed:id}");
            }
            [Scenario] public async Task Unavailable()
            {
                await Given.Fail();
                var seed = await Given.Produce("one");
                await Given.Observe(seed, seed).Note($"Unavailable {seed:id}").Note("remaining");
            }
            [Scenario] public async Task Registered()
            {
                await Given.Registered(new Plain("manual"));
            }
        }
        """;

    [Fact]
    public async Task Generated_titles_and_notes_use_the_type_label()
    {
        var source = Source.Replace("public override string ToString() => \"data-\" + Key;",
            "public static string? LabelFor(Seed seed) => \"Label \" + seed.Key; public override string ToString() => \"data-\" + Key;", StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await Scenario(generated, "Story").RunAsync();
        var observe = results.Single(result => result.Node.OperationName == "Observe");
        Assert.Equal("Observe Label one one data-two", observe.DisplayName);
        Assert.Equal("Bare Label one one data-two", observe.Notes[0]);
        Assert.Equal("one", References(observe.TitleMessage).First().Resource!.Value.Key.Value);
    }

    [Fact]
    public async Task Typed_notes_and_titles_preserve_resources_without_changing_the_graph()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definition = Scenario(generated, "Story");
        Assert.Equal(7, definition.Nodes.Count);
        var observe = definition.Nodes.Single(node => node.OperationName == "Observe");
        Assert.Equal(new[] { 0, 1, 2, 3 }, observe.DependsOn);
        Assert.Empty(observe.Guards);
        Assert.Empty(observe.MergeSources);
        var withoutNotes = GeneratorHarness.Run(Source.Replace(".Note($\"Bare {seed} {seed:id} {other:data}\")", "", StringComparison.Ordinal)
            .Replace(".Note((FormattableString)$\"Explicit {seed:id}\")", "", StringComparison.Ordinal)
            .Replace(".Note(frozen).Note(\"plain\").Note($\"Opaque {(object)seed}\")", "", StringComparison.Ordinal)
            .Replace(".Note($\"Culture {12.5m:F2}\").Note((FormattableString)$\"Invariant {12.5m:F2}\")", "", StringComparison.Ordinal));
        withoutNotes.AssertCompiles();
        Assert.Equal(Scenario(withoutNotes, "Story").Nodes.Select(node => node.StepId), definition.Nodes.Select(node => node.StepId));
        Assert.NotNull(observe.FormatTitleMessage);
        var frozen = StepMessage.FromText("same instance");
        Assert.Same(frozen, observe.FormatNoteMessages[2](new FrozenInputs(frozen), new MessageFormattingContext(new ResourceIdentityResolver())));
        Assert.Equal("Observe {seed} {seed:id} {other:data}", observe.DisplayNameTemplate);
        var results = await definition.RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var result = results.Single(item => item.Node.OperationName == "Observe");
        Assert.Equal("Observe Seed:one one data-two", result.DisplayName);
        Assert.Equal(new[] { "Bare Seed:one one data-two", "Explicit one", "frozen", "plain", "Opaque data-one", "Culture 12.50", "Invariant 12.50" }, result.Notes);
        Assert.Equal(new[] { "one", "one", "two" }, References(result.TitleMessage).Select(part => part.Resource!.Value.Key.Value));
        Assert.Equal(3, References(result.NoteMessages[0]).Count());
        Assert.Equal("data-two", References(result.NoteMessages[0]).Last().Data);
        Assert.Equal("one", Assert.Single(References(result.NoteMessages[1])).Resource!.Value.Key.Value);
        Assert.Equal("frozen", Assert.Single(References(result.NoteMessages[2])).Resource!.Value.Key.Value);
        Assert.Empty(References(result.NoteMessages[4]));
        Assert.Empty(result.Effects);
        Assert.Empty(result.Links);
        Assert.Equal("Path  2026-10-13 12.50", results.Single(item => item.Node.OperationName == "Path").DisplayName);
    }

    [Theory]
    [InlineData("Skipped", "Available one", 3)]
    [InlineData("Unavailable", "remaining", 0)]
    public async Task Skipped_metadata_uses_only_available_inputs(string method, string note, int titleReferences)
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var results = await Scenario(generated, method).RunAsync();
        var result = results.Single(item => item.Node.OperationName == "Observe");
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal(new[] { note }, result.Notes);
        Assert.Equal(titleReferences, References(result.TitleMessage).Count());
        Assert.Equal(method == "Skipped" ? 1 : 0, References(Assert.Single(result.NoteMessages)).Count());
    }

    [Fact]
    public async Task Generated_title_delegate_can_format_a_manual_node_with_registered_identity()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var original = Scenario(generated, "Registered").Nodes.Single(node => node.OperationName == "Registered");
        var resolver = new ResourceIdentityResolver();
        var plain = generated.Assembly!.GetType("Messages.Plain")!;
        typeof(ResourceIdentityResolver).GetMethods().Single(method => method.Name == nameof(ResourceIdentityResolver.Identify) && method.IsGenericMethod && method.GetParameters().Length == 1).MakeGenericMethod(plain)
            .Invoke(resolver, new[] { Selector(plain) });
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var manual = new ScenarioNode
        {
            Index = 0, DependsOn = [], StepId = "manual", Phase = "Given", OperationName = "manual",
            DisplayNameTemplate = original.DisplayNameTemplate, FormatTitleMessage = original.FormatTitleMessage,
            PrepareArguments = original.PrepareArguments,
            Invoke = static (_, _) => Task.FromResult<object?>(null),
        };
        var definition = new ScenarioDefinition { ScenarioId = "manual", DisplayName = "manual", MethodName = "manual", Nodes = new[] { manual } };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(definition, services));
        Assert.StartsWith("Registered configured ", result.DisplayName, StringComparison.Ordinal);
        Assert.All(References(result.TitleMessage), part => Assert.Equal("configured", part.Resource!.Value.Key.Value));
        Assert.Equal(2, References(result.TitleMessage).Count());
    }

    [Fact]
    public async Task Titles_and_explicit_formattable_notes_are_invariant_but_handler_notes_keep_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var generated = GeneratorHarness.Run(Source);
            generated.AssertCompiles();
            var results = await Scenario(generated, "Story").RunAsync();
            var observed = results.Single(item => item.Node.OperationName == "Observe");
            Assert.Equal("Culture 12,50", observed.Notes[5]);
            Assert.Equal("Invariant 12.50", observed.Notes[6]);
            Assert.Equal("Path  2026-10-13 12.50", results.Single(item => item.Node.OperationName == "Path").DisplayName);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Resource_formats_follow_CSharp_policy_and_keep_parameter_validation()
    {
        var source = Source.Replace("Seed seed, Seed other", "[Read] Seed seed, [Read] Seed other", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var ordinaryFormat = await GeneratorHarness.DiagnoseAsync(source.Replace("{seed:id}", "{seed:invalid}", StringComparison.Ordinal), requireCompilable: true);
        Assert.Empty(ordinaryFormat); // C# ignores formats on values without IFormattable, as notes/logs do.
        var invalidParameter = await GeneratorHarness.DiagnoseAsync(source.Replace("{plain:id}", "{missing:id}", StringComparison.Ordinal), requireCompilable: true);
        Assert.Contains(invalidParameter, diagnostic => diagnostic.Id == "RAUN008");
    }

    private const string DeclaredTypeSource = """
        using System.Threading.Tasks;
        using Raun;
        namespace DeclaredMessages;
        public interface IView { string Label { get; } }
        public class BaseSeed { public string Label => "base"; }
        public sealed class Seed : BaseSeed, IView, IResource<Seed>
        {
            public new string Label => "derived";
            public static ResourceKey KeyFor(Seed seed) => "concrete";
            public override string ToString() => "data";
        }
        public sealed class Steps : Given<NoWorld>
        {
            public Task<Seed> Produce() => Task.FromResult(new Seed());
            [StepName("Opaque {seed}")]
            public Task Opaque(object seed) => Task.CompletedTask;
            [StepName("Base {seed} {seed.Label}")]
            public Task Base(BaseSeed seed) => Task.CompletedTask;
            [StepName("View {seed}")]
            public Task View(IView seed) => Task.CompletedTask;
            [StepName("Constants {key} {count}")]
            public Task Constants(string key, int count) => Task.CompletedTask;
        }
        public sealed class Scenarios : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Notes()
            {
                object opaque = await Given.Produce();
                BaseSeed seedBase = await Given.Produce();
                IView seedView = await Given.Produce();
                await Given.Opaque(opaque).Note($"Notes {opaque} {seedBase:id} {seedView:id}");
            }
            [Scenario] public async Task Titles()
            {
                var seed = await Given.Produce();
                await Given.Base(seed);
                await Given.View(seed);
                await Given.Opaque(seed);
            }
            [Scenario] public async Task Constants()
            {
                await Given.Constants("one", 7);
            }
        }
        """;

    [Fact]
    public async Task Notes_preserve_widened_local_types_for_object_base_and_interface_values()
    {
        var generated = GeneratorHarness.Run(DeclaredTypeSource);
        generated.AssertCompiles();
        using var services = DeclaredServices(generated);
        var definition = Scenario(generated, "Notes");
        Assert.Equal(6, definition.Nodes.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, definition.Nodes[4].DependsOn);
        Assert.Empty(definition.Nodes[4].Guards);
        Assert.Empty(definition.Nodes[4].MergeSources);
        var results = await new ScenarioScheduler().RunAsync(definition, services);
        var note = Assert.Single(results.Single(result => result.Node.OperationName == "Opaque").NoteMessages);
        Assert.Equal("Notes data configured configured", note.Text);
        Assert.Equal(new[] { "BaseSeed", "IView" }, References(note).Select(part => part.Resource!.Value.Kind.Type!.Name));
        Assert.All(References(note), part => Assert.Equal("configured", part.Resource!.Value.Key.Value));
    }

    [Fact]
    public async Task Titles_apply_the_bound_parameter_type_before_resource_formatting_and_member_access()
    {
        var generated = GeneratorHarness.Run(DeclaredTypeSource);
        generated.AssertCompiles();
        using var services = DeclaredServices(generated);
        var definition = Scenario(generated, "Titles");
        Assert.Equal(6, definition.Nodes.Count);
        Assert.Equal(new[] { 0, 1 }, definition.Nodes[2].DependsOn);
        Assert.Equal(new[] { 0, 1, 2 }, definition.Nodes[3].DependsOn);
        Assert.Equal(new[] { 0, 1, 3 }, definition.Nodes[4].DependsOn);
        Assert.All(definition.Nodes.Skip(2).Take(3), node =>
        {
            Assert.Empty(node.Guards);
            Assert.Empty(node.MergeSources);
        });
        var results = await new ScenarioScheduler().RunAsync(definition, services);
        var baseTitle = results.Single(result => result.Node.OperationName == "Base").TitleMessage;
        Assert.Equal("Base BaseSeed:configured base", baseTitle.Text);
        Assert.Equal("BaseSeed", Assert.Single(References(baseTitle)).Resource!.Value.Kind.Type!.Name);
        var viewTitle = results.Single(result => result.Node.OperationName == "View").TitleMessage;
        Assert.Equal("View IView:configured", viewTitle.Text);
        Assert.Equal("IView", Assert.Single(References(viewTitle)).Resource!.Value.Kind.Type!.Name);
        var opaqueTitle = results.Single(result => result.Node.OperationName == "Opaque").TitleMessage;
        Assert.Equal("Opaque data", opaqueTitle.Text);
        Assert.Empty(References(opaqueTitle));
    }

    [Fact]
    public async Task Constant_titles_keep_folded_discovery_names_and_typed_inputs_for_registered_selectors()
    {
        var generated = GeneratorHarness.Run(DeclaredTypeSource);
        generated.AssertCompiles();
        var definition = Scenario(generated, "Constants");
        Assert.Equal(3, definition.Nodes.Count);
        Assert.Equal("Constants one 7", definition.Nodes[1].DisplayNameTemplate);
        Assert.Null(definition.Nodes[1].FormatDisplayName);
        Assert.Equal(new[] { 0 }, definition.Nodes[1].DependsOn);
        var plain = (await definition.RunAsync())[1];
        Assert.Equal("Constants one 7", plain.DisplayName);
        Assert.Empty(References(plain.TitleMessage));
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<string>(value => "str-" + value);
        resolver.Identify<int>(value => "num-" + value.ToString(CultureInfo.InvariantCulture));
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var typed = (await new ScenarioScheduler().RunAsync(definition, services))[1];
        Assert.Equal("Constants String:str-one Int32:num-7", typed.DisplayName);
        Assert.Equal(new[] { "str-one", "num-7" }, References(typed.TitleMessage).Select(part => part.Resource!.Value.Key.Value));
        Assert.Equal(plain.Node.StepId, typed.Node.StepId);
        Assert.Empty(typed.Effects);
        Assert.Empty(typed.Links);
    }

    private static ServiceProvider DeclaredServices(GeneratorResult generated)
    {
        var resolver = new ResourceIdentityResolver();
        foreach (var name in new[] { "BaseSeed", "IView" })
        {
            var type = generated.Assembly!.GetType("DeclaredMessages." + name)!;
            typeof(ResourceIdentityResolver).GetMethods().Single(method => method.Name == nameof(ResourceIdentityResolver.Identify) && method.IsGenericMethod && method.GetParameters().Length == 1).MakeGenericMethod(type)
                .Invoke(resolver, new[] { Selector(type) });
        }

        return new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
    }

    private sealed class FrozenInputs(StepMessage message) : IStepInputs
    {
        public T Get<T>(int producerIndex)
        {
            Assert.Equal(3, producerIndex);
            return (T)(object)message;
        }
    }

    private static Delegate Selector(Type type)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(type);
        return System.Linq.Expressions.Expression.Lambda(typeof(Func<,>).MakeGenericType(type, typeof(ResourceKey)),
            System.Linq.Expressions.Expression.Constant((ResourceKey)"configured"), parameter).Compile();
    }

    private static ScenarioDefinition Scenario(GeneratorResult generated, string method)
        => generated.Definitions().Single(definition => definition.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    private static IEnumerable<StepMessagePart> References(StepMessage message)
        => message.Parts.Where(part => part.Resource is not null);
}
