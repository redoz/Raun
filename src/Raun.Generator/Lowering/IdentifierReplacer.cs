using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>
/// Replaces bare references to step-output locals (and LINQ loop variables) with the supplied
/// expressions. It leaves member names and argument labels alone, and is scope-aware: an
/// identifier shadowed by a lambda parameter is not rewritten.
/// </summary>
internal sealed class IdentifierReplacer : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, ExpressionSyntax> _map;
    private readonly HashSet<string> _shadowed = [];

    public IdentifierReplacer(Dictionary<string, ExpressionSyntax> map) => _map = map;

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        => WithShadow([node.Parameter.Identifier.Text], () => base.VisitSimpleLambdaExpression(node));

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        => WithShadow(
            node.ParameterList.Parameters.Select(p => p.Identifier.Text),
            () => base.VisitParenthesizedLambdaExpression(node));

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // Leave member names (`x.Member`) and argument labels (`name:` / `Name =`) untouched,
        // and never rewrite a name that a nested scope (lambda parameter) has shadowed.
        if ((node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
            || node.Parent is NameColonSyntax or NameEqualsSyntax
            || _shadowed.Contains(node.Identifier.Text))
        {
            return base.VisitIdentifierName(node);
        }

        return _map.TryGetValue(node.Identifier.Text, out var replacement)
            ? replacement.WithTriviaFrom(node)
            : base.VisitIdentifierName(node);
    }

    private SyntaxNode? WithShadow(IEnumerable<string> names, Func<SyntaxNode?> visit)
    {
        var added = names.Where(_shadowed.Add).ToList();
        try
        {
            return visit();
        }
        finally
        {
            foreach (var name in added)
            {
                _shadowed.Remove(name);
            }
        }
    }
}
