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
    internal static RelationshipReadResult Read(IMethodSymbol method)
    {
        var relationships = new List<DeclaredRelationship>();
        var issues = new List<ResourceMetadataIssue>();
        foreach (var parameter in method.Parameters)
        { ReadSite(parameter.Name, AttributeReader.RoleAttribute(parameter.GetAttributes()), parameter.GetAttributes(), parameter); }
        var explicitRelations = method.GetReturnTypeAttributes().Where(IsStandalone).ToImmutableArray();
        ReadSite(AttributeReader.ReturnSubject, AttributeReader.ReturnAttribute(method),
            explicitRelations.Length > 0 ? explicitRelations : method.GetAttributes().Where(IsStandalone).ToImmutableArray(), method);
        var seen = new HashSet<(string Source, string Target)>();
        foreach (var relationship in relationships)
        {
            var subject = relationship.Source == AttributeReader.ReturnSubject ? (ISymbol)method : method.Parameters.First(p => p.Name == relationship.Source);
            var role = relationship.Source == AttributeReader.ReturnSubject ? AttributeReader.ReturnRole(method)
                : AttributeReader.ParameterRole((IParameterSymbol)subject);
            if (role is not ("Create" or "Load" or "Edit"))
            { issues.Add(new("RAUN032", subject, "A relationship source requires a Create, Load, or Edit action.")); }
            var targetParameter = method.Parameters.FirstOrDefault(p => p.Name == relationship.Target);
            var targetValid = relationship.Target == AttributeReader.ReturnSubject
                ? SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var result) && result is not null && AttributeReader.ReturnRole(method) is not null
                : targetParameter is not null;
            if (!targetValid || relationship.Target == relationship.Source)
            { issues.Add(new("RAUN010", subject, relationship.Target)); }
            else if (targetParameter is not null && targetParameter.Type.SpecialType != SpecialType.None
                && AttributeReader.ResourceKeyType(targetParameter) is null && AttributeReader.ParameterRole(targetParameter) is null)
            { issues.Add(new("RAUN032", subject, "The target must be a resource or a typed resource key.")); }
            if (!seen.Add((relationship.Source, relationship.Target)))
            { issues.Add(new("RAUN032", subject, "Duplicate or conflicting relationships name the same source and target.")); }
        }
        return new(relationships.OrderBy(r => r.Kind == "Reference" ? 0 : 1).ToImmutableArray(), issues.Distinct().ToImmutableArray());

        void ReadSite(string source, AttributeData? action, ImmutableArray<AttributeData> standalone, ISymbol subject)
        {
            if (action?.AttributeClass is { } type)
            {
                var schemaIssues = new List<ResourceMetadataIssue>();
                foreach (var property in ResourceDefinitionReader.Schema(type, schemaIssues))
                {
                    var supplied = action.NamedArguments.FirstOrDefault(a => a.Key == property.PropertyName);
                    if (supplied.Key is null)
                    {
                        if (property.Required) { issues.Add(new("RAUN032", subject, "Supply required relationship property '" + property.PropertyName + "'.")); }
                        continue;
                    }
                    var constant = supplied.Value;
                    if (constant.IsNull || property.IsCollection && constant.Kind != TypedConstantKind.Array)
                    { issues.Add(new("RAUN032", subject, "A supplied relationship binding cannot be null or have the wrong shape.")); continue; }
                    var values = property.IsCollection && constant.Kind == TypedConstantKind.Array && !constant.IsNull
                        ? constant.Values : ImmutableArray.Create(constant);
                    if (property.Required && values.IsEmpty)
                    { issues.Add(new("RAUN032", subject, "Supply a nonempty relationship property '" + property.PropertyName + "'.")); }
                    foreach (var value in values)
                    {
                        if (value.Value is not string target || string.IsNullOrWhiteSpace(target))
                        { issues.Add(new("RAUN032", subject, "A supplied relationship target must contain text.")); continue; }
                        relationships.Add(new(source, target, property.Kind,
                            property.ResourceType is null ? null : AssemblyQualifiedTypeName.From(type),
                            property.ResourceType is null ? null : property.PropertyName));
                    }
                }
            }
            foreach (var declaration in standalone.Where(IsStandalone))
            {
                var kind = declaration.ConstructorArguments.Length > 0 && declaration.ConstructorArguments[0].Value is int value
                    ? ResourceDefinitionReader.RelationKind(value) : null;
                var target = declaration.ConstructorArguments.Length > 1 ? declaration.ConstructorArguments[1].Value as string : null;
                if (kind is null || string.IsNullOrWhiteSpace(target))
                { issues.Add(new("RAUN032", subject, "A relationship needs a valid kind and a nonblank target.")); continue; }
                relationships.Add(new(source, target!, kind!));
            }
        }
    }
    internal static bool IsStandalone(AttributeData data) => ResourceDefinitionReader.Is(data.AttributeClass, "RelatesToAttribute");
}
