using System.Collections.Concurrent;
using System.ComponentModel;

namespace Raun;

internal static class ResourceActionMetadata
{
    private static readonly ConcurrentDictionary<Type, ResourceAction> Actions = new();
    private static readonly Dictionary<Type, LifecycleVerb> Bases = new Dictionary<Type, LifecycleVerb>
    {
        [typeof(CreatedAttribute)] = LifecycleVerb.Create,
        [typeof(LoadedAttribute)] = LifecycleVerb.Load,
        [typeof(ReadAttribute)] = LifecycleVerb.Read,
        [typeof(EditedAttribute)] = LifecycleVerb.Edit,
        [typeof(DeletedAttribute)] = LifecycleVerb.Delete,
    };

    internal static ResourceAction Get(Type type) => Actions.GetOrAdd(type, Build);

    private static ResourceAction Build(Type type)
    {
        if (type.ContainsGenericParameters || !typeof(Attribute).IsAssignableFrom(type))
        { throw new ArgumentException("Use a closed attribute type derived from a Raun resource role.", nameof(type)); }
        string? displayName = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (Bases.TryGetValue(current, out var effect))
            {
                if (current == type) { return new(effect, effect.ToString(), attributeType: null); }
                var simpleName = type.Name.Split('`')[0];
                if (simpleName.EndsWith("Attribute", StringComparison.Ordinal)) { simpleName = simpleName[..^9]; }
                return new(effect, displayName ?? simpleName, type);
            }
            if (displayName is not null) { continue; }
            var label = current.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType == typeof(DisplayNameAttribute));
            if (label is null) { continue; }
            displayName = label.ConstructorArguments.Count == 1 ? label.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(displayName))
            { throw new ArgumentException("A resource action's DisplayName must contain text.", nameof(type)); }
        }
        throw new ArgumentException("Use an attribute derived from Created, Loaded, Read, Edited, or Deleted.", nameof(type));
    }
}
