using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>Splits authored C# interpolation content into decoded literals and placeholder text.</summary>
internal static class TemplateTokenizer
{
    public readonly record struct Token(string Text, bool IsPlaceholder);

    public static List<Token> Tokenize(string template)
    {
        var parsed = TemplateInterpolation.Read(template, out var source);
        if (parsed is null) return new List<Token> { new(template, true) };
        var tokens = new List<Token>();
        foreach (var part in parsed.Contents)
        {
            if (part is InterpolatedStringTextSyntax literal)
                tokens.Add(new Token(literal.TextToken.ValueText.Replace("{{", "{").Replace("}}", "}"), false));
            else if (part is InterpolationSyntax hole)
                tokens.Add(new Token(source.Substring(hole.Span.Start + 1, hole.Span.Length - 2).Replace("\"\"", "\""), true));
        }
        return tokens;
    }
}
