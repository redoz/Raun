namespace Raun.Model;

/// <summary>
/// Where a scenario's namespace and declaring type come from. The generator records
/// <see cref="ScenarioDefinition.Namespace"/> and <see cref="ScenarioDefinition.TypeName"/>; a
/// definition produced before those members existed carries only <see cref="ScenarioDefinition.MethodName"/>,
/// whose dotted split is the fallback. One derivation, in the runtime, so every adapter's grouping
/// and every filter path agree.
/// </summary>
public static class ScenarioIdentity
{
    /// <summary>The namespace and declaring type of <paramref name="definition"/>: the recorded
    /// members when present, otherwise <see cref="Split"/> of the method name.</summary>
    public static void Resolve(ScenarioDefinition definition, out string @namespace, out string typeName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.TypeName.Length > 0)
        {
            @namespace = definition.Namespace;
            typeName = definition.TypeName;
            return;
        }

        Split(definition.MethodName, out @namespace, out typeName, out _);
    }

    /// <summary>
    /// Splits a <c>{namespace}.{type}.{method}</c> name into its parts. The namespace (and, for a
    /// bare name, the type) come back empty rather than throwing. Nested types fold the outer type
    /// into the namespace segment — which is why the generator records the parts separately.
    /// </summary>
    public static void Split(string methodFullName, out string @namespace, out string typeName, out string methodName)
    {
        ArgumentNullException.ThrowIfNull(methodFullName);

        var lastDot = methodFullName.LastIndexOf('.');
        if (lastDot < 0)
        {
            @namespace = string.Empty;
            typeName = string.Empty;
            methodName = methodFullName;
            return;
        }

        methodName = methodFullName[(lastDot + 1)..];
        var declaringType = methodFullName[..lastDot];

        var typeDot = declaringType.LastIndexOf('.');
        if (typeDot < 0)
        {
            @namespace = string.Empty;
            typeName = declaringType;
        }
        else
        {
            @namespace = declaringType[..typeDot];
            typeName = declaringType[(typeDot + 1)..];
        }
    }
}
