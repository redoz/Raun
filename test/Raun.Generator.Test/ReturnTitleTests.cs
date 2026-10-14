using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Generator.Test;

public class ReturnTitleTests
{
    internal const string Source = """
        using System;
        using System.Threading.Tasks;
        using Raun;
        namespace ReturnTitles;
        public sealed class Seed(string key) : IResource<Seed>
        {
            public string Key { get; set; } = key;
            public static ResourceKey KeyFor(Seed seed) => seed.Key;
        }
        public sealed record Holder(string Name, DateTime Day);
        public sealed class Steps : Given<NoWorld>
        {
            public Task<Seed> Input() => Task.FromResult(new Seed("before"));
            [StepName("Input {input:id}; output {$return:id ?? name}; again {$return:data ?? name}")]
            public Task<Seed?> Produce(Seed input, string name = "optional")
            {
                input.Key = "after";
                return Task.FromResult<Seed?>(new Seed("returned"));
            }
            public Task Fail() => throw new InvalidOperationException("failed");
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Story()
            {
                var input = await Given.Input();
                await Given.Produce(name: "fallback", input: input);
            }
        }
        """;

    [Fact]
    public async Task Return_holes_replace_only_their_frozen_parts()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definition = generated.Definitions().Single();
        var producer = definition.Nodes[2];
        Assert.Equal(4, definition.Nodes.Count);
        Assert.Equal(new[] { 0, 1 }, producer.DependsOn);
        Assert.Empty(producer.Guards);
        Assert.Empty(producer.MergeSources);
        Assert.Equal("Input {input:id}; output fallback; again fallback", producer.DisplayNameTemplate);
        var results = await definition.RunAsync();
        Assert.All(results, result => Assert.Equal(StepStatus.Passed, result.Status));
        var title = results[2].TitleMessage;
        Assert.StartsWith("Input before; output returned; again ", title.Text, StringComparison.Ordinal);
        var references = title.Parts.Where(part => part.Resource.HasValue).ToArray();
        Assert.Equal(new[] { "before", "returned", "returned" }, references.Select(part => part.Resource!.Value.Key.Value));
        Assert.Equal("before", SnapshotKey(references[0]));
        Assert.All(references.Skip(1), part => Assert.Equal("returned", SnapshotKey(part)));
        Assert.NotNull(producer.FormatCompletedTitleMessage);
        var unexpected = StepMessage.FromText("custom frozen title");
        var formatting = new MessageFormattingContext(new ResourceIdentityResolver());
        Assert.Same(unexpected, producer.FormatCompletedTitleMessage(unexpected, new object(), formatting));
        Assert.Same(title, producer.FormatCompletedTitleMessage(title, null, formatting));
    }

    [Theory]
    [InlineData("null", "Passed")]
    [InlineData("throw new InvalidOperationException(\"failed\")", "Failed")]
    public async Task Null_and_failed_outputs_keep_frozen_fallback(string expression, string status)
    {
        var source = status == "Failed"
            ? Source.Replace("return Task.FromResult<Seed?>(new Seed(\"returned\"));", expression + ";", StringComparison.Ordinal)
            : Source.Replace("new Seed(\"returned\")", expression, StringComparison.Ordinal);
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var results = await generated.Definitions().Single().RunAsync();
        Assert.Equal(status, results[2].Status.ToString());
        Assert.Equal("Input before; output fallback; again fallback", results[2].DisplayName);
        Assert.Equal("before", Assert.Single(results[2].TitleMessage.Parts, part => part.Resource.HasValue).Resource!.Value.Key.Value);
    }

    [Fact]
    public async Task Skipped_output_keeps_available_fallback()
    {
        var generated = GeneratorHarness.Run(Source.Replace("await Given.Produce(name:", "await Given.Fail();\n                await Given.Produce(name:", StringComparison.Ordinal));
        generated.AssertCompiles();
        var results = await generated.Definitions().Single().RunAsync();
        var result = results.Single(item => item.Node.OperationName == "Produce");
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal("Input before; output fallback; again fallback", result.DisplayName);
    }

    [Theory]
    [InlineData("Task<string>", "Task.FromResult(\"\")", "{$return ?? name}", "", "fallback")]
    [InlineData("Task<int>", "Task.FromResult(0)", "{$return}", "0", "…")]
    [InlineData("Task<string?>", "Task.FromResult<string?>(null)", "{$return}", "…", "…")]
    [InlineData("Task<int>", "Task.FromResult(0)", "{$return}{$return:D2}", "000", "……")]
    [InlineData("ValueTask<bool>", "new ValueTask<bool>(false)", "{$return}", "False", "…")]
    [InlineData("ValueTask<DateTime>", "new ValueTask<DateTime>(new DateTime(2026, 10, 13, 9, 30, 0))", "{$return:HH:mm ?? holder.Day:yyyy-MM-dd}", "09:30", "{holder.Day:yyyy-MM-dd}")]
    [InlineData("Task<string>", "Task.FromResult(\"returned\")", "{$return ?? holder.Name}", "returned", "{holder.Name}")]
    [InlineData("Task<DateTime?>", "Task.FromResult<DateTime?>(null)", "{$return:HH:mm ?? holder.Day:yyyy-MM-dd}", "2026-10-13", "{holder.Day:yyyy-MM-dd}")]
    [InlineData("Task<string?>", "Task.FromResult<string?>(null)", "{$return ?? holder.Name}", "member", "{holder.Name}")]
    public async Task Return_binding_supports_task_shapes_formats_and_actual_null_semantics(string type, string expression, string template, string expected, string discovery)
    {
        var source = SimpleSource(type, expression, template);
        Assert.DoesNotContain(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), diagnostic => diagnostic.Id == "RAUN008");
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var definition = generated.Definitions().Single();
        Assert.Equal(discovery, definition.Nodes[1].DisplayNameTemplate);
        Assert.Equal(expected, (await definition.RunAsync())[1].DisplayName);
    }

    [Fact]
    public async Task Omitted_optional_fallback_requires_a_matching_title()
    {
        var source = SimpleSource("Task<string?>", "Task.FromResult<string?>(null)", "{$return:id ?? name}")
            .Replace("string name, Holder holder", "string name = \"optional\", Holder? holder = null", StringComparison.Ordinal)
            .Replace("name: \"fallback\", holder: new Holder(\"member\", new DateTime(2026, 10, 13))", "", StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        Assert.Equal("RAUN023", Assert.Single(diagnostics).Id);
    }

    [Theory]
    [InlineData("int", "-7", "-7", "-7")]
    [InlineData("long", "long.MinValue", "-9223372036854775808", "-9223372036854775808")]
    [InlineData("decimal", "-12.5m", "-12.5", "-12.5")]
    [InlineData("DayOfWeek", "DayOfWeek.Friday", "Friday", "5")]
    [InlineData("bool", "false", "False", "False")]
    [InlineData("string?", "null", "", "")]
    [InlineData("double", "double.NaN", "NaN", "NaN")]
    [InlineData("double", "double.PositiveInfinity", "Infinity", "Infinity")]
    [InlineData("double", "double.NegativeInfinity", "-Infinity", "-Infinity")]
    [InlineData("float", "float.NaN", "NaN", "NaN")]
    [InlineData("float", "float.PositiveInfinity", "Infinity", "Infinity")]
    [InlineData("float", "float.NegativeInfinity", "-Infinity", "-Infinity")]
    public async Task Explicit_optional_fallback_values_preserve_the_declared_parameter_type(string type, string value, string expected, string discovery)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // Discovery retains the existing constant-folding culture; make these expectations stable.
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var source = SimpleSource("Task<string?>", "Task.FromResult<string?>(null)", "{$return ?? name}")
                .Replace("string name, Holder holder", type + " name = " + value + ", Holder? holder = null", StringComparison.Ordinal)
                .Replace("name: \"fallback\", holder: new Holder(\"member\", new DateTime(2026, 10, 13))", "name: " + value, StringComparison.Ordinal);
            var generated = GeneratorHarness.Run(source);
            generated.AssertCompiles();
            Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
            var definition = generated.Definitions().Single();
            Assert.Equal(discovery, definition.Nodes[1].DisplayNameTemplate);
            Assert.Equal(expected, (await definition.RunAsync())[1].DisplayName);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("object", null)]
    [InlineData("BaseSeed", "BaseSeed")]
    [InlineData("IView", "IView")]
    public async Task Completion_formats_using_the_declared_result_type(string declaredType, string? resourceType)
    {
        var source = SimpleSource("Task<" + declaredType + ">", "Task.FromResult<" + declaredType + ">(new Seed())", "{$return:id ?? name}") + """

            public interface IView { }
            public class BaseSeed { }
            public sealed class Seed : BaseSeed, IView, IResource<Seed>
            {
                public static ResourceKey KeyFor(Seed seed) => "concrete";
                public override string ToString() => "data";
            }
            """;
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var resolver = new ResourceIdentityResolver();
        if (resourceType is not null)
        {
            var type = generated.Assembly!.GetType(resourceType)!;
            var parameter = System.Linq.Expressions.Expression.Parameter(type);
            var selector = System.Linq.Expressions.Expression.Lambda(typeof(Func<,>).MakeGenericType(type, typeof(ResourceKey)),
                System.Linq.Expressions.Expression.Constant((ResourceKey)"configured"), parameter).Compile();
            typeof(ResourceIdentityResolver).GetMethod(nameof(ResourceIdentityResolver.Identify))!.MakeGenericMethod(type)
                .Invoke(resolver, new[] { selector });
        }
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var results = await new ScenarioScheduler().RunAsync(generated.Definitions().Single(), services);
        var title = results[1].TitleMessage;
        Assert.Equal(resourceType is null ? "data" : "configured", title.Text);
        if (resourceType is null)
        {
            Assert.DoesNotContain(title.Parts, part => part.Resource.HasValue);
        }
        else
        {
            Assert.Equal(resourceType, Assert.Single(title.Parts, part => part.Resource.HasValue).Resource!.Value.Kind.Type!.Name);
        }
    }

    [Theory]
    [InlineData("Task", "Task.CompletedTask", "{$return ?? name}")]
    [InlineData("ValueTask", "ValueTask.CompletedTask", "{$return}")]
    [InlineData("Task<string>", "Task.FromResult(\"return\")", "{$return:HH:mm ?? name}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return ?? missing}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return ?? name ?? holder.Name}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return ?? name + name}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return.Member ?? name}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return: ?? name}")]
    [InlineData("Task<int>", "Task.FromResult(1)", "{$return ?? name:F2}")]
    public async Task Invalid_return_placeholders_use_RAUN008(string type, string expression, string template)
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(SimpleSource(type, expression, template), requireCompilable: true);
        Assert.Single(diagnostics, diagnostic => diagnostic.Id == "RAUN008");
    }

    [Theory]
    [InlineData("", "names")]
    [InlineData("\"one\", \"two\"", "names")]
    [InlineData("new[] { \"one\", \"two\" }", "names")]
    [InlineData("", "names.Length")]
    public async Task Return_fallback_rejects_params_roots(string arguments, string fallback)
    {
        var source = SimpleSource("Task<int>", "Task.FromResult(1)", "Created {$return ?? " + fallback + "} now")
            .Replace("string name, Holder holder", "params string[] names", StringComparison.Ordinal)
            .Replace("name: \"fallback\", holder: new Holder(\"member\", new DateTime(2026, 10, 13))", arguments, StringComparison.Ordinal);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "RAUN008");
        Assert.Contains("params", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_params_placeholders_keep_the_existing_binding()
    {
        var source = SimpleSource("Task<int>", "Task.FromResult(1)", "Existing {names.Length}")
            .Replace("string name, Holder holder", "params string[] names", StringComparison.Ordinal)
            .Replace("name: \"fallback\", holder: new Holder(\"member\", new DateTime(2026, 10, 13))", "new[] { \"one\", \"two\" }", StringComparison.Ordinal);
        Assert.DoesNotContain(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true), item => item.Id == "RAUN008");
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Equal("Existing 2", (await generated.Definitions().Single().RunAsync())[1].DisplayName);
    }

    private static string SimpleSource(string type, string expression, string template) => $$"""
        using System;
        using System.Threading.Tasks;
        using Raun;
        public sealed record Holder(string Name, DateTime Day);
        public sealed class Steps : Given<NoWorld>
        {
            [StepName("{{template}}")]
            public {{type}} Produce(string name, Holder holder) => {{expression}};
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario] public async Task Story()
            {
                await Given.Produce(name: "fallback", holder: new Holder("member", new DateTime(2026, 10, 13)));
            }
        }
        """;

    private static string? SnapshotKey(StepMessagePart part)
    {
        using var snapshot = JsonDocument.Parse(part.Snapshot!);
        return snapshot.RootElement.GetProperty("Key").GetString();
    }
}
