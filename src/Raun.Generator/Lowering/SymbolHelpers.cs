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
