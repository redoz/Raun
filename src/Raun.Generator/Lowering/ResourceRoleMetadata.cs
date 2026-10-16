using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Raun.Generator.Syntax;

namespace Raun.Generator.Lowering;

internal readonly record struct ResourceRoleMetadata(string Verb, string Name, string? AttributeIdentity)
{
    internal static string? BaseVerb(INamedTypeSymbol? type) => ResourceDefinitionReader.Kind(type);

    internal static ResourceRoleMetadata? From(AttributeData? attribute, out string? error)
    {
        error = null;
        var type = attribute?.AttributeClass;
        var verb = BaseVerb(type);
        if (verb is null || type is null) { return null; }
        if (ResourceDefinitionReader.IsBuiltin(type)) { return new(verb, verb, null); }
        var name = type.Name.EndsWith("Attribute", StringComparison.Ordinal) ? type.Name.Substring(0, type.Name.Length - 9) : type.Name;
        for (var current = type; current is not null && !ResourceDefinitionReader.Is(current, "ResourceAttribute"); current = current.BaseType)
        {
            var display = current.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DisplayNameAttribute");
            if (display is null) { continue; }
            name = display.ConstructorArguments.Length == 1 ? display.ConstructorArguments[0].Value as string ?? "" : "";
            break;
        }
        if (string.IsNullOrWhiteSpace(name)) { error = "The resource action name must contain text."; }
        return new(verb, name, AssemblyQualifiedTypeName.From(type));
    }

    internal static IEnumerable<(string Id, ISymbol Subject, string Reason)> Validate(IMethodSymbol method, Compilation? compilation = null)
    {
        foreach (var parameter in method.Parameters)
        {
            foreach (var problem in ValidateSite(parameter, parameter.GetAttributes(), compilation)) { yield return problem; }
        }
        foreach (var problem in ValidateSite(method, method.GetReturnTypeAttributes(), compilation)) { yield return problem; }
        foreach (var problem in ValidateSite(method, method.GetAttributes(), compilation)) { yield return problem; }
        if ((method.GetAttributes().Any(a => BaseVerb(a.AttributeClass) is not null)
            || method.GetReturnTypeAttributes().Any(a => BaseVerb(a.AttributeClass) is not null))
            && (!SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var result) || result is null))
        { yield return ("RAUN029", method, "A method or return role requires a Task<T> or ValueTask<T> result."); }
    }

    private static IEnumerable<(string Id, ISymbol Subject, string Reason)> ValidateSite(ISymbol subject, IEnumerable<AttributeData> attributes, Compilation? compilation)
    {
        var roles = attributes.Where(a => ResourceDefinitionReader.IsAction(a.AttributeClass)).ToArray();
        if (roles.Length > 1) { yield return ("RAUN028", subject, "Use one resource role for this subject."); }
        foreach (var role in roles)
        {
            if (role.AttributeClass is { } type && type.DeclaringSyntaxReferences.Length == 0)
            {
                foreach (var issue in ResourceDefinitionReader.Validate(type, compilation)) { yield return (issue.Id, subject, issue.Reason); }
            }
            From(role, out var error);
            if (error is not null) { yield return ("RAUN027", subject, error); }
        }
    }
}
