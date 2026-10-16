using System.Runtime.CompilerServices;
using System.ComponentModel;

namespace Raun;

internal static class ResourceActionMetadata
{
    private static readonly ConditionalWeakTable<Type, ResourceAction> Actions = new();
    private static readonly Dictionary<Type, LifecycleVerb> Bases = new Dictionary<Type, LifecycleVerb>
    {
        [typeof(CreatedAttribute)] = LifecycleVerb.Create,
        [typeof(LoadedAttribute)] = LifecycleVerb.Load,
        [typeof(ReadAttribute)] = LifecycleVerb.Read,
        [typeof(EditedAttribute)] = LifecycleVerb.Edit,
        [typeof(DeletedAttribute)] = LifecycleVerb.Delete,
    };

    internal static ResourceAction Get(Type type) => Actions.GetValue(type, Build);

    private static ResourceAction Build(Type type)
    {
        if (type.ContainsGenericParameters || !typeof(ResourceAttribute).IsAssignableFrom(type))
        { throw new ArgumentException("Use a closed ResourceAttribute type with one ResourceActionDefinition.", nameof(type)); }
        var definitions = new List<System.Reflection.CustomAttributeData>();
        string? displayName = null;
        var hierarchy = new List<Type>();
        for (var current = type; current is not null && current != typeof(ResourceAttribute); current = current.BaseType)
        {
            hierarchy.Add(current);
            definitions.AddRange(current.GetCustomAttributesData().Where(data => data.AttributeType == typeof(ResourceActionDefinitionAttribute)));
            if (displayName is not null || Bases.ContainsKey(current)) { continue; }
            var label = current.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType == typeof(DisplayNameAttribute));
            if (label is null) { continue; }
            displayName = label.ConstructorArguments.Count == 1 ? label.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(displayName))
            { throw new ArgumentException("A resource action's DisplayName must contain text.", nameof(type)); }
        }
        if (definitions.Count != 1 || definitions[0].ConstructorArguments[0].Value is not int kind || !Enum.IsDefined((ResourceActionKind)kind))
        { throw new ArgumentException("Declare exactly one valid ResourceActionDefinition across the attribute hierarchy.", nameof(type)); }
        var effect = (LifecycleVerb)kind;
        var builtin = Bases.ContainsKey(type);
        var name = builtin ? effect.ToString() : displayName ?? type.Name.Split('`')[0];
        if (!builtin && displayName is null && name.EndsWith("Attribute", StringComparison.Ordinal)) { name = name[..^9]; }
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("A resource action name must contain text. Set DisplayName on the attribute type.", nameof(type)); }
        var properties = new Dictionary<string, (System.Reflection.PropertyInfo Property, RelationshipDefinition? Definition)>(StringComparer.Ordinal);
        foreach (var owner in hierarchy.AsEnumerable().Reverse())
        {
            foreach (var property in owner.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var markers = property.GetCustomAttributesData().Where(data => data.AttributeType == typeof(RelationshipAttribute)
                    || data.AttributeType.IsGenericType && data.AttributeType.GetGenericTypeDefinition() == typeof(RelationshipAttribute<>)).ToArray();
                if (properties.TryGetValue(property.Name, out var inherited) && (inherited.Definition is not null || markers.Length > 0))
                { throw new ArgumentException("A relationship property cannot hide or override an inherited property.", nameof(type)); }
                if (markers.Length == 0) { properties[property.Name] = (property, null); continue; }
                var marker = markers[0];
                if (markers.Length != 1 || marker.ConstructorArguments[0].Value is not int relation || !Enum.IsDefined((ResourceRelation)relation)
                    || property.PropertyType != typeof(string) && property.PropertyType != typeof(string[])
                    || property.GetMethod is not { IsPublic: true, IsStatic: false } || property.SetMethod is not { IsPublic: true, IsStatic: false }
                    || property.GetIndexParameters().Length != 0)
                { throw new ArgumentException("A relationship needs one valid marker on a public instance string or string-array property with get and set.", nameof(type)); }
                var required = !marker.NamedArguments.Any(argument => argument.MemberName == nameof(RelationshipAttribute.Required) && argument.TypedValue.Value is false);
                var resourceType = marker.AttributeType.IsGenericType ? marker.AttributeType.GenericTypeArguments[0] : null;
                properties[property.Name] = (property, new(property.Name, (ResourceRelation)relation, resourceType, property.PropertyType == typeof(string[]), required));
            }
        }
        var schema = Array.AsReadOnly(properties.Values.Where(value => value.Definition is not null).Select(value => value.Definition!)
            .OrderBy(value => value.PropertyName, StringComparer.Ordinal).ToArray());
        return new(effect, name, builtin ? null : type, schema);
    }
}
