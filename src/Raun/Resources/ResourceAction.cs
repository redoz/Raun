using System.Runtime.Loader;

namespace Raun;

/// <summary>A resource action name and its access effect.</summary>
public sealed class ResourceAction
{
    internal ResourceAction(LifecycleVerb effect, string name, Type? attributeType)
    { Effect = effect; Name = name; AttributeType = attributeType; }

    /// <summary>The base effect used for conflict checks.</summary>
    public LifecycleVerb Effect { get; }
    /// <summary>The action name shown in logs and reports.</summary>
    public string Name { get; }
    /// <summary>The custom attribute type, or null for a built-in action.</summary>
    public Type? AttributeType { get; }

    /// <summary>Gets action metadata without creating the attribute.</summary>
    public static ResourceAction For<TAttribute>() where TAttribute : Attribute => For(typeof(TAttribute));

    /// <summary>Gets action metadata without running attribute code.</summary>
    public static ResourceAction For(Type attributeType)
    {
        ArgumentNullException.ThrowIfNull(attributeType);
        return ResourceActionMetadata.Get(attributeType);
    }

    /// <summary>Resolves generated action metadata in the scenario assembly's load context.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ResourceAction FromGenerated(Type scope, string assemblyQualifiedAttributeTypeName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyQualifiedAttributeTypeName);
        var context = AssemblyLoadContext.GetLoadContext(scope.Assembly) ?? AssemblyLoadContext.Default;
        var type = Type.GetType(assemblyQualifiedAttributeTypeName,
            name => name.FullName == scope.Assembly.FullName ? scope.Assembly : context.LoadFromAssemblyName(name),
            typeResolver: null, throwOnError: true)!;
        return For(type);
    }
}
