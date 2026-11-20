using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>The folded discovery template and the optional title builder, which reads __inputs and
/// __formatting.</summary>
internal readonly record struct LoweredDisplayName(string Template, ExpressionSyntax? TitleExpression, ExpressionSyntax? CompletedTitleExpression);

/// <summary>
/// Builds a step's display name from its <c>[StepName]</c> template: constant placeholders are
/// folded into a literal, and runtime ones — a member path such as <c>{specification.Outcome}</c>
/// always is — feed a message builder. A fully constant name needs no runtime expression.
/// </summary>
internal static class DisplayNameBuilder
{
    /// <summary>The display name for one call: <paramref name="written"/> are its arguments as the
    /// scenario wrote them (for constant folding), <paramref name="lowered"/> the same arguments
    /// lowered for the generated step, index for index (for runtime holes).</summary>
    public static LoweredDisplayName Build(
        SemanticModel model,
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> written,
        IReadOnlyList<ArgumentSyntax> lowered,
        IReadOnlyList<ArgumentSyntax>? originalLowered = null)
    {
        var template = StepTemplateSelector.Select(method, written) ?? method.Name;
        var tokens = TemplateTokenizer.Tokenize(template);

        var constant = new StringBuilder();
        var message = new Message();
        var returns = new List<ReturnHole>();

        foreach (var token in tokens)
        {
            if (!token.IsPlaceholder)
            {
                constant.Append(token.Text);
                message.Append(token.Text);
                continue;
            }

            var index = -1;
            PlaceholderPath? path = null;
            var placeholderText = token.Text;
            var isReturn = false;
            if (PlaceholderPath.TryBind(method, token.Text, out path, out _))
            {
                if (path!.ReturnType is { } resultType)
                {
                    isReturn = true;
                    returns.Add(new ReturnHole(message.MarkReturn(), TypeSyntaxFactory.From(resultType), path.Format, path.Alignment));
                    var unavailable = path.Align("…");
                    var returnAlignment = path.Alignment;
                    placeholderText = path.FallbackText ?? "";
                    path = path.Fallback;
                    if (path is null)
                    {
                        constant.Append(unavailable);
                        message.AppendUnavailableReturn(returnAlignment);
                        continue;
                    }
                }
                var ordinal = method.Parameters.IndexOf(path.Parameter!);
                index = CallArguments.IndexOf(written, path.Parameter!.Name, ordinal);
            }

            var defaultParameter = isReturn && index < 0 && path?.Parameter is { HasExplicitDefaultValue: true } parameter ? parameter : null;
            if (index < 0 && defaultParameter is null)
            {
                // No argument resolves the placeholder: it stays as written, in both forms.
                constant.Append('{').Append(placeholderText).Append('}');
                message.Append("{" + placeholderText + "}");
                continue;
            }

            var argument = defaultParameter is null ? lowered[index].Expression.WithoutTrivia() : DefaultArgument(defaultParameter);
            // Bind the value as the called method does before accessing members or inferring the
            // formatter's generic type. The producer may return a more concrete type than this parameter.
            var parameterType = TypeSyntaxFactory.FromAnnotated(path!.Parameter!.Type);
            var boundArgument = CastExpression(parameterType, ParenthesizedExpression(argument));
            if (path.Members.Count == 0 && AttributeReader.ResourceKeyType(path.Parameter) is not null)
            {
                message.Append(ResourceKeyBinding.Apply(path.Parameter, boundArgument), path.Format, path.Alignment);
                constant.Append('{').Append(placeholderText).Append('}');
                continue;
            }
            message.Append(path.Walk(boundArgument), path.Format, path.Alignment);
            if (path!.Members.Count > 0)
            {
                // A member path is always read at run time, null-safely: the value it projects is not
                // a compile-time constant even when the argument is.
                constant.Append('{').Append(placeholderText).Append('}');
                continue;
            }

            // A constant folds into the name, formatted as the run time would format it. So does a
            // lowered argument made only of literals when there is no format: a LINQ unroll binds its
            // loop variable to a literal, and `$"user-{i}"` becomes "user-1" — each unrolled step then
            // lists under its own name at discovery time, not three "{name}" entries.
            var constValue = defaultParameter is null ? model.GetConstantValue(written[index].Expression) : new Optional<object?>(defaultParameter.ExplicitDefaultValue);
            string? folded = null;
            if (constValue.HasValue && path.FormatConstant(constValue.Value) is var formatted && (formatted is not null || path.Format is null))
            {
                constant.Append(formatted ?? "");
            }
            else if (path.Format is null && TryFoldLiterals(defaultParameter is null ? (originalLowered ?? lowered)[index].Expression : argument, out folded))
            {
                constant.Append(path.Align(folded!));
            }
            else
            {
                constant.Append('{').Append(placeholderText).Append('}');
            }
        }

        var title = message.Build();
        return new LoweredDisplayName(constant.ToString(), title,
            returns.Count == 0 ? null : CompletedTitle(returns, message.PartCount));
    }

    private readonly record struct ReturnHole(int Index, TypeSyntax Type, string? Format, int Alignment);

