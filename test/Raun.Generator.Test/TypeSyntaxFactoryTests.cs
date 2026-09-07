using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Raun.Generator.Lowering;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Pins <see cref="TypeSyntaxFactory"/> to Roslyn's own printer: every built type must render
/// exactly as <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> prints the symbol, so replacing
/// <c>ParseTypeName(symbol.ToDisplayString(...))</c> with a builder cannot change emitted text.
/// </summary>
public class TypeSyntaxFactoryTests
{
    /// <summary>
    /// The display format the builder is pinned to. It is <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// plus the nullable-reference modifier: the builder emits <c>string?</c> for an annotated
    /// reference type, where the bare fully-qualified format prints <c>string</c> (it does not set
    /// <see cref="SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier"/>). That is
    /// the one deliberate divergence, and <see cref="Nullable_reference_types_are_the_one_divergence"/>
    /// nails down exactly which cases it covers.
    /// </summary>
    private static readonly SymbolDisplayFormat Expected =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private const string Snippet =
        """
        #nullable enable
        using System.Collections.Generic;

        namespace @class
        {
            public class @event { }
        }

        namespace Demo
        {
            public class Patient { }
            public class Slot { }
            public class Outer { public class Inner { } }

            public class Fields
            {
                public int Int;
                public string String = "";
                public object Object = new();
                public bool Bool;
                public string? NullableString;
                public int? NullableInt;
                public Patient Named;
                public Outer.Inner Nested;
                public List<Patient> Generic;
                public Dictionary<string, List<int>> NestedGeneric;
                public Patient[] Array;
                public int[][] Jagged;
                public int[,] Rectangular;
                public (Patient, Slot) Tuple;
                public (Patient p, Slot s) NamedTuple;
                public List<string?> GenericOfNullable;
                public dynamic Dynamic;
                public global::@class.@event Keyword;
            }

            public class Generics
            {
                public T Echo<T>(T value) => value;
            }
        }
        """;

    /// <summary>Cases whose built form has no whitespace of its own — raw <c>ToString()</c> parity.</summary>
    [Theory]
    [InlineData("Int")]
    [InlineData("String")]
    [InlineData("Object")]
    [InlineData("Bool")]
    [InlineData("NullableString")]
    [InlineData("NullableInt")]
    [InlineData("Named")]
    [InlineData("Nested")]
    [InlineData("Generic")]
    [InlineData("Array")]
    [InlineData("Jagged")]
    [InlineData("Rectangular")]
    [InlineData("Dynamic")]
    [InlineData("Keyword")]
    public void A_built_type_prints_exactly_as_Roslyn_prints_the_symbol(string field)
    {
        var type = FieldType(field);

        Assert.Equal(type.ToDisplayString(Expected), TypeSyntaxFactory.From(type).ToString());
    }

    /// <summary>
    /// Cases where Roslyn's printer puts a space after a separator (<c>,</c>) that the factory does
    /// not emit as trivia. The generated file is always run through <c>NormalizeWhitespace</c>, so
    /// that is the form that has to agree.
    /// </summary>
    [Theory]
    [InlineData("NestedGeneric")]
    [InlineData("Tuple")]
    [InlineData("NamedTuple")]
    [InlineData("GenericOfNullable")]
    public void A_built_type_with_separators_matches_after_normalization(string field)
    {
        var type = FieldType(field);

        Assert.Equal(
            type.ToDisplayString(Expected),
            TypeSyntaxFactory.From(type).NormalizeWhitespace().ToString());
    }

    [Fact]
    public void A_generic_methods_type_parameter_is_built_by_name()
    {
        var compilation = Compile(Snippet);
        var echo = compilation.GetTypeByMetadataName("Demo.Generics")!
            .GetMembers("Echo").OfType<IMethodSymbol>().Single();

        Assert.Equal("T", TypeSyntaxFactory.From(echo.ReturnType).ToString());
        Assert.Equal(echo.ReturnType.ToDisplayString(Expected), TypeSyntaxFactory.From(echo.ReturnType).ToString());
    }

    /// <summary>
    /// The builder deliberately keeps the <c>?</c> that <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// drops for reference types. Nothing else diverges, which this asserts both ways.
    /// </summary>
    [Fact]
    public void Nullable_reference_types_are_the_one_divergence()
    {
        var diverging = new List<string>();
        foreach (var field in AllFields())
        {
            var bare = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (bare != field.Type.ToDisplayString(Expected))
            {
                diverging.Add(field.Name);
            }
        }

        Assert.Equal(["GenericOfNullable", "NullableString"], diverging.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("string?", TypeSyntaxFactory.From(FieldType("NullableString")).ToString());
        Assert.Equal("string", FieldType("NullableString").ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    [Fact]
    public void An_unrepresentable_type_is_refused_rather_than_guessed()
    {
        var compilation = Compile(
            """
            namespace Demo
            {
                public unsafe class Pointers { public int* Pointer; }
            }
            """,
            unsafeCode: true);
        var pointer = compilation.GetTypeByMetadataName("Demo.Pointers")!
            .GetMembers("Pointer").OfType<IFieldSymbol>().Single();

        var error = Assert.Throws<NotSupportedException>(() => TypeSyntaxFactory.From(pointer.Type));
        Assert.Contains("int*", error.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<IFieldSymbol> AllFields()
        => Compile(Snippet).GetTypeByMetadataName("Demo.Fields")!.GetMembers().OfType<IFieldSymbol>();

    private static ITypeSymbol FieldType(string name)
        => AllFields().Single(f => f.Name == name).Type;

    private static CSharpCompilation Compile(string source, bool unsafeCode = false)
    {
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa.Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        return CSharpCompilation.Create(
            "TypeSyntax_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: unsafeCode,
                nullableContextOptions: NullableContextOptions.Enable));
    }
}
