using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Raun.Generator.Diagnostics;

namespace Raun.Generator.Analysis;

internal sealed record ResourceDeclaration(INamedTypeSymbol Owner, ITypeSymbol Source);
internal sealed record ResourceMappingDiscovery(
    IReadOnlyList<ResourceDeclaration> Local,
    IReadOnlyList<INamedTypeSymbol> Imports,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    internal IReadOnlyList<ResourceDeclaration> All { get; init; } = [];

    internal bool HasMappings => Local.Count > 0 || Imports.Count > 0;

    internal static ResourceMappingDiscovery Discover(Compilation compilation)
    {
        var alternate = compilation.GetTypeByMetadataName("Raun.IResourceAlternate`1");
        var manifestContract = compilation.GetTypeByMetadataName("Raun.IResourceMappingManifest");
        var local = new List<ResourceDeclaration>();
        var declarations = new List<ResourceDeclaration>();
        var imports = new List<INamedTypeSymbol>();
        var diagnostics = new List<Diagnostic>();
        if (alternate is null || manifestContract is null) { return new(local, imports, diagnostics); }
        foreach (var owner in Types(compilation.Assembly.GlobalNamespace))
        {
            // Inherited class contracts retain their declaring owner. Interfaces describe contracts;
            // they cannot themselves provide the concrete static dispatch required by a manifest.
            if (owner.TypeKind == TypeKind.Interface) { continue; }
            foreach (var contract in AlternateContracts(owner, alternate))
            {
                var source = contract.TypeArguments[0];
                if (!Closed(owner) || !Closed(source) || !Accessible(compilation, owner) || !Accessible(compilation, source))
                {
                    diagnostics.Add(Diagnostic.Create(Descriptors.InvalidResourceAlternate, owner.Locations.FirstOrDefault(),
                        owner.ToDisplayString(), source.ToDisplayString()));
                    continue;
                }
                var declaration = new ResourceDeclaration(owner, source);
                if (!local.Any(d => Equal(d, declaration))) { local.Add(declaration); }
            }
        }
        declarations.AddRange(local);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols.OrderBy(a => a.Identity.ToString(), System.StringComparer.Ordinal))
        {
            var ns = assembly.GlobalNamespace.GetNamespaceMembers().FirstOrDefault(n => n.Name == "Raun")?
                .GetNamespaceMembers().FirstOrDefault(n => n.Name == "Generated");
            if (ns is null) { continue; }
            foreach (var manifest in ns.GetTypeMembers().OrderBy(t => t.Name, System.StringComparer.Ordinal))
            {
                if (!manifest.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, manifestContract))) { continue; }
                var fields = manifest.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue)
                    .ToDictionary(f => f.Name, f => f.ConstantValue);
                if (!fields.TryGetValue("MappingProtocol", out var version) || version is not 1
                    || !fields.TryGetValue("MappingCount", out var countValue) || countValue is not int count)
                {
                    diagnostics.Add(Diagnostic.Create(Descriptors.InvalidResourceAlternate, Location.None,
                        manifest.ToDisplayString(), "unsupported generated mapping manifest; rebuild its library"));
                    continue;
                }
                imports.Add(manifest);
                var seenOwners = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                for (var i = 0; i < count; i++)
                {
                    var owner = ReadOwner(compilation, fields, i);
                    if (owner is null)
                    {
                        diagnostics.Add(Diagnostic.Create(Descriptors.InvalidResourceAlternate, Location.None,
                            manifest.ToDisplayString(), "unresolvable generated owner; rebuild its library"));
                        continue;
                    }
                    if (!seenOwners.Add(owner)) { continue; }
                    // The compiler has already bound every generic argument to its defining assembly.
                    // Reuse the owner's contracts rather than reconstructing source types from names.
                    foreach (var contract in AlternateContracts(owner, alternate))
                    {
                        declarations.Add(new(owner, contract.TypeArguments[0]));
                    }
                }
            }
        }
        foreach (var group in declarations.GroupBy(d => d.Source, SymbolEqualityComparer.Default))
        {
            var owners = group.Select(d => d.Owner).Distinct(SymbolEqualityComparer.Default).ToArray();
            if (owners.Length > 1)
            {
                diagnostics.Add(Diagnostic.Create(Descriptors.ConflictingResourceAlternate,
                    group.First().Owner.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None,
                    group.Key?.ToDisplayString(), string.Join(", ", owners.Select(o => o!.ToDisplayString()))));
            }
        }
        return new(local.OrderBy(d => d.Owner.ToDisplayString(), System.StringComparer.Ordinal)
            .ThenBy(d => d.Source.ToDisplayString(), System.StringComparer.Ordinal).ToArray(), imports, diagnostics) { All = local.OrderBy(d => d.Owner.ToDisplayString(), System.StringComparer.Ordinal).ThenBy(d => d.Source.ToDisplayString(), System.StringComparer.Ordinal).Concat(declarations.Skip(local.Count)).ToArray() };
    }

    private static INamedTypeSymbol? ReadOwner(Compilation compilation, Dictionary<string, object?> fields, int index)
    {
        if (!fields.TryGetValue("Owner" + index, out var id) || id is not string referenceId
            || !fields.TryGetValue("OwnerAssembly" + index, out var assembly) || assembly is not string assemblyId) { return null; }
        return DocumentationCommentId.GetSymbolsForReferenceId(referenceId, compilation).OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => t.ContainingAssembly.Identity.ToString() == assemblyId);
    }

    private static IEnumerable<INamedTypeSymbol> AlternateContracts(INamedTypeSymbol owner, INamedTypeSymbol alternate)
        => owner.Interfaces.SelectMany(i => new[] { i }.Concat(i.AllInterfaces))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .Where(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, alternate));

    private static bool Accessible(Compilation compilation, ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) { return Accessible(compilation, array.ElementType); }
        if (type is INamedTypeSymbol named)
        {
            for (var containing = named; containing is not null; containing = containing.ContainingType)
            {
                if (containing.IsFileLocal) { return false; }
            }
            if (!named.TypeArguments.All(argument => Accessible(compilation, argument))) { return false; }
        }
        return compilation.IsSymbolAccessibleWithin(type, compilation.Assembly);
    }

    private static bool Equal(ResourceDeclaration left, ResourceDeclaration right)
        => SymbolEqualityComparer.Default.Equals(left.Owner, right.Owner) && SymbolEqualityComparer.Default.Equals(left.Source, right.Source);

    private static bool Closed(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => false,
        INamedTypeSymbol named => !named.IsUnboundGenericType && named.TypeArguments.All(Closed)
            && (named.ContainingType is null || Closed(named.ContainingType)),
        IArrayTypeSymbol array => Closed(array.ElementType),
        _ => true,
    };

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamedTypeSymbol type) { yield return type; }
            if (member is INamespaceOrTypeSymbol nested)
            {
                foreach (var child in Types(nested)) { yield return child; }
            }
        }
    }
}
