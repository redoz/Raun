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

    /// <summary>The absolute value of a negative int, as the token under a unary minus. It is taken
    /// as a <see cref="long"/> because negating <see cref="int.MinValue"/> as an int overflows back
    /// to itself (printing <c>--2147483648</c>), and its text is spelled here because
    /// <c>Literal(long)</c> would append an <c>L</c> suffix the old emitted form never had.</summary>
    private static LiteralExpressionSyntax Magnitude(long magnitude)
        => LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            Literal(magnitude.ToString(CultureInfo.InvariantCulture), magnitude));
}
