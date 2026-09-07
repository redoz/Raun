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
    /// resolves to <c>SyntaxFactory</c> only in a file with the <c>using static</c> below. The
    /// negative lookbehind on the bare form drops every OTHER receiver (<c>this.ParseStatement(</c>,
    /// <c>x.ParseName(</c>), and the <c>using static</c> condition drops a file's own private
    /// <c>Parse*</c> methods: <see cref="Lowering.ScenarioParser"/> declares and calls
    /// <c>ParseStatement</c>/<c>ParseExpressionStatement</c> of its own and imports no static
    /// <c>SyntaxFactory</c>, so those never trip the guard. (A file that both imported
    /// <c>SyntaxFactory</c> statically AND declared its own <c>Parse*</c> member would be a false
    /// positive; no file does, and the split is deliberate.)
    /// </summary>
    [GeneratedRegex(
        @"(?:(?<receiver>SyntaxFactory\.)|(?<![\w.]))" +
        @"(?<name>ParseExpression|ParseTypeName|ParseName|ParseCompilationUnit|ParseSyntaxTree|ParseMemberDeclaration|ParseStatement)\s*\(")]
    private static partial Regex ParseCall();

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
                    if (match.Groups["receiver"].Success || importsStaticFactory)
                    {
                        offences.Add($"{location}: SyntaxFactory.{match.Groups["name"].Value} — {line.Trim()}");
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
