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
    /// <summary>The display name for one call: <paramref name="written"/> are its arguments as the
    /// scenario wrote them (for constant folding), <paramref name="lowered"/> the same arguments
    /// lowered for the generated step, index for index (for runtime holes).</summary>
    public static LoweredDisplayName Build(
        SemanticModel model,
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> written,
        IReadOnlyList<ArgumentSyntax> lowered)
    {
        var template = AttributeReader.StepTemplate(method) ?? method.Name;
        var tokens = TemplateTokenizer.Tokenize(template);

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

            var index = ArgumentIndexFor(method, written, token.Text);
            if (index < 0)
            {
                // No argument resolves the placeholder: it stays as written, in both forms.
                constant.Append('{').Append(token.Text).Append('}');
                format.Append("{" + token.Text + "}");
                continue;
            }

            // A constant folds into the name. So does a lowered argument made only of literals: a LINQ
            // unroll binds its loop variable to a literal, and `$"user-{i}"` becomes "user-1" — each
            // unrolled step then lists under its own name at discovery time, not three "{name}" entries.
            var constValue = model.GetConstantValue(written[index].Expression);
            var argument = lowered[index].Expression;
            string? folded = null;
            if (constValue.HasValue || TryFoldLiterals(argument, out folded))
            {
                var text = constValue.HasValue ? constValue.Value?.ToString() ?? "" : folded!;
                constant.Append(text);
                format.Append(text);
            }
            else
            {
                constant.Append('{').Append(token.Text).Append('}');
                // Parenthesize so the hole binds tighter than the surrounding `+`, whatever it is.
                format.Append(ParenthesizedExpression(argument.WithoutTrivia()));
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

    /// <summary>The index of the argument bound to the parameter a placeholder names, or -1.</summary>
    private static int ArgumentIndexFor(
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string parameterName)
    {
        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Name == parameterName)
            {
                return CallArguments.IndexOf(arguments, parameterName, i);
            }
        }

        return -1;
    }

    /// <summary>Folds an expression made only of literals: a literal itself, a parenthesized one, or
    /// an interpolated string whose every hole is such an expression (no alignment or format clause).
    /// Purely syntactic, so it works on lowered nodes the semantic model has never seen. False for
    /// anything that needs evaluation.</summary>
    private static bool TryFoldLiterals(ExpressionSyntax expression, out string? text)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.Token.Value is not null:
                text = literal.Token.Value is string s ? s : System.Convert.ToString(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture);
                return text is not null;

            case ParenthesizedExpressionSyntax parenthesized:
                return TryFoldLiterals(parenthesized.Expression, out text);

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
                            when TryFoldLiterals(hole.Expression, out var holeText):
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
