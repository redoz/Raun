using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class ScenarioCaseGenerationTests
{
    internal const string Dsl = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using Raun;
        namespace CaseDemo;
        [Flags] public enum Actor { None=0, Reader=1, Writer=2 }
        public sealed class Probe : Given<NoWorld>
        {
            [StepName("capture {value}")]
            public Task Capture<T>(T value)
            {
                Context.Log(typeof(T).FullName!);
                Context.Log(value?.GetType().FullName ?? "<null>");
                Context.Log(value is null ? "<null>" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
                return Task.CompletedTask;
            }
            [StepName("allow {value}")]
            public Task<bool> Allow(bool value) => Task.FromResult(value);
        }
        public abstract class Suite : Scenarios<NoWorld> { public Probe Given => Steps<Probe>(); }
        """;

    internal static string Source(string cases, string parameters = "string value", string body = "await Given.Capture(value);")
        => Dsl + $$"""

        public sealed class Cases : Suite
        {
            [Scenario("authorization")]
            {{cases}}
            public async Task Run({{parameters}}) { {{body}} }
        }
        """;

    [Fact]
    public async Task Rows_generate_independent_definitions_with_typed_values_and_original_metadata()
    {
        var result = GeneratorHarness.Run(Source("[ScenarioCase(\"reader\", \"Alice\")] [ScenarioCase(\"anonymous\", \"Bob\")]"));
        result.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(Source("[ScenarioCase(\"reader\", \"Alice\")] [ScenarioCase(\"anonymous\", \"Bob\")]")));
        var definitions = result.Definitions();
        Assert.Equal(2, definitions.Count);
        Assert.Equal(["authorization [reader]", "authorization [anonymous]"], definitions.Select(d => d.DisplayName));
        Assert.Equal(2, definitions.Select(d => d.ScenarioId).Distinct().Count());
        Assert.Equal(definitions.Sum(d => d.Nodes.Count), definitions.SelectMany(d => d.Nodes).Select(n => n.StepId).Distinct().Count());
        foreach (var (definition, name) in definitions.Zip(new[] { "Alice", "Bob" }))
        {
            Assert.Equal("CaseDemo.Cases.Run", definition.MethodName);
            Assert.Equal("Run", definition.Method!.Name);
            Assert.Equal(typeof(string), Assert.Single(definition.Method.GetParameters()).ParameterType);
            var steps = await definition.RunAsync();
            Assert.All(steps, step => Assert.Equal(StepStatus.Passed, step.Status));
            Assert.Equal(["System.String", "System.String", name], steps.Single(s => s.Node.OperationName == "Capture").Logs);
        }
        // Other compilations can register the same method name with different case rows.
        var unrelated = GeneratorHarness.Run(Source("[ScenarioCase(\"a\", \"other\")]"));
        unrelated.AssertCompiles();
        _ = unrelated.Definitions();
        Assert.Contains(ScenarioRegistry.Definitions(), d => d.Method?.DeclaringType?.Assembly == unrelated.Assembly);
        var registered = ScenarioRegistry.Definitions()
            .Where(d => d.Method?.DeclaringType?.Assembly == result.Assembly && d.MethodName == "CaseDemo.Cases.Run")
            .ToList();
        Assert.Equal(definitions.Select(d => d.DisplayName), registered.Select(d => d.DisplayName));
    }

    [Theory]
    [InlineData("byte value", "255", "System.Byte", "255")]
    [InlineData("long value", "-2147483648", "System.Int64", "-2147483648")]
    [InlineData("object value", "Actor.Reader | Actor.Writer", "CaseDemo.Actor", "Reader, Writer")]
    [InlineData("Actor value", "0", "CaseDemo.Actor", "None")]
    [InlineData("string? value", "null", "<null>", "<null>")]
    [InlineData("int? value", "null", "<null>", "<null>")]
    [InlineData("string value", "\"quote \\\" and \\n newline\"", "System.String", "quote \" and \n newline")]
    [InlineData("double value", "double.NaN", "System.Double", "NaN")]
    [InlineData("double value", "double.PositiveInfinity", "System.Double", "Infinity")]
    [InlineData("ulong value", "ulong.MaxValue", "System.UInt64", "18446744073709551615")]
    public async Task Supported_constants_preserve_value_and_runtime_type(string parameter, string value, string type, string expected)
    {
        var result = GeneratorHarness.Run(Source("[ScenarioCase(\"one\", " + value + ")]", parameter));
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        var steps = await definition.RunAsync();
        Assert.All(steps, step => Assert.True(step.Status == StepStatus.Passed, step.Exception?.ToString()));
        var notes = steps.Single(s => s.Node.OperationName == "Capture").Logs;
        Assert.Equal(type, notes[1]);Assert.Equal(expected, notes[2]);
    }

    [Fact]
    public void Reordering_and_value_edits_preserve_case_and_step_ids()
    {
        var a = GeneratorHarness.Run(Source("[ScenarioCase(\"a\", \"one\")] [ScenarioCase(\"b\", \"two\")]"));
        var b = GeneratorHarness.Run(Source("[ScenarioCase(\"b\", \"changed\")] [ScenarioCase(\"inserted\", \"new\")] [ScenarioCase(\"a\", \"one\")]"));
        a.AssertCompiles();b.AssertCompiles();
        foreach (var previous in a.Definitions())
        {
            var current = b.Definitions().Single(d => d.DisplayName == previous.DisplayName);
            Assert.Equal(previous.ScenarioId, current.ScenarioId);
            Assert.Equal(previous.Nodes.Select(n => n.StepId), current.Nodes.Select(n => n.StepId));
        }
        Assert.Equal(["authorization [b]", "authorization [inserted]", "authorization [a]"], b.Definitions().Select(d => d.DisplayName));
    }

    [Fact]
    public async Task Case_arguments_flow_into_conditions_titles_and_parallel_steps()
    {
        var source = Source("[ScenarioCase(\"yes\", true, \"allowed\")] [ScenarioCase(\"no\", false, \"denied\")]", "bool permitted, string value",
            "if (await Given.Allow(permitted)) { await (Given.Capture(value), Given.Capture(value + \"!\")); } else { await Given.Capture(value); }");
        var result = GeneratorHarness.Run(source);result.AssertCompiles();
        foreach (var definition in result.Definitions())
        {
            var steps = await definition.RunAsync();
            Assert.DoesNotContain(steps, step => step.Status == StepStatus.Failed);
            Assert.Contains(steps, step => step.DisplayName.Contains(definition.DisplayName.EndsWith("[yes]", StringComparison.Ordinal) ? "allowed" : "denied", StringComparison.Ordinal));
            Assert.Single(definition.Nodes, node => node.IsSetup);Assert.Single(definition.Nodes, node => node.IsTeardown);
        }
    }

    [Fact]
    public async Task Nested_lambda_parameters_are_bound_by_symbol()
    {
        var result = GeneratorHarness.Run(Source("[ScenarioCase(\"outer\", \"outer\")]", body:
            "await Given.Capture(new[] { \"inner\" }.Select(inner => inner + value).Single());"));
        result.AssertCompiles();
        var steps = await Assert.Single(result.Definitions()).RunAsync();
        Assert.Contains(steps, step => step.Logs.Contains("innerouter"));
    }
}
