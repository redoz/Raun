using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Raun.Generator.Analysis;
using Raun.Generator.Lowering;

namespace Raun.Generator;

/// <summary>Describes a resource value through the compiler's canonical mapping rules.</summary>
public sealed record ResourceParameterInfo(ITypeSymbol ValueType, ITypeSymbol CanonicalType, bool IsKey, bool IsCollection, bool IsIdentityOnly);
/// <summary>Describes a required named input of a resource action.</summary>
public sealed record ResourceRoleInfo(INamedTypeSymbol OwnerType, INamedTypeSymbol MarkerType, string Name, ITypeSymbol ResourceType, ITypeSymbol CanonicalType, INamedTypeSymbol ActionType, bool IsIdentityOnly);
/// <summary>Describes an action and its required resource roles.</summary>
public sealed record ResourceActionInfo(INamedTypeSymbol AttributeType, string Effect, IReadOnlyList<ResourceRoleInfo> Roles);

/// <summary>States whether a parameter has no action, one valid action, or an invalid action declaration.</summary>
public enum ResourceActionBindingStatus { None, Valid, Invalid }

/// <summary>Shares resource semantics between the generator, analyzers, and editor actions.</summary>
public static class ResourceCodeModel
{
    private static readonly ConditionalWeakTable<Compilation, ResourceMappingDiscovery> Mappings = new();

    /// <summary>Gets one normalized input action. Competing or malformed declarations return Invalid.</summary>
    public static ResourceActionBindingStatus GetParameterAction(Compilation compilation, IParameterSymbol parameter, out ResourceActionInfo? action)
    {
        action = null;
        var declarations = parameter.GetAttributes().Where(a => ResourceDefinitionReader.ActionType(a.AttributeClass) is not null).ToArray();
        if (declarations.Length == 0) { return ResourceActionBindingStatus.None; }
        if (declarations.Length != 1 || declarations[0].AttributeClass is not { } type
            || ResourceDefinitionReader.Validate(type, compilation).Any()
            || ResourceDefinitionReader.LinkBase(type) is not null && (type.ContainingType is not { } owner
                || !TryGetAction(compilation, owner, out _))
            || ResourceDefinitionReader.ActionType(type) is not { } actionType || !TryGetAction(compilation, actionType, out action))
        { return ResourceActionBindingStatus.Invalid; }
        return ResourceActionBindingStatus.Valid;
    }

    /// <summary>Gets the identity represented by a step parameter.</summary>
    public static bool TryGetParameter(Compilation compilation, IParameterSymbol parameter, out ResourceParameterInfo? info)
        => TryGetValue(compilation, parameter.Type, AttributeReader.ResourceKeyType(parameter), out info);

