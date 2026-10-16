using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Lowering;

/// <summary>Reads Raun's <c>[Scenario]</c> / <c>[StepName]</c> attribute data off method symbols.</summary>
internal static class AttributeReader
{
    /// <summary>The reserved lineage-target token meaning the step's own return value, used in a
    /// producer's <c>References</c>/<c>Consumes</c>. MUST stay identical to <c>Raun.Subject.Return</c>
    /// (separate assembly).</summary>
    public const string ReturnSubject = "<return>";

    /// <summary>
    /// The lineage targets declared on a producer's <c>[Created]</c>/<c>[Loaded]</c>/<c>[Edited]</c>
    /// attribute — its <c>References</c>/<c>Consumes</c> named properties. Each target is a parameter
    /// name (via <c>nameof</c>) or <see cref="ReturnSubject"/>. Empty arrays when absent.
    /// </summary>
    public static (ImmutableArray<string> References, ImmutableArray<string> Consumes) ProducerLineage(
        ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (ResourceRoleMetadata.BaseVerb(attr.AttributeClass) is "Create" or "Load" or "Edit")
            {
                return (NamedStringArray(attr, "References"), NamedStringArray(attr, "Consumes"));
            }
        }

        return (ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
    }

    private static ImmutableArray<string> NamedStringArray(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == name && named.Value.Kind == TypedConstantKind.Array)
            {
                return named.Value.Values
                    .Select(v => v.Value as string)
                    .Where(s => s is not null)
                    .ToImmutableArray()!;
            }
        }

        return ImmutableArray<string>.Empty;
    }

    public static string? ScenarioDisplayName(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ScenarioAttribute");
        if (attr is { ConstructorArguments.Length: > 0 } && attr.ConstructorArguments[0].Value is string name)
        {
            return name;
        }

        return null;
    }

    /// <summary>The scenario's teardown policy as the underlying <c>Raun.Run</c> value, defaulting
    /// to 0 (<c>Run.Always</c>) when <c>[Teardown]</c> is absent.</summary>
    public static int TeardownPolicy(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "TeardownAttribute");
        if (attr is { ConstructorArguments.Length: > 0 } && attr.ConstructorArguments[0].Value is int value)
        {
            return value;
        }

        return 0;
    }

    public static int ScenarioTimeout(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ScenarioAttribute");
        return TimeoutMs(attr, "Timeout"); // FactAttribute.Timeout (ms)
    }

    public static int StepTimeout(IMethodSymbol method)
        => TimeoutMs(
            method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "StepNameAttribute"),
            "TimeoutMs");

    public static IEnumerable<string> StepTemplates(IMethodSymbol method)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "StepNameAttribute"
                && attribute is { ConstructorArguments.Length: > 0 }
                && attribute.ConstructorArguments[0].Value is string template)
            {
                yield return template;
            }
        }
    }

    public static string? ClassDisplayName(INamedTypeSymbol type)
    {
        var attr = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "DisplayNameAttribute");
        if (attr is { ConstructorArguments.Length: > 0 } && attr.ConstructorArguments[0].Value is string name)
        {
            return name;
        }

        return null;
    }

    /// <summary>The resource type represented by an explicitly annotated key parameter.</summary>
    public static ITypeSymbol? ResourceKeyType(IParameterSymbol parameter)
    {
        foreach (var attribute in parameter.GetAttributes())
        {
            if (attribute.AttributeClass is { Name: "ResourceKeyAttribute", Arity: 1 } type
                && type.ContainingNamespace.ToDisplayString() == "Raun")
            {
                return type.TypeArguments[0];
            }
        }
        return null;
    }

    /// <summary>
    /// The resource role declared on <paramref name="parameter"/> (<c>[Read]/[Edited]/[Deleted]</c>),
    /// as the runtime <c>ResourceContext</c> verb name, or null when none.
    /// </summary>
    public static ResourceRoleMetadata? ParameterAction(IParameterSymbol parameter)
        => ResourceRoleMetadata.From(RoleAttribute(parameter.GetAttributes()), out _);

    public static ResourceRoleMetadata? ReturnAction(IMethodSymbol method)
        => ResourceRoleMetadata.From(ReturnAttribute(method), out _);

    public static string? ParameterRole(IParameterSymbol parameter) => ParameterAction(parameter)?.Verb;
    public static string? ReturnRole(IMethodSymbol method) => ReturnAction(method)?.Verb;

    public static AttributeData? ReturnAttribute(IMethodSymbol method)
        => RoleAttribute(method.GetReturnTypeAttributes()) ?? RoleAttribute(method.GetAttributes());

    private static AttributeData? RoleAttribute(ImmutableArray<AttributeData> attributes)
        => attributes.FirstOrDefault(a => ResourceRoleMetadata.BaseVerb(a.AttributeClass) is not null);

    public static (ImmutableArray<string> References, ImmutableArray<string> Consumes) ReturnLineage(IMethodSymbol method)
        => ReturnAttribute(method) is { } attribute
            ? ProducerLineage(ImmutableArray.Create(attribute))
            : (ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

    /// <summary>Numeric value of <c>Raun.LockMode.Exclusive</c>. MUST stay identical to the runtime enum (separate assembly).</summary>
    private const int LockModeExclusive = 1;

    /// <summary>
    /// Every <c>[Uses&lt;T&gt;]</c> among <paramref name="attributes"/>: the token type and the mode
    /// name, <c>Shared</c> unless the attribute was given <c>LockMode.Exclusive</c>.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Resource, string Mode)> Uses(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass is { Name: "UsesAttribute", IsGenericType: true, TypeArguments.Length: 1 } cls
                && cls.TypeArguments[0] is INamedTypeSymbol resource)
            {
                var exclusive = attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is int mode
                    && mode == LockModeExclusive;
                yield return (resource, exclusive ? "Exclusive" : "Shared");
            }
        }
    }

    private static int TimeoutMs(AttributeData? attr, string namedArgument)
    {
        if (attr is null)
        {
            return 0;
        }

        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == namedArgument && named.Value.Value is int ms)
            {
                return ms;
            }
        }

        return 0;
    }
}
