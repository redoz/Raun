using System.Text.RegularExpressions;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator builds every emitted node with <c>SyntaxFactory</c> and never assembles code as
/// text: no <c>Parse*</c> entry point, no <c>ToString()</c>/<c>Trim()</c> round trip. Text
/// concatenation is where escaping, precedence and qualification bugs hide, so this is a structural
/// guard rather than a style preference.
/// </summary>
public partial class EmissionDisciplineTests
{
    /// <summary>
    /// A call to ANY <c>Parse*</c> member, in the two spellings that can reach a parser: qualified by
    /// a receiver (<c>SyntaxFactory.ParseExpression(</c>, <c>CSharpSyntaxTree.ParseText(</c>, an alias
    /// like <c>SF.ParseExpression(</c>) — the <c>receiver</c> group, which the test clears only for
    /// <c>this</c> — or bare, which binds to <c>SyntaxFactory</c> in a file carrying the
    /// <c>using static</c> below. Deliberately not a whitelist of names: <c>ParseArgumentList</c>,
    /// <c>ParseToken</c>, <c>ParseLeadingTrivia</c> and the rest are parsers too. An object creation
    /// is excluded — <c>new ParsedGuard(…)</c> is a constructor, not a factory entry point.
    /// </summary>
    [GeneratedRegex(
        @"(?<!\bnew\s+)(?:(?<receiver>[A-Za-z_]\w*)\s*\.\s*|(?<![\w.]))(?<name>Parse\w*)\s*\(")]
    private static partial Regex ParseCall();

    /// <summary>
    /// A method DECLARATION of a <c>Parse*</c> name, so a class's own <c>Parse*</c> member and the bare
    /// calls to it are not mistaken for the factory's: <see cref="Lowering.ScenarioParser"/> lowers a
    /// statement in a private <c>ParseStatement</c> of its own, and it imports <c>SyntaxFactory</c>
    /// statically to BUILD syntax. Only names the file declares itself are exempted, and only in the
    /// bare form, so <c>SyntaxFactory.ParseStatement(</c> in that same file is still caught.
    /// </summary>
    [GeneratedRegex(@"\b(?:private|internal|protected|public)\b[^=;]*?\b(?<name>Parse\w*)\s*\(")]
    private static partial Regex ParseDeclaration();

    /// <summary>The text round trip the emitter used to do: render a node, then trim the result back
    /// into something to concatenate. Either renderer, any of the trims.</summary>
    [GeneratedRegex(@"\.To(?:Full)?String\(\s*\)\s*\.Trim(?:Start|End)?\s*\(")]
    private static partial Regex TextRoundTrip();

    private const string StaticFactoryImport = "using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;";

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

            // Comments are stripped first: prose is not code, and a comment naming a Parse* member
            // must not exempt that name for the whole file.
            var lines = File.ReadAllLines(file).Select(WithoutLineComment).ToArray();
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

                if (TextRoundTrip().IsMatch(line))
                {
                    offences.Add($"{location}: text round trip — {line.Trim()}");
                }

                foreach (var match in ParseCall().Matches(line).Cast<Match>())
                {
                    var receiver = match.Groups["receiver"];
                    var name = match.Groups["name"].Value;
                    var isParserCall = receiver.Success
                        ? receiver.Value != "this"
                        : importsStaticFactory && !declaredHere.Contains(name);
                    if (isParserCall)
                    {
                        var call = receiver.Success ? receiver.Value + "." + name : name;
                        offences.Add($"{location}: {call} — {line.Trim()}");
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

    /// <summary>Everything from the first <c>//</c> on. Crude on purpose: truncating a line that has
    /// <c>//</c> inside a string literal can only lose a match, never invent one.</summary>
    private static string WithoutLineComment(string line)
    {
        var comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
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
