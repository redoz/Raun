using System.Text.RegularExpressions;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator builds every emitted node with <c>SyntaxFactory</c> and never assembles code as
/// text: no <c>Parse*</c> entry point, no <c>.ToFullString().Trim()</c> round trip. Text
/// concatenation is where escaping, precedence and qualification bugs hide, so this is a structural
/// guard rather than a style preference.
/// </summary>
public partial class EmissionDisciplineTests
{
    /// <summary>
    /// A <c>SyntaxFactory</c> parse entry point, in the only two spellings that reach one:
    /// qualified (<c>SyntaxFactory.ParseExpression(</c>) — the <c>receiver</c> group — or bare, which
    /// binds to <c>SyntaxFactory</c> only in a file carrying the <c>using static</c> below. The
    /// negative lookbehind on the bare form drops every OTHER receiver (<c>this.ParseStatement(</c>,
    /// <c>x.ParseName(</c>).
    /// </summary>
    [GeneratedRegex(
        @"(?:(?<receiver>SyntaxFactory\.)|(?<![\w.]))" +
        @"(?<name>ParseExpression|ParseTypeName|ParseName|ParseCompilationUnit|ParseSyntaxTree|ParseMemberDeclaration|ParseStatement)\s*\(")]
    private static partial Regex ParseCall();

    /// <summary>
    /// A method DECLARATION of one of those names, so a class's own <c>Parse*</c> member and the calls
    /// to it are not mistaken for the factory's: <see cref="Lowering.ScenarioParser"/> lowers a
    /// statement in a private <c>ParseStatement</c> of its own, and it imports <c>SyntaxFactory</c>
    /// statically to BUILD syntax. Only names the file declares itself are exempted, so a bare
    /// <c>ParseExpression(</c> there would still be caught.
    /// </summary>
    [GeneratedRegex(@"\b(?:private|internal|protected|public)\b[^=;]*?\b(?<name>Parse\w*)\s*\(")]
    private static partial Regex ParseDeclaration();

    private const string StaticFactoryImport = "using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;";

    private const string TextRoundTrip = ".ToFullString().Trim()";

    [Fact]
    public void The_generator_never_parses_source_text()
    {
        var generatorRoot = Path.Combine(RepositoryRoot(), "src", "Raun.Generator");
        Assert.True(Directory.Exists(generatorRoot), $"generator sources not found at {generatorRoot}");

        var offences = new List<string>();

        foreach (var file in Directory.EnumerateFiles(generatorRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(generatorRoot, file);
            if (IsBuildOutput(relative))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            var importsStaticFactory = lines.Any(
                line => line.Contains(StaticFactoryImport, StringComparison.Ordinal));
            var declaredHere = lines
                .SelectMany(line => ParseDeclaration().Matches(line).Cast<Match>())
                .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var location = $"{relative}({i + 1})";

                if (line.Contains(TextRoundTrip, StringComparison.Ordinal))
                {
                    offences.Add($"{location}: {TextRoundTrip} — {line.Trim()}");
                }

                foreach (var match in ParseCall().Matches(line).Cast<Match>())
                {
                    var name = match.Groups["name"].Value;
                    var isFactoryCall = match.Groups["receiver"].Success
                        || (importsStaticFactory && !declaredHere.Contains(name));
                    if (isFactoryCall)
                    {
                        offences.Add($"{location}: SyntaxFactory.{name} — {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "the generator must build syntax, never parse or concatenate source text:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    private static bool IsBuildOutput(string relativePath)
    {
        var head = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return head is "bin" or "obj";
    }

    /// <summary>Walks up from the test binaries until the directory holding <c>Raun.slnx</c>.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Raun.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
