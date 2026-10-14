using Microsoft.CodeAnalysis;
using Xunit;

namespace Raun.Generator.Test;

public class StepNameSelectionTests
{
    [Theory]
    [InlineData("[StepName(\"create {name}\")] [StepName(\"create {name} at {time}\")]", "\"Jane\", \"noon\"", "create Jane at noon")]
    [InlineData("[StepName(\"create {name} at {time}\")] [StepName(\"create {name}\")]", "\"Jane\"", "create Jane")]
    [InlineData("[StepName(\"create {name} at {time}\")]", "time: \"noon\", name: \"Jane\"", "create Jane at noon")]
    [InlineData("[StepName(\"first {name}\")] [StepName(\"second {time}\")]", "\"Jane\", \"noon\"", "first Jane")]
    [InlineData("[StepName(\"repeat {name} {name}\")] [StepName(\"both {name} {time}\")]", "\"Jane\", \"noon\"", "both Jane noon")]
    [InlineData("", "", "Create")]
    [InlineData("[StepName(\"create {name}\")]", "@name: \"Jane\"", "create Jane")]
    [InlineData("[StepName(\"create {name}\")] [StepName(\"create\")]", "", "create")]
    [InlineData("[StepName(\"length {name.Length}\")]", "\"Jane\"", "length 4")]
    [InlineData("[StepName(\"paths {name.Length} {name.Length}\")] [StepName(\"both {name} {time}\")]", "\"Jane\", \"noon\"", "both Jane noon")]
    public async Task Chooses_the_most_specific_title_with_supplied_parameters(string attributes, string arguments, string expected)
    {
        var result = GeneratorHarness.Run(Source(attributes, arguments));
        result.AssertCompiles();
        var results = await result.Definitions().Single().RunAsync();
        Assert.Equal(expected, results[1].DisplayName);
        Assert.Equal(Raun.Model.StepStatus.Passed, results[1].Status);
    }

    [Theory]
    [InlineData("[StepName(\"create {name} at {time}\")]", "\"Jane\"", true)]
    [InlineData("[StepName(\"create {name} at {time}\")]", "time: \"noon\"", true)]
    [InlineData("[StepName(\"create {name} at {time}\")] [StepName(\"create {name}\")]", "\"Jane\"", false)]
    [InlineData("[StepName(\"create {name}\")] [StepName(\"create\")]", "", false)]
    [InlineData("[StepName(\"create {name} at {time}\")]", "time: \"noon\", name: \"Jane\"", false)]
    [InlineData("[StepName(\"create {name}\")]", "name: null", false)]
    [InlineData("", "", false)]
    [InlineData("[StepName(\"create {name}\")]", "@name: \"Jane\"", false)]
    public async Task Errors_at_the_call_only_when_no_declared_title_is_usable(string attributes, string arguments, bool error)
    {
        var source = Source(attributes, arguments);
        var diagnostics = await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true);
        if (error)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("RAUN023", diagnostic.Id);
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Equal("Given.Create(" + arguments + ")", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        }
        else
        {
            Assert.Empty(diagnostics);
        }
    }

    [Fact]
    public async Task Validates_placeholders_in_every_title()
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(
            Source("[StepName(\"create {name}\")] [StepName(\"create {missing}\")]", "\"Jane\""), requireCompilable: true);
        Assert.Equal("RAUN008", Assert.Single(diagnostics).Id);
    }

    private static string Source(string attributes, string arguments) => $$"""
        using System.Threading.Tasks;
        using Raun;
        public sealed class Steps : Given<NoWorld>
        {
            {{attributes}}
            public Task Create(string? name = "default", string time = "default") => Task.CompletedTask;
        }
        public sealed class Stories : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario]
            public async Task Example() { await Given.Create({{arguments}}); }
        }
        """;
}