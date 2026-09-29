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

    public const string ScenarioContextFullName = "Raun.ScenarioContext";

    /// <summary>Returns the receiver type's name if it implements <c>Raun.IPhase</c> (the built-in
    /// Given/When/Then markers do; so does any user-defined marker), else null.</summary>
    public static string? PhaseOf(ExpressionSyntax receiver, SemanticModel model)
    {
        if (model.GetSymbolInfo(receiver).Symbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var isPhase = type.AllInterfaces.Any(i =>
            i.Name == "IPhase"
            && i.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun");

        return isPhase ? type.Name : null;
    }

    /// <summary>The world of a scenario class: <c>TWorld</c> when <paramref name="type"/> derives from
    /// <c>Raun.Scenarios&lt;TWorld&gt;</c>, else null.</summary>
    public static ITypeSymbol? WorldOfScenarios(ITypeSymbol? type) => GenericBaseArgument(type, "Scenarios");

    /// <summary>The world of a step class: <c>TWorld</c> when <paramref name="type"/> derives from
    /// <c>Raun.Phase&lt;TWorld&gt;</c>, else null.</summary>
    public static ITypeSymbol? WorldOfStepClass(ITypeSymbol? type) => GenericBaseArgument(type, "Phase");

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

    /// <summary>True for Raun's own <c>Steps&lt;T&gt;()</c>, on <c>Phase&lt;&gt;</c> or <c>Scenarios&lt;&gt;</c>.</summary>
    public static bool IsStepsAccessor(IMethodSymbol method)
        => method is { Name: "Steps", Arity: 1, Parameters.Length: 0 }
            && method.ContainingType.OriginalDefinition is { Arity: 1, Name: "Phase" or "Scenarios" } owner
            && owner.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun";

    private static ITypeSymbol? GenericBaseArgument(ITypeSymbol? type, string name)
    {
        for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
        {
            if (t.OriginalDefinition is { Arity: 1 } definition
                && definition.Name == name
                && definition.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun")
            {
                return t.TypeArguments[0];
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

    /// <summary>Whether the method has a trailing <c>ScenarioContext</c> parameter not supplied by source args.</summary>
    public static bool WantsContext(IMethodSymbol method, int suppliedArgCount)
    {
        if (method.Parameters.Length == 0)
        {
            return false;
        }

        var last = method.Parameters[method.Parameters.Length - 1];
        return last.Type.ToDisplayString(NoGlobal) == ScenarioContextFullName
            && suppliedArgCount == method.Parameters.Length - 1;
    }

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
