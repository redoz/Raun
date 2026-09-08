using Raun;
using Raun.Model;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Generated code registers each scenario's definition factory keyed by the originating method's
/// full name; the xUnit discoverer looks the factory up by the same key.
/// </summary>
public class ScenarioRegistryTests
{
    private static ScenarioDefinition Empty(string method) => new()
    {
        ScenarioId = "id",
        DisplayName = method,
        MethodName = method,
        Nodes = [],
    };

    [Fact]
    public void Registered_factory_is_retrievable_by_method_name()
    {
        var key = "Ns.Type." + nameof(Registered_factory_is_retrievable_by_method_name);
        ScenarioRegistry.Register(key, () => Empty(key));

        Assert.True(ScenarioRegistry.TryGet(key, out var factory));
        Assert.Equal(key, factory!().MethodName);
    }

    [Fact]
    public void Missing_key_returns_false()
    {
        Assert.False(ScenarioRegistry.TryGet("Ns.Type.NeverRegistered", out var factory));
        Assert.Null(factory);
    }

    [Fact]
    public void Definitions_lowers_every_registered_scenario_once()
    {
        // The one registry walk every host needs; before this, each adapter hand-rolled
        // RegisteredMethods -> TryGet -> factory().
        var a = "Ns.Type." + nameof(Definitions_lowers_every_registered_scenario_once) + ".A";
        var b = "Ns.Type." + nameof(Definitions_lowers_every_registered_scenario_once) + ".B";
        ScenarioRegistry.Register(a, () => Empty(a));
        ScenarioRegistry.Register(b, () => Empty(b));

        var names = ScenarioRegistry.Definitions().Select(d => d.MethodName).ToList();

        Assert.Contains(a, names);
        Assert.Contains(b, names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
