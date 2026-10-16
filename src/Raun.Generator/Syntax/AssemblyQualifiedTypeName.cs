using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Syntax;

internal static class AssemblyQualifiedTypeName
{
    internal static string From(ITypeSymbol type) => Name(type) + ", " + Assembly(type).Identity;
    private static IAssemblySymbol Assembly(ITypeSymbol type) => type is IArrayTypeSymbol array ? Assembly(array.ElementType) : type.ContainingAssembly;
    private static string Name(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) { return Name(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]"; }
        var named = (INamedTypeSymbol)type;
        var parts = new Stack<INamedTypeSymbol>();
        for (var current = named; current is not null; current = current.ContainingType) { parts.Push(current); }
        var prefix = named.ContainingNamespace.IsGlobalNamespace ? "" : named.ContainingNamespace.ToDisplayString() + ".";
        var name = prefix + string.Join("+", parts.Select(part => part.MetadataName));
        var arguments = parts.SelectMany(part => part.TypeArguments).ToArray();
        return arguments.Length == 0 ? name : name + "[" + string.Join(",", arguments.Select(argument => "[" + From(argument) + "]")) + "]";
    }
}
