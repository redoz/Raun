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
    private PlaceholderPath(IParameterSymbol parameter, IReadOnlyList<PathMember> members, string? format)
    {
        Parameter = parameter;
        Members = members;
        Format = format;
    }

    private PlaceholderPath(ITypeSymbol returnType, string? format, PlaceholderPath? fallback, string? fallbackText)
    {
        ReturnType = returnType;
        Format = format;
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

    /// <summary>
    /// Binds <paramref name="placeholder"/> against <paramref name="method"/>'s parameters. On failure
    /// <paramref name="reason"/> says which segment did not resolve and why.
    /// </summary>
    public static bool TryBind(IMethodSymbol method, string placeholder, out PlaceholderPath? path, out string reason)
    {
        if (!placeholder.StartsWith("$return", System.StringComparison.Ordinal))
        {
            return TryBindParameter(method, placeholder, out path, out reason);
        }

        path = null;
        var coalesce = placeholder.IndexOf("??", System.StringComparison.Ordinal);
        var primary = coalesce < 0 ? placeholder : placeholder.Substring(0, coalesce).TrimEnd();
        var fallbackText = coalesce < 0 ? null : placeholder.Substring(coalesce + 2).Trim();
        if (!TrySplitFormat(primary, out var root, out var format, out reason))
        {
            return false;
        }
        if (root != "$return")
        {
            reason = "a return placeholder starts at '$return' without a member path";
            return false;
        }
        if (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType) || resultType is null)
        {
            reason = "'$return' requires a Task<T> or ValueTask<T> result";
            return false;
        }
        if (!ValidateFormat(resultType, format, out reason))
        {
            return false;
        }
        PlaceholderPath? fallback = null;
        if (fallbackText is not null)
        {
            if (fallbackText.IndexOf("??", System.StringComparison.Ordinal) >= 0)
            {
                reason = "a return placeholder accepts only one parameter-path fallback";
                return false;
            }
            if (!TryBindParameter(method, fallbackText, out fallback, out reason))
            {
                return false;
            }
            // Params packing is outside return-title binding; all invocation forms share this rule.
            // Ordinary parameter placeholders continue through their existing binder unchanged.
            if (fallback!.Parameter!.IsParams)
            {
                reason = "a return placeholder fallback cannot start at a params parameter";
                return false;
            }
        }
        path = new PlaceholderPath(resultType, format, fallback, fallbackText);
        return true;
    }

    private static bool TryBindParameter(IMethodSymbol method, string placeholder, out PlaceholderPath? path, out string reason)
    {
        path = null;
        if (!TrySplitFormat(placeholder, out placeholder, out var format, out reason))
        {
            return false;
        }

        var segments = placeholder.Split('.');

        var parameter = method.Parameters.FirstOrDefault(p => p.Name == segments[0]);
        if (parameter is null)
        {
            reason = "no parameter is named '" + segments[0] + "'";
            return false;
        }

        var members = new List<PathMember>();
        var type = parameter.Type;
        for (var i = 1; i < segments.Length; i++)
        {
            var name = segments[i].Trim();
            if (!SyntaxFacts.IsValidIdentifier(name))
            {
                reason = "'" + name + "' is not a member name; a placeholder path is parameter.Member.Member";
                return false;
            }

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

            members.Add(new PathMember(name, CanBeNull(type)));
            type = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => type,
            };
        }

        if (!ValidateFormat(type, format, out reason))
        {
            return false;
        }

        path = new PlaceholderPath(parameter, members, format);
        reason = "";
        return true;
    }

    private static bool TrySplitFormat(string placeholder, out string root, out string? format, out string reason)
    {
        var colon = placeholder.IndexOf(':');
        root = colon < 0 ? placeholder : placeholder.Substring(0, colon).TrimEnd();
        format = colon < 0 ? null : placeholder.Substring(colon + 1);
        reason = format is { Length: 0 } ? "the format after ':' is empty" : "";
        return reason.Length == 0;
    }

    private static bool ValidateFormat(ITypeSymbol type, string? format, out string reason)
    {
        if (format is not null && format is not ("id" or "data") && !IsFormattable(UnderlyingIfNullable(type)))
        {
            reason = "'" + UnderlyingIfNullable(type).Name + "' does not implement IFormattable, so it takes no format";
            return false;
        }
        reason = "";
        return true;
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
        => Format is null
            ? value?.ToString()
            : value is System.IFormattable formattable
                ? formattable.ToString(Format, System.Globalization.CultureInfo.InvariantCulture)
                : null;

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
