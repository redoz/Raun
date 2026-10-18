using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Lowering;

internal readonly record struct RelationshipPropertyMetadata(string PropertyName, INamedTypeSymbol OwnerType, INamedTypeSymbol MarkerType, ITypeSymbol ResourceType, INamedTypeSymbol ActionType);
internal readonly record struct ResourceMetadataIssue(string Id, ISymbol Subject, string Reason,
    Location? Location = null, ImmutableDictionary<string, string?>? Properties = null);

internal static class ResourceDefinitionReader
{
    // Use a metadata-only view for declaration validation: default Roslyn imports omit private members,
    // but reflection sees them. The weak key prevents editor compilations from being retained.
    private static readonly ConditionalWeakTable<Compilation, Compilation> MetadataViews = new();

    private static INamedTypeSymbol MetadataOwner(INamedTypeSymbol type, Compilation? compilation)
    {
        if (compilation is null || type.DeclaringSyntaxReferences.Length > 0) { return type; }
        var view = MetadataViews.GetValue(compilation, static source => CSharpCompilation.Create(
            "Raun.ResourceMetadata", references: source.References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All)));
        var referenceId = DocumentationCommentId.CreateReferenceId(type);
        if (referenceId is not null && DocumentationCommentId.GetSymbolsForReferenceId(referenceId, view).OfType<INamedTypeSymbol>()
            .FirstOrDefault(candidate => candidate.ContainingAssembly.Identity.Equals(type.ContainingAssembly.Identity)) is { } constructed)
        { return constructed; }
        var assembly = view.References.Select(view.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>()
            .FirstOrDefault(candidate => candidate.Identity.Equals(type.ContainingAssembly.Identity));
        var names = new Stack<string>();
        for (var current = type.OriginalDefinition; current is not null; current = current.ContainingType) { names.Push(current.MetadataName); }
        var prefix = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".";
        return assembly?.GetTypeByMetadataName(prefix + string.Join("+", names)) ?? type;
    }

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

    internal static string? ActionKind(int kind) => kind switch { 0 => "Create", 1 => "Read", 2 => "Update", 3 => "Delete", _ => null };
    internal static bool IsBuiltin(INamedTypeSymbol type) => type.ContainingAssembly.Name == "Raun" && type.ContainingNamespace.ToDisplayString() == "Raun"
        && type.Name is "CreatedAttribute" or "ReadAttribute" or "UpdatedAttribute" or "DeletedAttribute";

    internal static INamedTypeSymbol? LinkBase(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        { if (Is(current.OriginalDefinition, "ResourceLinkAttribute", 2)) { return current; } }
        return null;
    }

    internal static INamedTypeSymbol? ActionType(INamedTypeSymbol? type)
        => LinkBase(type)?.TypeArguments[1] as INamedTypeSymbol ?? (IsAction(type) ? type : null);

    internal static string RoleName(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name.Substring(0, name.Length - 9) : name;

    internal static ImmutableArray<RelationshipPropertyMetadata> Schema(INamedTypeSymbol type, List<ResourceMetadataIssue> issues, Compilation? compilation = null)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && !Is(current, "ResourceAttribute"); current = current.BaseType) { hierarchy.Push(current); }
        var roles = new Dictionary<string, RelationshipPropertyMetadata>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            var owner = MetadataOwner(hierarchy.Pop(), compilation);
            foreach (var member in owner.GetMembers())
            {
                var name = RoleName(member.Name);
                if (roles.ContainsKey(name))
                { issues.Add(new("RAUN031", member, "A member cannot hide an inherited resource role.")); continue; }
                if (member is not INamedTypeSymbol marker || LinkBase(marker) is not { } link) { continue; }
                if (string.IsNullOrWhiteSpace(name) || marker.IsAbstract || marker.Arity != 0 || marker.DeclaredAccessibility != Accessibility.Public
                    || link.TypeArguments[1] is not INamedTypeSymbol action || action.IsAbstract || Kind(action) is null || DefinitionCount(action) != 1
                    || ResourceCodeModel.Closed(owner) && (!ResourceCodeModel.Closed(marker)
                        || !ResourceCodeModel.Closed(link.TypeArguments[0]) || !ResourceCodeModel.Closed(action)))
                { issues.Add(new("RAUN031", marker, "Use a public, closed resource role with a concrete resource action.")); continue; }
                roles.Add(name, new(name, owner, marker, link.TypeArguments[0], action));
            }
        }
        return roles.Values.OrderBy(role => role.PropertyName, StringComparer.Ordinal).ToImmutableArray();
    }

    private static int DefinitionCount(INamedTypeSymbol type)
    {
        var count = 0;
        for (var current = type; current is not null; current = current.BaseType)
        { count += current.GetAttributes().Count(a => Is(a.AttributeClass, "ResourceActionDefinitionAttribute")); }
        return count;
    }

    internal static IEnumerable<ResourceMetadataIssue> Validate(INamedTypeSymbol type, Compilation? compilation = null)
    {
        var hasDefinition = type.GetAttributes().Any(a => Is(a.AttributeClass, "ResourceActionDefinitionAttribute"));
        if (LinkBase(type) is not null && !Is(type.OriginalDefinition, "ResourceLinkAttribute", 2))
        {
            if (type.ContainingType is not { } owner || !IsAction(owner))
            { yield return new("RAUN031", type, "Declare a resource role inside its resource action."); }
            yield break;
        }
        if (!IsAction(type))
        {
            if (hasDefinition) { yield return new("RAUN030", type, "Resource metadata requires a ResourceAttribute-derived owner."); }
            yield break;
        }
        if (Is(type, "ResourceAttribute")) { yield break; }
        if (DefinitionCount(type) != 1 || Kind(type) is null)
        { yield return new("RAUN030", type, "Declare exactly one valid ResourceActionDefinition across the attribute hierarchy."); }
        var issues = new List<ResourceMetadataIssue>();
        Schema(type, issues, compilation);
        foreach (var issue in issues) { yield return issue; }
    }
}
