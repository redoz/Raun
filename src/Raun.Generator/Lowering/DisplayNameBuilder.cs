using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>The folded discovery template, optional legacy string expression, and optional typed
/// title builder. Runtime expressions use __inputs; the typed builder also uses __formatting.</summary>
internal readonly record struct LoweredDisplayName(string Template, ExpressionSyntax? FormatExpression, ExpressionSyntax? TitleExpression);

/// <summary>
/// Builds a step's display name from its <c>[StepName]</c> template: constant placeholders are
/// folded into a literal, and runtime ones — a member path such as <c>{specification.Outcome}</c>
/// always is — feed a typed message builder and, for previously supported formats, a legacy string
/// concatenation. Fully constant names need neither runtime expression.
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
        var message = new Message();
        var reservedFormat = false;

        foreach (var token in tokens)
        {
            if (!token.IsPlaceholder)
            {
                constant.Append(token.Text);
                format.Append(token.Text);
                message.Append(token.Text);
                continue;
            }

            var index = -1;
            PlaceholderPath? path = null;
            if (PlaceholderPath.TryBind(method, token.Text, out path, out _))
            {
                var ordinal = method.Parameters.IndexOf(path!.Parameter);
                index = CallArguments.IndexOf(written, path.Parameter.Name, ordinal);
            }

            if (index < 0)
            {
                // No argument resolves the placeholder: it stays as written, in both forms.
                constant.Append('{').Append(token.Text).Append('}');
                format.Append("{" + token.Text + "}");
                message.Append("{" + token.Text + "}");
                continue;
            }

            var argument = lowered[index].Expression.WithoutTrivia();
            // Bind the value as the called method does before accessing members or inferring the
            // formatter's generic type. The producer may return a more concrete type than this parameter.
            var boundArgument = CastExpression(TypeSyntaxFactory.From(path!.Parameter.Type), ParenthesizedExpression(argument));
            message.Append(path.Walk(boundArgument), path.Format);
            reservedFormat |= path.Format is "id" or "data";
            if (path!.Members.Count > 0)
            {
                // A member path is always read at run time, null-safely: the value it projects is not
                // a compile-time constant even when the argument is.
                constant.Append('{').Append(token.Text).Append('}');
                format.Append(path.Format is "id" or "data" ? path.Walk(boundArgument) : path.Apply(boundArgument));
                continue;
            }

            // A constant folds into the name, formatted as the run time would format it. So does a
            // lowered argument made only of literals when there is no format: a LINQ unroll binds its
            // loop variable to a literal, and `$"user-{i}"` becomes "user-1" — each unrolled step then
            // lists under its own name at discovery time, not three "{name}" entries.
            var constValue = model.GetConstantValue(written[index].Expression);
            string? folded = null;
            if (constValue.HasValue && path.FormatConstant(constValue.Value) is var formatted && (formatted is not null || path.Format is null))
            {
                constant.Append(formatted ?? "");
                format.Append(formatted ?? "");
            }
            else if (path.Format is null && TryFoldLiterals(argument, out folded))
            {
                constant.Append(folded!);
                format.Append(folded!);
            }
            else
            {
                constant.Append('{').Append(token.Text).Append('}');
                // Parenthesized so the hole binds tighter than the surrounding `+`, whatever it is.
                format.Append(path.Format is null or "id" or "data" ? ParenthesizedExpression(boundArgument) : path.Apply(boundArgument));
            }
        }

        return new LoweredDisplayName(constant.ToString(), reservedFormat ? null : format.Build(), message.Build());
    }

    /// <summary>Feeds typed runtime values to the same formatting policy as notes and logs.</summary>
    private sealed class Message
    {
        private ExpressionSyntax _builder = ObjectCreationExpression(Names.Global("Raun", "StepMessageBuilder"))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("__formatting")))));
        private readonly StringBuilder _pending = new();
        private bool _anyHole;

        public void Append(string text) => _pending.Append(text);

        public void Append(ExpressionSyntax value, string? format)
        {
            Flush();
            var arguments = new List<ArgumentSyntax> { Argument(value) };
            if (format is not null)
            {
                arguments.Add(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(format)))
                    .WithNameColon(NameColon(IdentifierName("format"))));
            }

            Call("AppendFormatted", arguments);
            _anyHole = true;
        }

        public ExpressionSyntax? Build()
        {
            if (!_anyHole)
            {
                return null;
            }

            Flush();
            Call("Build", []);
            return _builder;
        }

        private void Flush()
        {
            if (_pending.Length == 0)
            {
                return;
            }

            Call("AppendLiteral", [Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(_pending.ToString())))]);
            _pending.Clear();
        }

        private void Call(string name, IEnumerable<ArgumentSyntax> arguments)
            => _builder = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    _builder, IdentifierName(name)))
                .WithArgumentList(ArgumentList(SeparatedList(arguments)));
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
