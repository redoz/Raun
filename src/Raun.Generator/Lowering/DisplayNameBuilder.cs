using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>A lowered display name: the constant template plus an optional string expression
/// (in terms of <c>__inputs</c>) for runtime placeholders; null when fully constant.</summary>
internal readonly record struct LoweredDisplayName(string Template, ExpressionSyntax? FormatExpression);

/// <summary>
/// Builds a step's display name from its <c>[StepName]</c> template: constant placeholders are
/// folded into a literal, and runtime ones become a string concatenation (in terms of
/// <c>__inputs</c>). The format expression is null when the name is fully constant.
/// </summary>
/// <remarks>
/// The runtime form is a <c>+</c> chain rather than an interpolated string: Roslyn has no factory
/// that escapes interpolated-string text, so building one means hand-escaping the very text this
/// generator no longer wants to touch. <c>"a " + (hole) + " b"</c> has the same semantics —
/// <c>ToString()</c> on the value, <c>null</c> rendered as empty — and Roslyn escapes every literal.
/// A chain that would START with a hole gets a leading <c>""</c>, so left associativity keeps every
/// <c>+</c> a string concatenation even for two adjacent holes.
/// </remarks>
internal static class DisplayNameBuilder
{
    public static LoweredDisplayName Build(
        SemanticModel model,
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> args,
        Dictionary<string, ExpressionSyntax> replacements)
    {
        var template = AttributeReader.StepTemplate(method) ?? method.Name;
        var tokens = TemplateTokenizer.Tokenize(template);
        var rewriter = new IdentifierReplacer(replacements);

        var constant = new StringBuilder();
        var format = new Concatenation();

        foreach (var token in tokens)
        {
            if (!token.IsPlaceholder)
            {
                constant.Append(token.Text);
                format.Append(token.Text);
                continue;
            }

            var argExpr = ArgumentForParameter(method, args, token.Text);
            // LINQ unrolling substitutes the loop variable with a literal, producing detached nodes the
            // model cannot evaluate. Fold those syntactically when every part is a literal (a plain
            // literal, or an interpolated string whose holes are literals) — `$"user-{1}"` is
            // "user-1" — so each unrolled step lists under its real name at discovery time instead of
            // three identical "{name}" entries. Anything else stays runtime-formatted.
            var inModel = argExpr is not null && argExpr.SyntaxTree == model.SyntaxTree;
            var constValue = inModel ? model.GetConstantValue(argExpr!) : default;
            string? folded = null;
            var foldedOk = argExpr is not null && !inModel && TryFoldDetached(argExpr, out folded);

            if (argExpr is not null && (constValue.HasValue || foldedOk))
            {
                var text = constValue.HasValue ? constValue.Value?.ToString() ?? "" : folded!;
                constant.Append(text);
                format.Append(text);
            }
            else if (argExpr is not null)
            {
                constant.Append('{').Append(token.Text).Append('}');
                // Parenthesize so the hole binds tighter than the surrounding `+`, whatever it is.
                format.Append(ParenthesizedExpression(
                    ((ExpressionSyntax)rewriter.Visit(argExpr)).WithoutTrivia()));
            }
            else
            {
                // No argument resolves the placeholder: it stays as written, in both forms.
                constant.Append('{').Append(token.Text).Append('}');
                format.Append("{" + token.Text + "}");
            }
        }

        return new LoweredDisplayName(constant.ToString(), format.Build());
    }

    /// <summary>Accumulates the <c>+</c> chain, merging adjacent literal runs into one literal and
    /// reporting null unless at least one runtime hole made it in.</summary>
    private sealed class Concatenation
    {
        private readonly List<ExpressionSyntax> _operands = [];
        private readonly StringBuilder _pending = new();
        private bool _anyHole;

        public void Append(string text) => _pending.Append(text);

        public void Append(ExpressionSyntax hole)
        {
            if (_pending.Length > 0)
            {
                Flush();
            }
            else if (_operands.Count == 0)
            {
                // A chain starting with a hole would not be a string concatenation.
                _operands.Add(Literal(""));
            }

            _operands.Add(hole);
            _anyHole = true;
        }

        public ExpressionSyntax? Build()
        {
            if (!_anyHole)
            {
                return null;
            }

            if (_pending.Length > 0)
            {
                Flush();
            }

            var chain = _operands[0];
            for (var i = 1; i < _operands.Count; i++)
            {
                chain = BinaryExpression(SyntaxKind.AddExpression, chain, _operands[i]);
            }

            return chain;
        }

        private void Flush()
        {
            _operands.Add(Literal(_pending.ToString()));
            _pending.Clear();
        }

        private static LiteralExpressionSyntax Literal(string text)
            => LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(text));
    }

    private static ExpressionSyntax? ArgumentForParameter(
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> args,
        string parameterName)
    {
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Name == parameterName && i < args.Count)
            {
                return args[i].Expression;
            }
        }

        return null;
    }

    /// <summary>Folds a detached expression made only of literals: a literal itself, a parenthesized
    /// one, or an interpolated string whose every hole is such an expression (no alignment or format
    /// clause). False for anything that needs evaluation.</summary>
    private static bool TryFoldDetached(ExpressionSyntax expression, out string? text)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.Token.Value is not null:
                text = literal.Token.Value is string s ? s : System.Convert.ToString(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
                return text is not null;

            case ParenthesizedExpressionSyntax parenthesized:
                return TryFoldDetached(parenthesized.Expression, out text);

            case InterpolatedStringExpressionSyntax interpolated:
                var builder = new StringBuilder();
                foreach (var content in interpolated.Contents)
                {
                    switch (content)
                    {
                        case InterpolatedStringTextSyntax part:
                            builder.Append(part.TextToken.ValueText);
                            break;
                        case InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole
                            when TryFoldDetached(hole.Expression, out var holeText):
                            builder.Append(holeText);
                            break;
                        default:
                            text = null;
                            return false;
                    }
                }

                text = builder.ToString();
                return true;

            default:
                text = null;
                return false;
        }
    }
}
