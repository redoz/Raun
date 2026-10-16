using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>One member of a placeholder path, and whether its receiver can be null — in which case the
/// generated access is <c>?.</c>, so a null along the way renders as empty instead of throwing.</summary>
internal readonly record struct PathMember(string Name, bool NullConditional);

/// <summary>
/// A <c>[StepName]</c> placeholder bound by symbol: a parameter, optionally followed by a path of
/// readable instance members — <c>{specification.Outcome}</c>, <c>{specification.Employee.Name}</c> —
/// and optionally a format after the first colon, for a value that is <c>IFormattable</c>:
/// <c>{booking.When:yyyy-MM-dd}</c>, <c>{booking.When:HH:mm}</c>.
/// The generator and the analyzer both bind through <see cref="TryBind"/>, so a placeholder the
/// analyzer accepts is exactly one the generator renders.
/// </summary>
internal sealed class PlaceholderPath
{
    private PlaceholderPath(IParameterSymbol parameter, IReadOnlyList<PathMember> members, string? format, int alignment)
    {
        Parameter = parameter;
        Members = members;
        Format = format;
        Alignment = alignment;
    }

    private PlaceholderPath(ITypeSymbol returnType, string? format, PlaceholderPath? fallback, string? fallbackText, int alignment)
    {
        ReturnType = returnType;
        Format = format;
        Alignment = alignment;
        Fallback = fallback;
        FallbackText = fallbackText;
        Members = [];
    }

    /// <summary>The parameter the path starts at.</summary>
    public IParameterSymbol? Parameter { get; }

    public ITypeSymbol? ReturnType { get; }
    public PlaceholderPath? Fallback { get; }
    public string? FallbackText { get; }

    /// <summary>The members after the parameter; empty for a bare <c>{parameter}</c>.</summary>
    public IReadOnlyList<PathMember> Members { get; }

    /// <summary>The format after the first colon, or null for none.</summary>
    public string? Format { get; }
    public int Alignment { get; }
    public bool RequiresTypedFormatter { get; private init; }

    /// <summary>
    /// Binds <paramref name="placeholder"/> against <paramref name="method"/>'s parameters. On failure
    /// <paramref name="reason"/> says which segment did not resolve and why.
    /// </summary>
    public static bool TryBind(IMethodSymbol method, string placeholder, out PlaceholderPath? path, out string reason)
    {
        path = null;
        var hole = TemplateInterpolation.ReadHole(placeholder, out var translatedReturn);
        if (hole is null)
        {
            reason = "the placeholder must use C# interpolation syntax";
            return false;
        }
        var format = hole.FormatClause?.FormatStringToken.ValueText;
        var alignment = 0;
        if (hole.AlignmentClause is { } clause && !TryAlignment(clause.Value, out alignment))
        {
            reason = "alignment must be a signed integer literal";
            return false;
        }
        if (!translatedReturn) return TryBindParameter(method, hole.Expression, format, alignment, out path, out reason);

        var primary = hole.Expression;
        ExpressionSyntax? fallbackExpression = null;
        if (primary is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce)
        {
            primary = coalesce.Left;
            fallbackExpression = coalesce.Right;
        }
        if (primary is not IdentifierNameSyntax { Identifier.ValueText: "return" })
        {
            reason = "a return placeholder starts at '$return' without a member path";
            return false;
        }
        if (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType) || resultType is null)
        {
            reason = "'$return' requires a Task<T> or ValueTask<T> result";
            return false;
        }
        PlaceholderPath? fallback = null;
        string? fallbackText = null;
        if (fallbackExpression is not null)
        {
            if (!TryBindParameter(method, fallbackExpression, format, alignment, out fallback, out reason)) return false;
            if (fallback!.Parameter!.IsParams)
            {
                reason = "a return placeholder fallback cannot start at a params parameter";
                return false;
            }
            fallbackText = fallbackExpression.ToString()
                + (alignment == 0 ? "" : "," + alignment.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + (format is null ? "" : ":" + format);
        }
        path = new PlaceholderPath(resultType, format, fallback, fallbackText, alignment);
        reason = "";
        return true;
    }

