using System;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>C# interpolation parsing for authored templates. The sole syntax extension replaces a
/// leading $return hole token with the same-width escaped identifier @return.</summary>
internal static class TemplateInterpolation
{
    public static InterpolatedStringExpressionSyntax? Read(string template, out string source)
    {
        source = "$@\"" + template.Replace("\"", "\"\"") + "\"";
        var normalized = new StringBuilder(template);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;
            if (i + 1 < template.Length && template[i + 1] == '{') { i++; continue; }
            var start = i + 1;
            while (start < template.Length && char.IsWhiteSpace(template[start])) start++;
            if (IsReturn(template, start)) normalized[start] = '@';
        }
        var text = "$@\"" + normalized.ToString().Replace("\"", "\"\"") + "\"";
        var parsed = SyntaxFactory.ParseExpression(text);
        if (parsed is not InterpolatedStringExpressionSyntax { ContainsDiagnostics: false } result) return null;
        // Empty formats are rejected by the C# compiler after parsing (CS8089).
        foreach (var part in result.Contents)
            if (part is InterpolationSyntax { FormatClause.FormatStringToken.ValueText.Length: 0 }) return null;
        return result;
    }

    public static InterpolationSyntax? ReadHole(string placeholder, out bool translatedReturn)
    {
        var start = 0;
        while (start < placeholder.Length && char.IsWhiteSpace(placeholder[start])) start++;
        translatedReturn = IsReturn(placeholder, start);
        var parsed = Read("{" + placeholder + "}", out _);
        return parsed is { Contents.Count: 1 } && parsed.Contents[0] is InterpolationSyntax hole ? hole : null;
    }

    private static bool IsReturn(string text, int start)
        => start + 7 <= text.Length && string.CompareOrdinal(text, start, "$return", 0, 7) == 0
            && (start + 7 == text.Length || !SyntaxFacts.IsIdentifierPartCharacter(text[start + 7]));
}
