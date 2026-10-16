using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>Shared symbol/syntax recognition for the supported scenario subset.</summary>
internal static class SymbolHelpers
{
    public static readonly SymbolDisplayFormat NoGlobal =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>The world of a scenario class: <c>TWorld</c> when <paramref name="type"/> derives from
    /// <c>Raun.Scenarios&lt;TWorld&gt;</c>, <c>NoWorld</c> for non-generic <c>Scenarios</c>, else null.</summary>
    public static ITypeSymbol? WorldOfScenarios(ITypeSymbol? type) => WorldOfBase(type, "Scenarios");

    /// <summary>The world of a step class: <c>TWorld</c> when <paramref name="type"/> derives from
    /// <c>Raun.Phase&lt;TWorld&gt;</c>, <c>NoWorld</c> for non-generic <c>Phase</c>, else null.</summary>
    public static ITypeSymbol? WorldOfStepClass(ITypeSymbol? type) => WorldOfBase(type, "Phase");

    /// <summary>The phase label of a step class: the name in the nearest <c>[Raun.PhaseName]</c> of its
    /// base chain, the built-in <c>Given&lt;&gt;</c>/<c>When&lt;&gt;</c>/<c>Then&lt;&gt;</c> included.</summary>
    public static string? PhaseNameOf(ITypeSymbol? type)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            foreach (var attribute in t.GetAttributes())
            {
                if (attribute.AttributeClass is { Name: "PhaseNameAttribute" } attributeClass
                    && attributeClass.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string name
                    && name.Length > 0)
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>True for Raun's own <c>Steps&lt;T&gt;()</c>, on a generic or non-generic phase or suite.</summary>
    public static bool IsStepsAccessor(IMethodSymbol method)
        => method is { Name: "Steps", Arity: 1, Parameters.Length: 0 }
            && method.ContainingType.OriginalDefinition is { Arity: 0 or 1, Name: "Phase" or "Scenarios" } owner
            && owner.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun";

    /// <summary>A phase without a generic world binding can run in any scenario's scope.
    /// Explicit <c>Phase&lt;NoWorld&gt;</c> remains bound to NoWorld.</summary>
    public static bool IsWorldIndependentStepClass(ITypeSymbol? type)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition is { Arity: 0 or 1, Name: "Phase" } definition
                && definition.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun")
            {
                return definition.Arity == 0;
            }
        }

        return false;
    }

    private static ITypeSymbol? WorldOfBase(ITypeSymbol? type, string name)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition is { Arity: 0 or 1 } definition
                && definition.Name == name
                && definition.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun")
            {
                // A generic binding occurs before the non-generic base in the chain.
                // Resolve NoWorld in Raun's assembly, not in the consumer's namespace.
                return definition.Arity == 1
                    ? t.TypeArguments[0]
                    : definition.ContainingAssembly.GetTypeByMetadataName("Raun.NoWorld");
            }
        }

        return null;
    }

    /// <summary>Unwraps Task/ValueTask return types; out result type is null when there is none.</summary>
    public static bool TryUnwrapReturn(ITypeSymbol returnType, out ITypeSymbol? resultType)
    {
        resultType = null;
        if (returnType is not INamedTypeSymbol named)
        {
            return false;
        }

        if (named.ContainingNamespace?.ToDisplayString(NoGlobal) != "System.Threading.Tasks")
        {
            return false;
        }

        if (named.Name is not ("Task" or "ValueTask"))
        {
            return false;
        }

        if (named.Arity == 1)
        {
            resultType = named.TypeArguments[0];
        }

        return true;
    }

    /// <summary>True for a non-generic Task/ValueTask (a scenario's required return shape).</summary>
    public static bool IsVoidTaskLike(ITypeSymbol type)
        => type is INamedTypeSymbol named
            && named.ContainingNamespace?.ToDisplayString(NoGlobal) == "System.Threading.Tasks"
            && named.Name is "Task" or "ValueTask"
            && named.Arity == 0;

    /// <summary>
    /// True when <paramref name="type"/> can drive a C# <c>if</c>: it is <c>bool</c>, defines
    /// <c>operator true</c>, or has an implicit conversion to <c>bool</c>. <c>bool?</c> is correctly
    /// rejected — C# rejects it too.
    /// </summary>
    public static bool IsUsableAsCondition(ITypeSymbol type, Compilation compilation)
    {
        if (type.SpecialType == SpecialType.System_Boolean || type.GetMembers("op_True").Any())
        {
            return true;
        }

        var conversion = compilation.ClassifyConversion(type, compilation.GetSpecialType(SpecialType.System_Boolean));
        return conversion.IsImplicit && conversion.IsUserDefined;
    }
}
