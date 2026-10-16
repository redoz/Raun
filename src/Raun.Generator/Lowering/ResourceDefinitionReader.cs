using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Lowering;

internal readonly record struct RelationshipPropertyMetadata(string PropertyName, string Kind, ITypeSymbol? ResourceType, bool IsCollection, bool Required);
internal readonly record struct ResourceMetadataIssue(string Id, ISymbol Subject, string Reason);

internal static class ResourceDefinitionReader
{
    internal static bool Is(INamedTypeSymbol? type, string name, int arity = 0)
        => type is not null && type.Name == name && type.Arity == arity
            && type.ContainingNamespace.ToDisplayString() == "Raun" && type.ContainingAssembly.Name == "Raun";

    internal static bool IsAction(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        { if (Is(current, "ResourceAttribute")) { return true; } }
        return false;
    }

    internal static string? Kind(INamedTypeSymbol? type)
    {
        if (!IsAction(type)) { return null; }
        for (var current = type; current is not null; current = current.BaseType)
        {
            var definition = current.GetAttributes().FirstOrDefault(a => Is(a.AttributeClass, "ResourceActionDefinitionAttribute"));
            if (definition is not null) { return definition.ConstructorArguments.FirstOrDefault().Value is int value ? ActionKind(value) : null; }
        }
        return null;
    }

    internal static string? ActionKind(int kind) => kind switch { 0 => "Create", 1 => "Load", 2 => "Read", 3 => "Edit", 4 => "Delete", _ => null };
    internal static string? RelationKind(int kind) => kind switch { 0 => "Reference", 1 => "Consume", _ => null };
    internal static bool IsMarker(AttributeData data) => Is(data.AttributeClass, "RelationshipAttribute") || Is(data.AttributeClass, "RelationshipAttribute", 1);
    internal static bool IsBuiltin(INamedTypeSymbol type) => type.ContainingAssembly.Name == "Raun" && type.ContainingNamespace.ToDisplayString() == "Raun"
        && type.Name is "CreatedAttribute" or "LoadedAttribute" or "ReadAttribute" or "EditedAttribute" or "DeletedAttribute";

    internal static ImmutableArray<RelationshipPropertyMetadata> Schema(INamedTypeSymbol type, List<ResourceMetadataIssue> issues)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && !Is(current, "ResourceAttribute"); current = current.BaseType) { hierarchy.Push(current); }
        var properties = new Dictionary<string, RelationshipPropertyMetadata?>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            foreach (var property in hierarchy.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                var markers = property.GetAttributes().Where(IsMarker).ToArray();
                if (properties.TryGetValue(property.Name, out var inherited) && (inherited.HasValue || markers.Length > 0))
                { issues.Add(new("RAUN031", property, "A relationship property cannot hide or override an inherited property.")); continue; }
                if (markers.Length == 0) { properties[property.Name] = null; continue; }
                var marker = markers[0];
                var kind = marker.ConstructorArguments.FirstOrDefault().Value is int value ? RelationKind(value) : null;
                var collection = property.Type is IArrayTypeSymbol array && array.ElementType.SpecialType == SpecialType.System_String;
                if (markers.Length != 1 || kind is null || property.IsStatic || property.IsIndexer
                    || property.DeclaredAccessibility != Accessibility.Public || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.SetMethod?.DeclaredAccessibility != Accessibility.Public
                    || property.Type.SpecialType != SpecialType.System_String && !collection)
                { issues.Add(new("RAUN031", property, "Use one valid relationship marker on a public instance string or string-array property with get and set.")); continue; }
                var required = !marker.NamedArguments.Any(a => a.Key == "Required" && a.Value.Value is false);
                var resource = marker.AttributeClass!.Arity == 1 ? marker.AttributeClass.TypeArguments[0] : null;
                properties[property.Name] = new(property.Name, kind!, resource, collection, required);
            }
        }
        return properties.Values.Where(p => p.HasValue).Select(p => p!.Value).OrderBy(p => p.PropertyName, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static IEnumerable<ResourceMetadataIssue> Validate(INamedTypeSymbol type)
    {
        var hasDefinition = type.GetAttributes().Any(a => Is(a.AttributeClass, "ResourceActionDefinitionAttribute"));
        var hasProperties = type.GetMembers().OfType<IPropertySymbol>().Any(p => p.GetAttributes().Any(IsMarker));
        if (!IsAction(type))
        {
            if (hasDefinition || hasProperties) { yield return new("RAUN030", type, "Resource metadata requires a ResourceAttribute-derived owner."); }
            yield break;
        }
        if (Is(type, "ResourceAttribute")) { yield break; }
        var definitions = 0;
        for (var current = type; current is not null; current = current.BaseType)
        { definitions += current.GetAttributes().Count(a => Is(a.AttributeClass, "ResourceActionDefinitionAttribute")); }
        if (definitions != 1 || Kind(type) is null) { yield return new("RAUN030", type, "Declare exactly one valid ResourceActionDefinition across the attribute hierarchy."); }
        var issues = new List<ResourceMetadataIssue>();
        Schema(type, issues);
        foreach (var issue in issues) { yield return issue; }
    }
}
