using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Raun.Generator.Syntax;

namespace Raun.Generator.Lowering;

internal readonly record struct DeclaredRelationship(string Source, string Target, string Kind, string? AttributeIdentity = null, string? PropertyName = null);
internal readonly record struct RelationshipReadResult(ImmutableArray<DeclaredRelationship> Relationships, ImmutableArray<ResourceMetadataIssue> Issues);

internal static class RelationshipReader
{
    internal static RelationshipReadResult Read(IMethodSymbol method, Compilation? compilation = null)
    {
        var relationships = new List<DeclaredRelationship>();
        var issues = new List<ResourceMetadataIssue>();
        var owners = new List<(string Source, ISymbol Subject, AttributeData Attribute, INamedTypeSymbol Type)>();
        foreach (var parameter in method.Parameters)
        {
            var attribute = AttributeReader.RoleAttribute(parameter.GetAttributes());
            if (ResourceDefinitionReader.ActionType(attribute?.AttributeClass) is { } type)
            { owners.Add((parameter.Name, parameter, attribute!, type)); }
        }
        var returned = AttributeReader.ReturnAttribute(method);
        if (ResourceDefinitionReader.ActionType(returned?.AttributeClass) is { } returnType)
        { owners.Add((AttributeReader.ReturnSubject, method, returned!, returnType)); }
        var bindings = new HashSet<(string Source, string Role)>();
        foreach (var target in method.Parameters)
        {
            foreach (var attribute in target.GetAttributes())
            {
                var marker = attribute.AttributeClass;
                var packaged = ResourceDefinitionReader.LinkBase(marker);
                var raw = IsStandalone(attribute);
                if (packaged is null && !raw) { continue; }
                var owner = packaged is not null ? marker?.ContainingType : marker?.TypeArguments[0] as INamedTypeSymbol;
                var roleName = packaged is not null ? ResourceDefinitionReader.RoleName(marker!.Name)
                    : attribute.ConstructorArguments.FirstOrDefault().Value as string;
                var source = attribute.NamedArguments.FirstOrDefault(a => a.Key == "Source").Value.Value as string;
                if (owner is null || string.IsNullOrWhiteSpace(roleName))
                { issues.Add(new("RAUN032", target, "A link requires an action owner and a nonempty role name.")); continue; }
                var candidates = owners.Where(o => Matches(o.Type, owner) && (source is null || o.Source == source)).ToArray();
                if (candidates.Length != 1)
                { issues.Add(new("RAUN032", target, "Bind the link to exactly one action application. Use Source when the owner is ambiguous.")); continue; }
                var applied = candidates[0];
                var schema = ResourceDefinitionReader.Schema(owner, issues, compilation);
                var role = schema.FirstOrDefault(r => r.PropertyName == roleName);
                if (role.ResourceType is null)
                { issues.Add(new("RAUN032", target, "The owner does not declare resource role '" + roleName + "'.")); continue; }
                if (applied.Source == target.Name || !bindings.Add((applied.Source, roleName!)))
                { issues.Add(new("RAUN032", target, "Bind each resource role once, to a different input.")); continue; }
                if (AttributeReader.ParameterAction(target) is null)
                { issues.Add(new("RAUN032", target, "A raw link requires a separate resource action on its input.")); }
                if (compilation is not null)
                {
                    var knownInput = ResourceCodeModel.TryGetParameter(compilation, target, out var input);
                    var knownExpected = ResourceCodeModel.TryCanonical(compilation, role.ResourceType, out var expected);
                    // Fluent host configuration is runtime data. Reject known mismatches and untyped
                    // scalar inputs now; the independent recorder validates opaque mapped values later.
                    if (knownInput && knownExpected && !SymbolEqualityComparer.Default.Equals(input!.CanonicalType, expected)
                        || !knownInput && !ResourceCodeModel.MayHaveRuntimeMapping(compilation, target))
                    { issues.Add(new("RAUN032", target, "The input must represent the role's canonical resource type.")); }
                }
                relationships.Add(new(applied.Source, target.Name, "Link", AssemblyQualifiedTypeName.From(applied.Type), roleName));
            }
        }
        foreach (var owner in owners)
        {
            foreach (var role in ResourceDefinitionReader.Schema(owner.Type, issues, compilation))
            {
                if (bindings.Contains((owner.Source, role.PropertyName))) { continue; }
                var properties = ImmutableDictionary<string, string?>.Empty
                    .Add("Raun.OwnerId", owner.Type.GetDocumentationCommentId())
                    .Add("Raun.RoleName", role.PropertyName)
                    .Add("Raun.ExpectedResourceId", role.ResourceType.GetDocumentationCommentId())
                    .Add("Raun.MarkerId", role.MarkerType.GetDocumentationCommentId())
                    .Add("Raun.Source", owner.Source == AttributeReader.ReturnSubject ? "$return" : owner.Source);
                issues.Add(new("RAUN036", owner.Subject, "Supply required resource role '" + role.PropertyName + "'.",
                    owner.Attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation(), properties));
            }
        }
        return new(relationships.ToImmutableArray(), issues.Distinct().ToImmutableArray());
    }

    private static bool Matches(INamedTypeSymbol actual, INamedTypeSymbol owner)
    {
        for (var current = actual; current is not null; current = current.BaseType)
        { if (SymbolEqualityComparer.Default.Equals(current, owner)) { return true; } }
        return false;
    }

    internal static bool IsStandalone(AttributeData data) => ResourceDefinitionReader.Is(data.AttributeClass?.OriginalDefinition, "LinkAttribute", 1);
}
