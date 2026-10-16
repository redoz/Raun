using Microsoft.CodeAnalysis;
using Xunit;

namespace Raun.Generator.Test;

public class ScenarioCaseDiagnosticsTests
{
    private static string Source(string parameters, string attributes = "", string extra = "", bool scenario = true)
        => SampleSources.Dsl + $$"""

        public enum Actor { Anonymous, Reader }
        public enum OtherActor { Reader }
        public sealed class AuthorizationScenarios : AppointmentSuite
        {
            {{(scenario ? "[Scenario]" : "")}}
            {{attributes}}
            public async Task Authorization({{parameters}}) { await Given.DatabaseIsClean(); }
            {{extra}}
        }
        """;

    [Theory]
    [InlineData("string path", "", "RAUN033", "Authorization")]
    [InlineData("", "[ScenarioCase(\"unused\", 1)]", "RAUN033", "ScenarioCase")]
    [InlineData("int value", "[ScenarioCase(\" \u0020\", 1)]", "RAUN033", "ScenarioCase")]
    [InlineData("int value", "[ScenarioCase(\"one\", 1, 2)]", "RAUN033", "ScenarioCase")]
    [InlineData("int value", "[ScenarioCase(\"one\")]", "RAUN033", "ScenarioCase")]
    [InlineData("int value", "[ScenarioCase(\"one\", \"text\")]", "RAUN034", "\"text\"")]
    [InlineData("byte value", "[ScenarioCase(\"one\", 256)]", "RAUN034", "256")]
    [InlineData("Actor actor", "[ScenarioCase(\"one\", OtherActor.Reader)]", "RAUN034", "OtherActor.Reader")]
    [InlineData("object value", "[ScenarioCase(\"one\", typeof(string))]", "RAUN034", "typeof(string)")]
    [InlineData("object value", "[ScenarioCase(\"one\", new int[] { 1 })]", "RAUN034", "new int[]")]
    [InlineData("int value = 1", "[ScenarioCase(\"one\", 1)]", "RAUN035", "Authorization")]
    [InlineData("ref int value", "[ScenarioCase(\"one\", 1)]", "RAUN035", "Authorization")]
    [InlineData("params int[] value", "[ScenarioCase(\"one\", 1)]", "RAUN035", "Authorization")]
    public async Task Invalid_cases_are_located_and_refused(string parameters, string attributes, string id, string span)
    {
        var source = Source(parameters, attributes);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(id, diagnostic.Id);
        Assert.Contains(span, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length), StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorizationScenarios_Authorization", GeneratorHarness.Run(source).GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_names_are_errors_but_case_is_significant()
    {
        var duplicate = Source("int value", "[ScenarioCase(\"one\", 1)] [ScenarioCase(\"one\", 2)]");
        Assert.Equal("RAUN033", Assert.Single(await GeneratorHarness.DiagnoseAsync(duplicate)).Id);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(Source("int value", "[ScenarioCase(\"one\", 1)] [ScenarioCase(\"One\", 2)]"), requireCompilable: true));
    }

    [Theory]
    [InlineData("string path, Actor actor, int expected", "[ScenarioCase(\"reader\", \"/orders\", Actor.Reader, 200)]")]
    [InlineData("byte value", "[ScenarioCase(\"one\", 255)]")]
    [InlineData("string? value", "[ScenarioCase(\"missing\", null)]")]
    [InlineData("int? value", "[ScenarioCase(\"missing\", null)]")]
    [InlineData("long value", "[ScenarioCase(\"one\", 1)]")]
    public async Task Constants_enums_null_and_implicit_conversions_are_valid(string parameters, string attributes)
        => Assert.Empty(await GeneratorHarness.DiagnoseAsync(Source(parameters, attributes), requireCompilable: true));

    [Fact]
    public async Task Cases_require_the_scenario_marker()
        => Assert.Equal("RAUN033", Assert.Single(await GeneratorHarness.DiagnoseAsync(Source("int value", "[ScenarioCase(\"one\", 1)]", scenario: false))).Id);

    [Fact]
    public async Task Case_method_overloads_are_refused()
        => Assert.Equal("RAUN035", Assert.Single(await GeneratorHarness.DiagnoseAsync(Source("int value", "[ScenarioCase(\"one\", 1)]", "public void Authorization() { }"))).Id);

    [Fact]
    public async Task Generic_case_methods_are_refused()
    {
        var source = Source("int value", "[ScenarioCase(\"one\", 1)]").Replace("Authorization(int", "Authorization<T>(int", StringComparison.Ordinal);
        Assert.Equal("RAUN035", Assert.Single(await GeneratorHarness.DiagnoseAsync(source)).Id);
    }

    [Fact]
    public async Task Inaccessible_boxed_enum_is_diagnosed_before_emission()
    {
        var source = ScenarioCaseGenerationTests.Source("[ScenarioCase(\"one\", Secret.Reader)]", "object value")
            .Replace("[Scenario(\"authorization\")]", "private enum Secret { Reader }\n    [Scenario(\"authorization\")]", StringComparison.Ordinal);
        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));
        Assert.Equal("RAUN034", diagnostic.Id);
        Assert.Contains("Secret.Reader", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length), StringComparison.Ordinal);
        Assert.DoesNotContain("CaseDemo_Cases_Run", GeneratorHarness.Run(source).GeneratedSource, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Untyped_default_null_cannot_bind_to_a_nonnullable_value_parameter()
    {
        var source = ScenarioCaseGenerationTests.Source("[ScenarioCase(\"one\", default)]", "int value");
        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));
        Assert.Equal("RAUN034", diagnostic.Id);
        Assert.DoesNotContain("CaseDemo_Cases_Run", GeneratorHarness.Run(source).GeneratedSource, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("value++")]
    [InlineData("++value")]
    [InlineData("value = 2")]
    [InlineData("value += 2")]
    [InlineData("System.Threading.Interlocked.Increment(ref value)")]
    [InlineData("int.TryParse(\"2\", out value)")]
    public async Task Case_argument_writes_are_refused(string expression)
    {
        var source = ScenarioCaseGenerationTests.Source("[ScenarioCase(\"one\", 1)]", "int value", "await Given.Capture(" + expression + ");");
        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));
        Assert.Equal("RAUN007", diagnostic.Id);
        Assert.DoesNotContain("CaseDemo_Cases_Run", GeneratorHarness.Run(source).GeneratedSource, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Type_error_names_the_parameter_and_expected_type()
    {
        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(Source("Actor actor", "[ScenarioCase(\"one\", \"Reader\")]")));
        Assert.Contains("actor", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Contains("Actor", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