    /// <summary>Gets the identity represented by a task's result.</summary>
    public static bool TryGetReturn(Compilation compilation, IMethodSymbol method, out ResourceParameterInfo? info)
    {
        info = null;
        return SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var value) && value is not null
            && TryGetValue(compilation, value, null, out info);
    }

    /// <summary>Gets a valid action schema without executing attribute code.</summary>
    public static bool TryGetAction(Compilation compilation, INamedTypeSymbol attribute, out ResourceActionInfo? info)
    {
        info = null;
        if (!ResourceDefinitionReader.IsAction(attribute) || attribute.IsAbstract || !Closed(attribute)
            || ResourceDefinitionReader.Validate(attribute, compilation).Any()) { return false; }
        if (ResourceRoleMetadata.FromType(attribute, out var error) is null || error is not null) { return false; }
        var issues = new List<ResourceMetadataIssue>();
        var roles = new List<ResourceRoleInfo>();
        foreach (var role in ResourceDefinitionReader.Schema(attribute, issues, compilation))
        {
            if (!TryCanonical(compilation, role.ResourceType, out var canonical)) { return false; }
            roles.Add(new(role.OwnerType, role.MarkerType, role.PropertyName, role.ResourceType, canonical!, role.ActionType, IdentityOnly(canonical!)));
        }
        if (issues.Count > 0 || ResourceDefinitionReader.Kind(attribute) is not { } effect) { return false; }
        info = new(attribute, effect, roles.AsReadOnly());
        return true;
    }

    internal static bool TryGetValue(Compilation compilation, ITypeSymbol value, ITypeSymbol? keyOwner, out ResourceParameterInfo? info)
    {
        info = null;
        var element = value;
        var collection = false;
        if (keyOwner is null && !TryCanonical(compilation, element, out _))
        {
            if (value is IArrayTypeSymbol array) { element = array.ElementType; collection = true; }
            else if (value.SpecialType != SpecialType.System_String && value is INamedTypeSymbol named)
            {
                var sequence = new[] { named }.Concat(named.AllInterfaces).FirstOrDefault(i => i.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
                if (sequence is not null) { element = sequence.TypeArguments[0]; collection = true; }
            }
        }
        if (keyOwner is not null && value.SpecialType != SpecialType.System_String
            && value is not INamedTypeSymbol { Name: "ResourceKey", ContainingNamespace.Name: "Raun" }) { return false; }
        if (!TryCanonical(compilation, keyOwner ?? element, out var canonical)) { return false; }
        info = new(value, canonical!, keyOwner is not null, collection, IdentityOnly(canonical!));
        return true;
    }

    internal static bool MayHaveRuntimeMapping(Compilation compilation, IParameterSymbol parameter)
    {
        var value = AttributeReader.ResourceKeyType(parameter) ?? parameter.Type;
        if (value is IArrayTypeSymbol array) { value = array.ElementType; }
        else if (value is INamedTypeSymbol collection && value.SpecialType != SpecialType.System_String)
        {
            var sequence = new[] { collection }.Concat(collection.AllInterfaces)
                .FirstOrDefault(i => i.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
            if (sequence is not null) { value = sequence.TypeArguments[0]; }
        }
        return Closed(value) && value is INamedTypeSymbol { SpecialType: SpecialType.None, TypeKind: TypeKind.Class or TypeKind.Struct }
            && !TryCanonical(compilation, value, out _);
    }

    internal static bool TryCanonical(Compilation compilation, ITypeSymbol value, out ITypeSymbol? canonical)
    {
        canonical = null;
        if (!Closed(value)) { return false; }
        // Schema validation imports private metadata in a separate view. Compare canonical types
        // in the consumer compilation so equal referenced types keep equal Roslyn identities.
        var referenceId = DocumentationCommentId.CreateReferenceId(value);
        if (referenceId is not null && value.ContainingAssembly is { } assembly)
        {
            value = DocumentationCommentId.GetSymbolsForReferenceId(referenceId, compilation).OfType<ITypeSymbol>()
                .FirstOrDefault(candidate => candidate.ContainingAssembly?.Identity.Equals(assembly.Identity) is true) ?? value;
        }
        var mappings = Mappings.GetValue(compilation, ResourceMappingDiscovery.Discover);
        if (mappings.Diagnostics.Any(d => d.Id == "RAUN026")) { return false; }
        var parents = new Dictionary<ITypeSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var declaration in mappings.All)
        {
            var owner = Root(declaration.Owner);
            var source = Root(declaration.Source);
            if (!SymbolEqualityComparer.Default.Equals(owner, source)) { parents[source] = owner; }
        }
        var root = Root(value);
        if (!SymbolEqualityComparer.Default.Equals(root, value)) { canonical = root; return true; }

        ITypeSymbol Root(ITypeSymbol type)
        {
            while (parents.TryGetValue(type, out var parent)) { type = parent; }
            return type;
        }
        if (value is not INamedTypeSymbol named || !named.AllInterfaces.Concat(new[] { named }).Any(i =>
            ResourceDefinitionReader.Is(i.OriginalDefinition, "IResource") || ResourceDefinitionReader.Is(i.OriginalDefinition, "IResource", 1)
            || ResourceDefinitionReader.Is(i.OriginalDefinition, "IResourceIdentity"))) { return false; }
        canonical = value;
        return true;
    }

    private static bool IdentityOnly(ITypeSymbol type) => type is INamedTypeSymbol named
        && !named.AllInterfaces.Any(i => ResourceDefinitionReader.Is(i.OriginalDefinition, "IResource", 1));

    internal static bool Closed(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => false,
        INamedTypeSymbol named => !named.IsUnboundGenericType && named.TypeArguments.All(Closed)
            && (named.ContainingType is null || Closed(named.ContainingType)),
        IArrayTypeSymbol array => Closed(array.ElementType),
        _ => true,
    };
}
