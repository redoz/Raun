using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Syntax;

/// <summary>The literal expressions the generator emits, built once so every caller spells a value
/// the same way.</summary>
internal static class Literals
{
    /// <summary>
    /// An <see cref="int"/> as an expression: the bare numeric literal, or a unary minus over the
    /// magnitude when negative — C# has no negative literal token, and handing
    /// <c>Literal(negative)</c> out on its own is a malformed token that only happens to print right.
    /// </summary>
    public static ExpressionSyntax Num(int value)
        => value >= 0
            ? LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value))
            : PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Magnitude(-(long)value));

    /// <summary>
    /// <paramref name="value"/> as the scenario's variable of <paramref name="type"/> would read:
    /// <c>((type)(object)(value))</c>. The scenario wrote a parameter or a loop variable, and the
    /// generated code holds its value as a literal. A literal is a constant expression and a variable
    /// is not, which the compiler acts on: a constant <c>int</c> converts to <c>short</c> and picks
    /// another overload, <c>100 / 0</c> is an error where <c>100 / count</c> is not, and
    /// <c>count is 1</c> is refused as always true. Passing through <c>object</c> is the one spelling
    /// that keeps the value and its type and is never a constant.
    /// </summary>
    public static ExpressionSyntax AsVariable(TypeSyntax type, ExpressionSyntax value)
        => ParenthesizedExpression(CastExpression(
            type,
            CastExpression(PredefinedType(Token(SyntaxKind.ObjectKeyword)), ParenthesizedExpression(value))));

    /// <summary>The value inside <see cref="AsVariable"/>'s spelling, for a reader that wants the
    /// known value and not the variable; null for any other expression.</summary>
    public static ExpressionSyntax? ValueOfVariable(ExpressionSyntax expression)
        => expression is ParenthesizedExpressionSyntax
        {
            Expression: CastExpressionSyntax
            {
                Expression: CastExpressionSyntax
                {
                    Type: PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.ObjectKeyword },
                    Expression: ParenthesizedExpressionSyntax value,
                },
            },
        }
            ? value.Expression
            : null;

    /// <summary>The absolute value of a negative int, as the token under a unary minus. It is taken
    /// as a <see cref="long"/> because negating <see cref="int.MinValue"/> as an int overflows back
    /// to itself (printing <c>--2147483648</c>), and its text is spelled here because
    /// <c>Literal(long)</c> would append an <c>L</c> suffix and make the operand a long.</summary>
    private static LiteralExpressionSyntax Magnitude(long magnitude)
        => LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            Literal(magnitude.ToString(CultureInfo.InvariantCulture), magnitude));
}
