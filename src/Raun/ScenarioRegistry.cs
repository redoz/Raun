using Raun.Model;

namespace Raun;

/// <summary>
/// Process-wide map from a scenario method's full name to a factory that builds its lowered
/// <see cref="ScenarioDefinition"/>. The generator emits a module initializer that registers each
/// scenario here; a host discovers and runs what <see cref="Definitions"/> yields.
/// </summary>
public static class ScenarioRegistry
{
    /// <summary>
    /// The number of the contract between generated code and this runtime: everything the generated
    /// code calls, sets or relies on. The generator writes the number it generates for into each
    /// registration, and a registration that states another number is refused. It is raised whenever
    /// code generated before a change could no longer run correctly after it.
    /// </summary>
    public const int ContractVersion = 1;

    private static readonly ScenarioCatalog Catalog = new();

    /// <summary>Registers (or replaces) the definition factory for a scenario method. For a host or
    /// a test that builds definitions by hand; generated code uses the overload that states its
    /// contract.</summary>
    public static void Register(string methodFullName, Func<ScenarioDefinition> factory)
        => Catalog.Register(methodFullName, factory);

    /// <summary>
    /// What the generated module initializer calls. <paramref name="contractVersion"/> is the
    /// contract the code was generated for; when it is not <see cref="ContractVersion"/> the
    /// scenario is not registered and <see cref="Definitions"/> throws
    /// <see cref="ScenarioContractException"/>, naming the assembly to rebuild. This signature is
    /// the one part of the contract that cannot change: it is what tells old code that it is old.
    /// </summary>
    public static void Register(int contractVersion, string methodFullName, Func<ScenarioDefinition> factory)
        => Catalog.Register(contractVersion, methodFullName, factory);

    /// <summary>Looks up the definition factory for a scenario method.</summary>
    public static bool TryGet(string methodFullName, out Func<ScenarioDefinition>? factory)
        => Catalog.TryGet(methodFullName, out factory);

    /// <summary>The registration keys (method names, or individual generated case keys) currently registered.</summary>
    public static IReadOnlyCollection<string> RegisteredMethods => Catalog.Keys;

    /// <summary>Lowers every registered scenario once, in registration-key order. The one registry
    /// walk a host needs for discovery and for a run.</summary>
    /// <exception cref="ScenarioContractException">Some scenarios were registered by code generated
    /// for another contract.</exception>
    public static IReadOnlyList<ScenarioDefinition> Definitions() => Catalog.Definitions();
}
