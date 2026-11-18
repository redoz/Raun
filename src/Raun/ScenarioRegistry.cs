using Raun.Model;

namespace Raun;

/// <summary>
/// Process-wide map from a scenario method's full name to a factory that builds its lowered
/// <see cref="ScenarioDefinition"/>. The generator emits a module initializer that registers each
/// scenario here; a host discovers and runs what <see cref="Definitions"/> yields.
/// </summary>
public static class ScenarioRegistry
{
    private static readonly ScenarioCatalog Catalog = new();

    /// <summary>Registers (or replaces) the definition factory for a scenario method.</summary>
    public static void Register(string methodFullName, Func<ScenarioDefinition> factory)
        => Catalog.Register(methodFullName, factory);

    /// <summary>Looks up the definition factory for a scenario method.</summary>
    public static bool TryGet(string methodFullName, out Func<ScenarioDefinition>? factory)
        => Catalog.TryGet(methodFullName, out factory);

    /// <summary>The registration keys (method names, or individual generated case keys) currently registered.</summary>
    public static IReadOnlyCollection<string> RegisteredMethods => Catalog.Keys;

    /// <summary>Lowers every registered scenario once, in registration-key order. The one registry
    /// walk a host needs for discovery and for a run.</summary>
    public static IReadOnlyList<ScenarioDefinition> Definitions() => Catalog.Definitions();
}
