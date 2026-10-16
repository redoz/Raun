using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Lowering;
using Xunit;

namespace Raun.Generator.Test;

public class TemplateInterpolationTests
{
    [Fact]
    public void Return_adapter_preserves_token_width_and_format_text()
    {
        var hole = Assert.IsType<InterpolationSyntax>(TemplateInterpolation.ReadHole("$return:literal $return", out var translated));
        var name = Assert.IsType<IdentifierNameSyntax>(hole.Expression);
        Assert.True(translated);
        Assert.Equal("@return", name.Identifier.Text);
        Assert.Equal(7, name.Span.Length);
        Assert.Equal(4, name.Span.Start);
        Assert.Equal("literal $return", hole.FormatClause!.FormatStringToken.ValueText);
    }

    [Theory]
    [InlineData("patient.Name", SyntaxKind.SimpleMemberAccessExpression)]
    [InlineData("@return", SyntaxKind.IdentifierName)]
    [InlineData("@event", SyntaxKind.IdentifierName)]
    public void Roslyn_reads_paths_and_escaped_identifiers(string input, SyntaxKind kind)
    {
        var hole = Assert.IsType<InterpolationSyntax>(TemplateInterpolation.ReadHole(input, out var translated));
        Assert.False(translated);
        Assert.Equal(kind, hole.Expression.Kind());
    }

    [Theory]
    [InlineData("$returnValue")]
    [InlineData("patient.")]
    [InlineData("name} extra {name")]
    [InlineData("value:")]
    [InlineData("event")]
    [InlineData("return")]
    [InlineData("day:\\{yyyy")]
    [InlineData("value:'{'0")]
    [InlineData("")]
    public void Non_CSharp_holes_are_rejected(string input)
        => Assert.Null(TemplateInterpolation.ReadHole(input, out _));

    [Fact]
    public async Task Escaped_identifiers_and_spaced_paths_bind_without_becoming_return_holes()
    {
        const string source = """
            using System.Threading.Tasks;
            using Raun;
            public sealed record Holder(string @event);
            public sealed class Probe : Given<NoWorld>
            {
                [StepName("literal $return {@return} {holder . @event}; output {$return ?? @return}")]
                public Task<string> Read(string @return, Holder holder) => Task.FromResult("created");
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public Probe Given => Steps<Probe>();
                [Scenario] public async Task Story()
                {
                    await Given.Read("input", new Holder("patient"));
                }
            }
            """;
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source, requireCompilable: true));
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Equal("literal $return input patient; output created", (await Assert.Single(generated.Definitions()).RunAsync())[1].DisplayName);
    }

}
