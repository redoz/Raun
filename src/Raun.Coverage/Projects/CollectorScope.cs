using Microsoft.Extensions.FileSystemGlobbing;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Raun.Coverage.Projects;

/// <summary>Effective collection exclusions, shared by project and source inventory.</summary>
public sealed record CollectorScope
{
    public IReadOnlyList<string> CoverletSourceExcludes { get; init; } = [];
    public bool CoverletSingleHit { get; init; }
    public IReadOnlyList<string> CoverletIncludes { get; init; } = [];
    public IReadOnlyList<string> CoverletExcludes { get; init; } = [];

    public bool IncludesCoverletType(string assembly, string type)
        => (CoverletIncludes.Count == 0 || CoverletIncludes.Any(filter => CoverletMatch(filter, assembly, type)))
            && !CoverletExcludes.Any(filter => CoverletMatch(filter, assembly, type));

    private static bool CoverletMatch(string filter, string assembly, string type)
    {
        var end = filter.IndexOf(']', StringComparison.Ordinal);
        if (!filter.AsSpan().StartsWith('[') || end < 1) { return false; }
        static string Pattern(string glob) => "^" + Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", "?", StringComparison.Ordinal) + "$";
        return Match(assembly, Pattern(filter[1..end])) && Match(type.Replace('+', '.'), Pattern(filter[(end + 1)..].Replace('+', '.')));
    }

    public bool IncludeTestAssembly { get; init; }
    public bool MergeDefaultAttributes { get; init; } = true;
    public IReadOnlyList<string> ModuleIncludes { get; init; } = [];
    public IReadOnlyList<string> ModuleExcludes { get; init; } = [];
    public IReadOnlyList<string> SourceIncludes { get; init; } = [];
    public IReadOnlyList<string> SourceExcludes { get; init; } = [];
    public IReadOnlyList<string> FunctionIncludes { get; init; } = [];
    public IReadOnlyList<string> FunctionExcludes { get; init; } = [];
    public IReadOnlyList<string> AttributeIncludes { get; init; } = [];
    public IReadOnlyList<string> AttributeExcludes { get; init; } = [];

    public IReadOnlyList<string> CompanyIncludes { get; init; } = [];
    public IReadOnlyList<string> CompanyExcludes { get; init; } = [];
    public IReadOnlyList<string> PublicKeyIncludes { get; init; } = [];
    public IReadOnlyList<string> PublicKeyExcludes { get; init; } = [];

    public bool IncludesAssembly(IAssemblySymbol assembly)
    {
        var company = assembly.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyCompanyAttribute")?.ConstructorArguments.FirstOrDefault().Value?.ToString() ?? "";
        return Matches(company, CompanyIncludes, CompanyExcludes)
            && Matches(Convert.ToHexString(assembly.Identity.PublicKeyToken.AsSpan()), PublicKeyIncludes, PublicKeyExcludes);
    }

    public bool IncludesModule(string path) => Matches(path, ModuleIncludes, ModuleExcludes);
    public bool IncludesSource(string path) => Matches(path, SourceIncludes, SourceExcludes) && !IsCoverletSourceExcluded(path);

    private bool IsCoverletSourceExcluded(string path)
    {
        if (CoverletSourceExcludes.Count == 0) { return false; }
        // Coverlet's ExcludedFilesHelper uses Matcher with filesystem roots removed.
        static string Relative(string value)
        {
            value = value.Replace('\\', '/');
            return Path.IsPathRooted(value) ? value[Path.GetPathRoot(value)!.Length..] : value;
        }
        var matcher = new Matcher();
        foreach (var pattern in CoverletSourceExcludes) { matcher.AddInclude(Relative(pattern)); }
        return matcher.Match(Relative(path)).HasMatches;
    }
    public bool IncludesFunction(string name) => Matches(name, FunctionIncludes, FunctionExcludes);

    public bool IsExcluded(ISymbol symbol, bool requireIncludedAttribute = true)
    {
        var names = new List<string>();
        for (ISymbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is IMethodSymbol { AssociatedSymbol: { } associated })
            {
                names.AddRange(associated.GetAttributes().Select(attribute => attribute.AttributeClass?.ToDisplayString() ?? ""));
            }
            names.AddRange(current.GetAttributes().Select(attribute => attribute.AttributeClass?.ToDisplayString() ?? ""));
        }
        if (MergeDefaultAttributes && names.Any(name => name is
            "System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute" or
            "System.Diagnostics.DebuggerHiddenAttribute" or
            "System.Diagnostics.DebuggerNonUserCodeAttribute" or
            "System.CodeDom.Compiler.GeneratedCodeAttribute"))
        {
            return true;
        }
        return (requireIncludedAttribute && AttributeIncludes.Count > 0 && !names.Any(name => AttributeIncludes.Any(pattern => Match(name, pattern))))
            || names.Any(name => AttributeExcludes.Any(pattern => Match(name, pattern)));
    }

    private static bool Matches(string value, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
        => (includes.Count == 0 || includes.Any(pattern => Match(value, pattern)))
            && !excludes.Any(pattern => Match(value, pattern));

    private static bool Match(string value, string pattern)
        => Regex.IsMatch(value, pattern, RegexOptions.ECMAScript | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
}
