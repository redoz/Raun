using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Emit;

/// <summary>
/// The fixed names the generator spells out itself — runtime types, framework types, the generated
/// namespace. Built as syntax, never parsed: a name is a chain of identifiers, and writing it as one
/// keeps qualification and escaping out of string handling.
/// </summary>
internal static class Names
{
    /// <summary><c>global::a.b.c</c> for the parts <c>a</c>, <c>b</c>, <c>c</c>.</summary>
    public static NameSyntax Global(params string[] parts)
    {
        NameSyntax name = AliasQualifiedName(
            IdentifierName(Token(SyntaxKind.GlobalKeyword)),
            IdentifierName(parts[0]));

        for (var i = 1; i < parts.Length; i++)
        {
            name = QualifiedName(name, IdentifierName(parts[i]));
        }

        return name;
    }

    /// <summary><c>a.b.c</c> — the unrooted form, for namespace declarations and using directives.</summary>
    public static NameSyntax Dotted(params string[] parts)
    {
        NameSyntax name = IdentifierName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            name = QualifiedName(name, IdentifierName(parts[i]));
        }

        return name;
    }

    /// <summary><c>container.name&lt;arguments&gt;</c>.</summary>
    public static NameSyntax Generic(NameSyntax container, string name, params TypeSyntax[] arguments)
        => QualifiedName(
            container,
            GenericName(Identifier(name)).WithTypeArgumentList(TypeArgumentList(SeparatedList(arguments))));
}
