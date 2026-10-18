using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Raun;

internal static class ResourceActionMetadata
{
    private static readonly ConditionalWeakTable<Type, ResourceAction> Actions = new();
    private static readonly HashSet<Type> Builtins = [typeof(CreatedAttribute), typeof(ReadAttribute), typeof(UpdatedAttribute), typeof(DeletedAttribute)];

    internal static ResourceAction Get(Type type) => Actions.GetValue(type, Build);

    private static (ResourceEffect Effect, string Name) Definition(Type type)
    {
        if (type.ContainsGenericParameters || type.IsAbstract || !typeof(ResourceAttribute).IsAssignableFrom(type))
        { throw new ArgumentException("Use a closed concrete resource action.", nameof(type)); }
        var definitions = new List<CustomAttributeData>();
        string? name = null;
        for (var current = type; current is not null && current != typeof(ResourceAttribute); current = current.BaseType)
        {
            definitions.AddRange(current.GetCustomAttributesData().Where(data => data.AttributeType == typeof(ResourceActionDefinitionAttribute)));
            if (name is not null) { continue; }
            var display = current.GetCustomAttributesData().FirstOrDefault(data => data.AttributeType == typeof(DisplayNameAttribute));
            if (display is null) { continue; }
            name = display.ConstructorArguments.Count == 1 ? display.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("An action name must contain text.", nameof(type)); }
        }
        if (definitions.Count != 1 || definitions[0].ConstructorArguments[0].Value is not int value || !Enum.IsDefined((ResourceEffect)value))
        { throw new ArgumentException("Declare exactly one valid ResourceActionDefinition in the hierarchy.", nameof(type)); }
        var effect = (ResourceEffect)value;
        name ??= Builtins.Contains(type) ? effect.ToString() : RoleName(type.Name);
        if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("An action name must contain text.", nameof(type)); }
        return (effect, name);
    }

    internal static string RoleName(string name)
    {
        name = name.Split('`')[0];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^9] : name;
    }

    internal static Type? LinkBase(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ResourceLinkAttribute<,>)) { return current; }
        }
        return null;
    }

    private static ResourceAction Build(Type type)
    {
        var definition = Definition(type);
        var hierarchy = new Stack<Type>();
        for (var current = type; current is not null && current != typeof(ResourceAttribute); current = current.BaseType) { hierarchy.Push(current); }
        var roles = new Dictionary<string, RelationshipDefinition>(StringComparer.Ordinal);
        while (hierarchy.TryPop(out var owner))
        {
            foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                var marker = nested;
                if (marker.ContainsGenericParameters && owner.IsConstructedGenericType
                    && marker.GetGenericArguments().Length == owner.GetGenericArguments().Length)
                { marker = marker.MakeGenericType(owner.GetGenericArguments()); }
                if (roles.ContainsKey(RoleName(marker.Name)))
                { throw new ArgumentException("A nested type cannot hide an inherited resource role.", nameof(type)); }
                if (LinkBase(marker) is not { } contract) { continue; }
                var arguments = contract.GenericTypeArguments;
                if (marker.ContainsGenericParameters || marker.IsAbstract || !marker.IsNestedPublic
                    || arguments[0].ContainsGenericParameters || arguments[0].IsPointer || arguments[0].IsByRef)
                { throw new ArgumentException("Declare a public, closed, concrete nested role with a closed resource target.", nameof(type)); }
                Definition(arguments[1]);
                var name = RoleName(marker.Name);
                if (string.IsNullOrWhiteSpace(name) || !roles.TryAdd(name, new(owner, marker, name, arguments[0], arguments[1])))
                { throw new ArgumentException("A role name must be nonblank and unique in the owner hierarchy.", nameof(type)); }
            }
            foreach (var member in owner.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is Type) { continue; }
                if (roles.ContainsKey(RoleName(member.Name)))
                { throw new ArgumentException("A member cannot hide a relationship role.", nameof(type)); }
            }
        }
        return new(definition.Effect, definition.Name, Builtins.Contains(type) ? null : type,
            Array.AsReadOnly(roles.Values.OrderBy(role => role.Name, StringComparer.Ordinal).ToArray()));
    }
}