    private static CastExpressionSyntax DefaultArgument(IParameterSymbol parameter)
    {
        var value = parameter.ExplicitDefaultValue;
        ExpressionSyntax expression = value switch
        {
            null when !parameter.Type.IsValueType => PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression,
                DefaultExpression(TypeSyntaxFactory.From(parameter.Type))),
            null => DefaultExpression(TypeSyntaxFactory.From(parameter.Type)),
            string text => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(text)),
            char character => LiteralExpression(SyntaxKind.CharacterLiteralExpression, Literal(character)),
            bool boolean => LiteralExpression(boolean ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            int number => Literals.Num(number),
            long number when number == long.MinValue => Access(Names.Global("System", "Int64"), "MinValue"),
            long number => SignedNumber(number, Literal(System.Math.Abs(number))),
            float number when float.IsNaN(number) => Access(Names.Global("System", "Single"), "NaN"),
            float number when float.IsPositiveInfinity(number) => Access(Names.Global("System", "Single"), "PositiveInfinity"),
            float number when float.IsNegativeInfinity(number) => Access(Names.Global("System", "Single"), "NegativeInfinity"),
            float number => SignedNumber(number, Literal(System.Math.Abs(number))),
            double number when double.IsNaN(number) => Access(Names.Global("System", "Double"), "NaN"),
            double number when double.IsPositiveInfinity(number) => Access(Names.Global("System", "Double"), "PositiveInfinity"),
            double number when double.IsNegativeInfinity(number) => Access(Names.Global("System", "Double"), "NegativeInfinity"),
            double number => SignedNumber(number, Literal(System.Math.Abs(number))),
            decimal number => SignedNumber(number, Literal(System.Math.Abs(number))),
            uint number => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(number)),
            ulong number => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(number)),
            _ => Literals.Num(System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)),
        };
        return CastExpression(TypeSyntaxFactory.From(parameter.Type), ParenthesizedExpression(expression));
    }

    private static ExpressionSyntax SignedNumber<T>(T value, SyntaxToken magnitude) where T : struct, System.IComparable<T>
    {
        var literal = LiteralExpression(SyntaxKind.NumericLiteralExpression, magnitude);
        return value.CompareTo(default) < 0 ? PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, literal) : literal;
    }

    private static ParenthesizedLambdaExpressionSyntax CompletedTitle(IReadOnlyList<ReturnHole> holes, int partCount)
    {
        var title = IdentifierName("__title");
        var output = IdentifierName("__output");
        var parts = Access(title, "Parts");
        var statements = new List<StatementSyntax>
        {
            IfStatement(BinaryExpression(SyntaxKind.LogicalOrExpression,
                IsPatternExpression(output, ConstantPattern(LiteralExpression(SyntaxKind.NullLiteralExpression))),
                BinaryExpression(SyntaxKind.NotEqualsExpression, Access(parts, "Count"), Literals.Num(partCount))),
                Block(ReturnStatement(title))),
            LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(Identifier("__parts"))
                    .WithInitializer(EqualsValueClause(ObjectCreationExpression(
                            Names.Generic(Names.Global("System", "Collections", "Generic"), "List", Names.Global("Raun", "StepMessagePart")))
                        .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(parts))))))))),
        };
        foreach (var hole in holes)
        {
            var replacement = new Message();
            replacement.Append(CastExpression(hole.Type, output), hole.Format, hole.Alignment);
            statements.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                At(IdentifierName("__parts"), hole.Index), At(Access(replacement.Build()!, "Parts"), 0))));
        }
        statements.Add(ReturnStatement(ObjectCreationExpression(Names.Global("Raun", "StepMessage"))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("__parts")))))));
        return ParenthesizedLambdaExpression().WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(ParameterList(SeparatedList(new[]
            {
                Parameter(Identifier("__title")), Parameter(Identifier("__output")), Parameter(Identifier("__formatting")),
            }))).WithBlock(Block(statements));
    }

    private static MemberAccessExpressionSyntax Access(ExpressionSyntax expression, string name)
        => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expression, IdentifierName(name));

    private static ElementAccessExpressionSyntax At(ExpressionSyntax expression, int index)
        => ElementAccessExpression(expression).WithArgumentList(BracketedArgumentList(SingletonSeparatedList(Argument(Literals.Num(index)))));

    /// <summary>Feeds typed runtime values to the same formatting policy as notes and logs.</summary>
    private sealed class Message
    {
        private ExpressionSyntax _builder = ObjectCreationExpression(Names.Global("Raun", "StepMessageBuilder"))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(IdentifierName("__formatting")))));
        private readonly StringBuilder _pending = new();
        private bool _anyHole;
        public int PartCount { get; private set; }

        public int MarkReturn()
        {
            Flush();
            return PartCount;
        }

        public void AppendUnavailableReturn(int alignment)
        {
            Append(alignment < 0 ? "…".PadRight(-alignment) : "…".PadLeft(alignment));
            Flush();
            _anyHole = true;
        }

        public void Append(string text) => _pending.Append(text);

        public void Append(ExpressionSyntax value, string? format, int alignment = 0)
        {
            Flush();
            var arguments = new List<ArgumentSyntax> { Argument(value) };
            if (format is not null)
            {
                arguments.Add(Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(format)))
                    .WithNameColon(NameColon(IdentifierName("format"))));
            }

            if (alignment != 0) arguments.Add(Argument(Literals.Num(alignment)).WithNameColon(NameColon(IdentifierName("alignment"))));
            Call("AppendFormatted", arguments);
            _anyHole = true;
            PartCount++;
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
            PartCount++;
        }

        private void Call(string name, IEnumerable<ArgumentSyntax> arguments)
            => _builder = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    _builder, IdentifierName(name)))
                .WithArgumentList(ArgumentList(SeparatedList(arguments)));
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

            case ParenthesizedExpressionSyntax when Literals.ValueOfVariable(expression) is { } known:
                return TryFoldLiterals(known, out text);

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