    private static bool TryBindParameter(IMethodSymbol method, ExpressionSyntax expression, string? format, int alignment, out PlaceholderPath? path, out string reason)
    {
        path = null;
        var segments = new List<IdentifierNameSyntax>();
        while (expression is MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Name: IdentifierNameSyntax memberName } access)
        {
            segments.Add(memberName);
            expression = access.Expression;
        }
        if (expression is not IdentifierNameSyntax parameterName)
        {
            reason = InvalidPathReason(expression.ToString());
            return false;
        }
        segments.Add(parameterName);
        segments.Reverse();

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == segments[0].Identifier.ValueText);
        if (parameter is null)
        {
            reason = "no parameter is named '" + segments[0].Identifier.ValueText + "'";
            return false;
        }

        var members = new List<PathMember>();
        var type = parameter.Type;
        for (var i = 1; i < segments.Count; i++)
        {
            var name = segments[i].Identifier.ValueText;

            var receiver = UnderlyingIfNullable(type);
            if (Member(receiver, name) is not { } member)
            {
                reason = "'" + receiver.Name + "' has no readable instance member '" + name + "'";
                return false;
            }

            if (!IsReadableFromGeneratedCode(member))
            {
                reason = "'" + name + "' is not readable from generated code; make it public or internal";
                return false;
            }

            members.Add(new PathMember(segments[i].Identifier.Text, CanBeNull(type)));
            type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => type,
            };
        }

        path = new PlaceholderPath(parameter, members, format, alignment)
        {
            RequiresTypedFormatter = format is not null && !IsFormattable(UnderlyingIfNullable(type)),
        };
        reason = "";
        return true;
    }

    private static bool TryAlignment(ExpressionSyntax expression, out int alignment)
    {
        var sign = 1;
        if (expression is PrefixUnaryExpressionSyntax unary && unary.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression)
        {
            sign = unary.IsKind(SyntaxKind.UnaryMinusExpression) ? -1 : 1;
            expression = unary.Operand;
        }
        alignment = expression is LiteralExpressionSyntax { Token.Value: int number } ? number * sign : 0;
        return expression is LiteralExpressionSyntax { Token.Value: int };
    }

    // Preserve the existing diagnostic's offending member text, even when C# rejects the path.
    private static string InvalidPathReason(string placeholder)
    {
        var colon = placeholder.IndexOf(':');
        var root = colon < 0 ? placeholder : placeholder.Substring(0, colon);
        var member = root.Substring(root.LastIndexOf('.') + 1).Trim();
        return "'" + member + "' is not a member name; a placeholder path is parameter.Member.Member";
    }

    /// <summary>
    /// <paramref name="argument"/> followed by the path — <c>(argument)?.Employee?.Name</c>, with
    /// <c>.</c> wherever the receiver is a non-nullable value type — and, with a format,
    /// <c>((IFormattable?)(…))?.ToString("format", CultureInfo.InvariantCulture)</c>: the invariant
    /// culture, so a step's name is the same on every machine.
    /// </summary>
    public ExpressionSyntax Apply(ExpressionSyntax argument)
    {
        var value = Walk(argument);
        if (Format is null)
        {
            return value;
        }

        var formattable = ParenthesizedExpression(CastExpression(
            NullableType(QualifiedName(AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName("System")), IdentifierName("IFormattable"))),
            ParenthesizedExpression(value)));
        return ConditionalAccessExpression(
            formattable,
            InvocationExpression(MemberBindingExpression(IdentifierName("ToString")))
                .WithArgumentList(ArgumentList(SeparatedList(new[]
                {
                    Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(Format))),
                    Argument(InvariantCulture),
                }))));
    }

    /// <summary>Formats a compile-time constant exactly as <see cref="Apply"/> formats it at run time,
    /// or returns null when the constant cannot take the format.</summary>
    public string? FormatConstant(object? value)
    {
        try
        {
            return Align(value is System.IFormattable formattable
                ? formattable.ToString(Format, System.Globalization.CultureInfo.InvariantCulture) ?? ""
                : value?.ToString() ?? "");
        }
        catch (System.FormatException) { return null; }
    }

    public string Align(string text) => Alignment < 0 ? text.PadRight(-Alignment) : text.PadLeft(Alignment);

    private static MemberAccessExpressionSyntax InvariantCulture
        => MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            QualifiedName(
                QualifiedName(AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), IdentifierName("System")), IdentifierName("Globalization")),
                IdentifierName("CultureInfo")),
            IdentifierName("InvariantCulture"));

    public ExpressionSyntax Walk(ExpressionSyntax argument)
    {
        ExpressionSyntax head = ParenthesizedExpression(argument);
        var first = 0;
        while (first < Members.Count && !Members[first].NullConditional)
        {
            head = Access(head, Members[first++].Name);
        }

        return first == Members.Count ? head : ConditionalAccessExpression(head, Tail(first));
    }

    /// <summary>The <c>whenNotNull</c> part of a conditional access starting at member
    /// <paramref name="start"/>: its binding, the plain accesses after it, and the next conditional.</summary>
    private ExpressionSyntax Tail(int start)
    {
        ExpressionSyntax part = MemberBindingExpression(IdentifierName(Members[start].Name));
        var next = start + 1;
        while (next < Members.Count && !Members[next].NullConditional)
        {
            part = Access(part, Members[next++].Name);
        }

        return next == Members.Count ? part : ConditionalAccessExpression(part, Tail(next));
    }

    private static MemberAccessExpressionSyntax Access(ExpressionSyntax receiver, string name)
        => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, IdentifierName(name));

    private static bool IsFormattable(ITypeSymbol type)
        => type.AllInterfaces.Any(i => i.Name == "IFormattable" && i.ContainingNamespace?.ToDisplayString() == "System")
            || (type.Name == "IFormattable" && type.ContainingNamespace?.ToDisplayString() == "System");
    private static bool CanBeNull(ITypeSymbol type)
        => !type.IsValueType || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static ITypeSymbol UnderlyingIfNullable(ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>A readable instance property (not an indexer) or field named <paramref name="name"/>,
    /// declared on the type, a base type, or — for an interface — an inherited interface.</summary>
    private static ISymbol? Member(ITypeSymbol type, string name)
    {
        var types = new List<ITypeSymbol>();
        for (var t = type; t is not null; t = t.BaseType)
        {
            types.Add(t);
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            types.AddRange(type.AllInterfaces);
        }

        return types
            .SelectMany(t => t.GetMembers(name))
            .FirstOrDefault(m => m switch
            {
                IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: not null } => true,
                IFieldSymbol { IsStatic: false } => true,
                _ => false,
            });
    }

    /// <summary>The generated file lives in the same assembly, outside the member's type: a public or
    /// internal member (and getter) is readable there, a private or protected one is not.</summary>
    private static bool IsReadableFromGeneratedCode(ISymbol member)
    {
        static bool Visible(Accessibility accessibility)
            => accessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal;

        return Visible(member.DeclaredAccessibility)
            && (member is not IPropertySymbol { GetMethod: { } getter } || Visible(getter.DeclaredAccessibility));
    }
}
