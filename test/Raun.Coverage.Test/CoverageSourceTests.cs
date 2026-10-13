using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Coverage.Projects;
using Xunit;
using Raun.Reporting.Coverage;

namespace Raun.Coverage.Test;

public sealed class CoverageSourceTests
{
    [Theory]
    [InlineData("if (b)\n {\n  System.Console.WriteLine(1);\n  System.Console.WriteLine(2);\n  System.Console.WriteLine(3);\n  System.Console.WriteLine(4);\n }")]
    [InlineData("try { System.Console.WriteLine(0); }\n catch\n {\n  System.Console.WriteLine(1);\n  System.Console.WriteLine(2);\n  System.Console.WriteLine(3);\n  System.Console.WriteLine(4);\n }")]
    [InlineData("switch (b)\n {\n  case true:\n   System.Console.WriteLine(1);\n   System.Console.WriteLine(2);\n   System.Console.WriteLine(3);\n   System.Console.WriteLine(4);\n   break;\n }")]
    public void Skipped_branch_snippets_include_the_complete_body(string body)
    {
        var compilation = TestCompilation.Create("class C\n{\n void M(bool b)\n {\n" + body + "\n }\n\n\n\n void Unrelated() { }\n}");
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
        var snippet = Assert.Single(CoverageSourceBuilder.Build([(branch, BranchCategory.Logic, EvidenceStatus.NotReached)]));
        Assert.Contains("System.Console.WriteLine(4);", snippet.Text, StringComparison.Ordinal);
        var reference = Assert.Single(snippet.Branches);
        Assert.Equal(branch.Syntax.ToString(), snippet.Text.Substring(reference.Start, reference.Length));
        Assert.DoesNotContain("Unrelated", snippet.Text, StringComparison.Ordinal);
    }
    [Fact]
    public void Snippets_preserve_tokens_and_references_without_unrelated_source()
    {
        var compilation = TestCompilation.Create("class C\n{\n int Other() => 3;\n\n\n\n int M(bool b) { // guard\n if (b) return 1;\n return 2;\n }\n}");
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
        var source = Assert.Single(CoverageSourceBuilder.Build([(branch, BranchCategory.Logic, EvidenceStatus.NotReached)]));
        Assert.DoesNotContain("class C", source.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", source.Text, StringComparison.Ordinal);
        Assert.Equal(6, source.StartLine);
        Assert.Contains(source.Tokens, token => token.Kind == "keyword" && source.Text.Substring(token.Start, token.Length) == "if");
        Assert.Contains(source.Tokens, token => token.Kind == "comment" && source.Text.Substring(token.Start, token.Length) == "// guard");
        var references = source;
        var reference = Assert.Single(references.Branches);
        Assert.Equal(branch.Syntax.ToString(), source.Text.Substring(reference.Start, reference.Length));
        Assert.Equal("missed", reference.Outcome);
    }
    [Fact]
    public void Red_lines_require_verified_negative_evidence_without_positive_or_partial_hits()
    {
        var compilation = TestCompilation.Create("class C { int M(bool b) {\n if (b)\n return 1;\n return 2;\n} }");
        var branch = Assert.Single(BranchInventoryBuilder.Build(compilation, new CollectorScope(), TestContext.Current.CancellationToken));
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(branch.Syntax.SyntaxTree.GetText().ToString())));
        var document = new CoverageSource(branch.Syntax.SyntaxTree.FilePath, "SHA256", checksum);
        CoverageRange Range(int line, CoverageHit hit) => new(document, new(line, 1), new(line, 6), hit);
        var observations = new CoverageObservations([new CoverageModule("build", "Fixture.dll", [Range(1, CoverageHit.Yes), Range(1, CoverageHit.No), Range(2, CoverageHit.No), Range(3, CoverageHit.No), Range(3, CoverageHit.Partial)])]);
        var facts = new[] { (branch, (BranchCategory?)BranchCategory.Logic, EvidenceStatus.Incomplete) };
        var snippet = Assert.Single(CoverageSourceBuilder.Build(facts, observations));
        Assert.Equal([3], snippet.NotExecutedLines);
        Assert.Equal(3, snippet.Execution.Count);
        Assert.Equal("executed", snippet.Execution[0].State);
        Assert.Equal("partial", snippet.Execution[2].State);
        var invalid = observations with { Modules = [observations.Modules[0] with { Ranges = [Range(2, CoverageHit.No) with { Source = document with { Checksum = "wrong" } }] }] };
        Assert.Empty(Assert.Single(CoverageSourceBuilder.Build(facts, invalid)).NotExecutedLines);
        Assert.Empty(Assert.Single(CoverageSourceBuilder.Build(facts)).NotExecutedLines);
    }
}




