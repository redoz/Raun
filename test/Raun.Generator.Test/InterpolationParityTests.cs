using System.Globalization;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class InterpolationParityTests
{
    [Fact]
    public async Task Titles_notes_and_logs_share_braces_alignment_and_formats()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using Raun;
            public sealed class Probe : Given<NoWorld>
            {
                [StepName("{{{name,8}}} | {count,-5:D2} | {day:HH:mm} | {{done}} $return")]
                public Task Render(string name, int count, DateTime day)
                {
                    Context.Log($"{{{name,8}}} | {count,-5:D2} | {day:HH:mm} | {{done}} $return");
                    return Task.CompletedTask.Note($"{{{name,8}}} | {count,-5:D2} | {day:HH:mm} | {{done}} $return");
                }
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public Probe Given => Steps<Probe>();
                [Scenario] public async Task Story() { await Given.Render("Alice", 5, new DateTime(2026, 10, 16, 9, 30, 0)); }
            }
            """;
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var result = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, result.Status);
        var expected = string.Create(CultureInfo.InvariantCulture, $"{{{"Alice",8}}} | {5,-5:D2} | {new DateTime(2026, 10, 16, 9, 30, 0):HH:mm} | {{done}} $return");
        Assert.Equal(expected, result.DisplayName);
        Assert.Equal(expected, Assert.Single(result.Logs));
        Assert.Equal(expected, Assert.Single(result.Notes));
    }

    [Theory]
    [InlineData(false, "   06")]
    [InlineData(true, "   05")]
    public async Task Return_fallback_uses_one_common_alignment_and_format(bool returnNull, string expected)
    {
        var source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed class Probe : Given<NoWorld>
            {
                [StepName("{$return ?? value,5:D2}")]
                public Task<int?> Produce(int value, bool returnNull)
                {
                    int? result = returnNull ? null : 6;
                    Context.Log($"{result ?? value,5:D2}");
                    return Task.FromResult(result).Note($"{result ?? value,5:D2}");
                }
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public Probe Given => Steps<Probe>();
                [Scenario] public async Task Story() { await Given.Produce(5, RETURN_NULL); }
            }
            """.Replace("RETURN_NULL", returnNull ? "true" : "false", StringComparison.Ordinal);
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var definition = Assert.Single(generated.Definitions());
        Assert.Equal("   05", definition.Nodes[1].DisplayNameTemplate);
        var result = (await definition.RunAsync())[1];
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(expected, result.DisplayName);
        Assert.Equal(expected, Assert.Single(result.Logs));
        Assert.Equal(expected, Assert.Single(result.Notes));
    }

    [Theory]
    [InlineData("quoted \"text\" \\ path {{literal}} {name}", "quoted \"text\" \\ path {literal} Name")]
    [InlineData("{text:N2}", "Alice")]
    [InlineData("{text,8:N2}", "   Alice")]
    [InlineData("{{$return}} and {name}", "{$return} and Name")]
    public async Task Literal_and_format_rules_match_actual_CSharp(string template, string expected)
    {
        var literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(template, quote: true);
        var source = $$"""
            using System.Threading.Tasks;
            using Raun;
            public sealed class Probe : Given<NoWorld>
            {
                [StepName({{literal}})]
                public Task Render(int value, string text, string name)
                {
                    Context.Log(${{literal}});
                    return Task.CompletedTask.Note(${{literal}});
                }
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public Probe Given => Steps<Probe>();
                [Scenario] public async Task Story() { await Given.Render(5, "Alice", "Name"); }
            }
            """;
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var result = (await Assert.Single(generated.Definitions()).RunAsync())[1];
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(expected, result.DisplayName);
        Assert.Equal(expected, Assert.Single(result.Logs));
        Assert.Equal(expected, Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Runtime_strings_and_member_paths_ignore_formats_like_CSharp()
    {
        const string source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed record Holder(string Name);
            public sealed class Probe : Given<NoWorld>
            {
                public Task<string> Text() => Task.FromResult("Alice");
                [StepName("{text:N2} {holder.Name:N2}")]
                public Task Render(string text, Holder holder)
                {
                    Context.Log($"{text:N2} {holder.Name:N2}");
                    return Task.CompletedTask.Note($"{text:N2} {holder.Name:N2}");
                }
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public Probe Given => Steps<Probe>();
                [Scenario] public async Task Story()
                {
                    var text = await Given.Text();
                    await Given.Render(text, new Holder(text));
                }
            }
            """;
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        var result = (await Assert.Single(generated.Definitions()).RunAsync())[2];
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal("Alice Alice", result.DisplayName);
        Assert.Equal(result.DisplayName, Assert.Single(result.Logs));
        Assert.Equal(result.DisplayName, Assert.Single(result.Notes));
    }
}
