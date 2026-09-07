using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>
/// Builds the <see cref="TypeSyntax"/> for a symbol the way
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> would print it — <c>global::</c>-rooted,
/// special types as keywords, keyword identifiers escaped — without ever going through text.
/// </summary>
/// <remarks>
/// Nullable ANNOTATIONS on reference types are dropped, exactly as that format drops them (it does
/// not set <see cref="SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier"/>).
/// The annotation carries no runtime meaning, and the generated file is <c>#nullable enable</c>:
/// emitting <c>string?</c> would let maybe-null state flow into code the consumer compiles, which
/// can warn — and warn as an error — in a build the consumer never changed. Nullable VALUE types
/// (<c>System.Nullable&lt;T&gt;</c>) print as <c>T?</c> in both.
/// </remarks>
internal static class TypeSyntaxFactory
{
    /// <summary>The C# keyword for each special type that has one, as <c>UseSpecialTypes</c> prints it.</summary>
    private static readonly Dictionary<SpecialType, SyntaxKind> Keywords = new()
    {
        [SpecialType.System_Object] = SyntaxKind.ObjectKeyword,
        [SpecialType.System_Void] = SyntaxKind.VoidKeyword,
        [SpecialType.System_Boolean] = SyntaxKind.BoolKeyword,
        [SpecialType.System_Char] = SyntaxKind.CharKeyword,
        [SpecialType.System_SByte] = SyntaxKind.SByteKeyword,
        [SpecialType.System_Byte] = SyntaxKind.ByteKeyword,
        [SpecialType.System_Int16] = SyntaxKind.ShortKeyword,
        [SpecialType.System_UInt16] = SyntaxKind.UShortKeyword,
        [SpecialType.System_Int32] = SyntaxKind.IntKeyword,
        [SpecialType.System_UInt32] = SyntaxKind.UIntKeyword,
        [SpecialType.System_Int64] = SyntaxKind.LongKeyword,
        [SpecialType.System_UInt64] = SyntaxKind.ULongKeyword,
        [SpecialType.System_Decimal] = SyntaxKind.DecimalKeyword,
        [SpecialType.System_Single] = SyntaxKind.FloatKeyword,
        [SpecialType.System_Double] = SyntaxKind.DoubleKeyword,
        [SpecialType.System_String] = SyntaxKind.StringKeyword,
    };

    /// <summary>The fully-qualified syntax for <paramref name="type"/>.</summary>
    /// <exception cref="NotSupportedException">The type has no representable form here (pointers,
    /// function pointers); the generator reports it as RAUN000 rather than emitting nonsense.</exception>
    public static TypeSyntax From(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => FromArray(array),
            IDynamicTypeSymbol => IdentifierName("dynamic"),
            ITypeParameterSymbol parameter => IdentifierName(Name(parameter.Name)),
            INamedTypeSymbol named => FromNamed(named),
            _ => throw new NotSupportedException(
                "cannot build syntax for " + type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    + " (" + type.Kind + ")"),
        };

    private static TypeSyntax FromNamed(INamedTypeSymbol named)
    {
        if (Keywords.TryGetValue(named.SpecialType, out var keyword))
        {
            return PredefinedType(Token(keyword));
        }

        // int? / Patient? — Nullable<T> is spelled with the suffix, never by name.
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
        {
            return NullableType(From(named.TypeArguments[0]));
        }

        if (named.IsTupleType)
        {
            return FromTuple(named);
        }

        return QualifiedFor(named);
    }

    /// <summary>(A, B) / (A first, B second) — element names only when the source spelled them.</summary>
    private static TupleTypeSyntax FromTuple(INamedTypeSymbol tuple)
    {
        var elements = tuple.TupleElements.Select(element =>
        {
            var declared = TupleElement(From(element.Type));
            return element.IsExplicitlyNamedTupleElement
                ? declared.WithIdentifier(Name(element.Name))
                : declared;
        });

        return TupleType(SeparatedList(elements));
    }

    /// <summary>T[], T[][], T[,] — every rank of a nested array collapses onto one element type.</summary>
    private static ArrayTypeSyntax FromArray(IArrayTypeSymbol array)
    {
        var ranks = new List<ArrayRankSpecifierSyntax> { RankOf(array) };
        var element = array.ElementType;
        while (element is IArrayTypeSymbol nested)
        {
            ranks.Add(RankOf(nested));
            element = nested.ElementType;
        }

        return ArrayType(From(element)).WithRankSpecifiers(List(ranks));
    }

    private static ArrayRankSpecifierSyntax RankOf(IArrayTypeSymbol array)
        => ArrayRankSpecifier(SeparatedList<ExpressionSyntax>(
            Enumerable.Repeat((ExpressionSyntax)OmittedArraySizeExpression(), array.Rank)));

    /// <summary>global::Ns.Outer&lt;T&gt;.Inner — containing types, then the namespace chain.</summary>
    private static NameSyntax QualifiedFor(INamedTypeSymbol named)
    {
        var simple = SimpleFor(named);
        return named.ContainingType is { } container
            ? QualifiedName(QualifiedFor(container), simple)
            : Rooted(named.ContainingNamespace, simple);
    }

    private static SimpleNameSyntax SimpleFor(INamedTypeSymbol named)
        => named.TypeArguments.Length == 0
            ? IdentifierName(Name(named.Name))
            : GenericName(Name(named.Name))
                .WithTypeArgumentList(TypeArgumentList(SeparatedList(named.TypeArguments.Select(From))));

    /// <summary>Prefixes <paramref name="simple"/> with <c>global::</c> and the namespace chain.</summary>
    private static NameSyntax Rooted(INamespaceSymbol? containing, SimpleNameSyntax simple)
    {
        var namespaces = new List<string>();
        for (var ns = containing; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            namespaces.Insert(0, ns.Name);
        }

        if (namespaces.Count == 0)
        {
            return AliasQualifiedName(GlobalAlias, simple);
        }

        NameSyntax left = AliasQualifiedName(GlobalAlias, IdentifierName(Name(namespaces[0])));
        for (var i = 1; i < namespaces.Count; i++)
        {
            left = QualifiedName(left, IdentifierName(Name(namespaces[i])));
        }

        return QualifiedName(left, simple);
    }

    private static IdentifierNameSyntax GlobalAlias => IdentifierName(Token(SyntaxKind.GlobalKeyword));

    /// <summary>An identifier token, verbatim (<c>@class</c>) when the name is a reserved keyword.</summary>
    private static SyntaxToken Name(string name)
        => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None
            ? Identifier(name)
            : VerbatimIdentifier(TriviaList(), name, name, TriviaList());
}
