using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>Chooses a title whose placeholder roots were explicitly supplied at this call.</summary>
internal static class StepTemplateSelector
{
    public static string? Select(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        string? best = null;
        var bestCount = -1;
        foreach (var template in AttributeReader.StepTemplates(method))
        {
            var parameters = new HashSet<string>();
            var eligible = true;
            foreach (var token in TemplateTokenizer.Tokenize(template))
            {
                if (!token.IsPlaceholder) continue;
                if (!PlaceholderPath.TryBind(method, token.Text, out var path, out _))
                {
                    eligible = false;
                    break;
                }
                // A return value is available on completion. Its discovery fallback, if any,
                // still requires an explicit argument, just like a normal parameter path.
                var parameter = path!.Parameter ?? path.Fallback?.Parameter;
                if (parameter is null) continue;
                if (CallArguments.IndexOf(arguments, parameter.Name, parameter.Ordinal) < 0)
                {
                    eligible = false;
                    break;
                }
                parameters.Add(parameter.Name);
            }
            if (eligible && parameters.Count > bestCount)
            {
                best = template;
                bestCount = parameters.Count;
            }
        }
        return best;
    }
}