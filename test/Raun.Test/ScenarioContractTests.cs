using Raun.Model;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Generated code states the contract it was generated for. A registration made for another
/// contract is refused, and listing the scenarios then fails with what to rebuild — instead of the
/// MissingMethodException, or the quietly different behaviour, that running it would give.
/// </summary>
public class ScenarioContractTests
{
    private static ScenarioDefinition Empty(string method) => new()
    {
        ScenarioId = method,
        DisplayName = method,
        MethodName = method,
        Namespace = "", TypeName = "",
        Nodes = [],
    };

    [Fact]
    public void A_registration_for_this_contract_is_listed()
    {
        var catalog = new ScenarioCatalog(contractVersion: 4);

        catalog.Register(4, "Ns.Type.Current", () => Empty("Ns.Type.Current"));

        Assert.Equal("Ns.Type.Current", Assert.Single(catalog.Definitions()).MethodName);
        Assert.Equal(["Ns.Type.Current"], catalog.Keys);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void A_registration_for_another_contract_is_refused_and_listing_says_what_to_rebuild(int generatedFor)
    {
        var catalog = new ScenarioCatalog(contractVersion: 4);
        var built = false;
        catalog.Register(4, "Ns.Type.Current", () => Empty("Ns.Type.Current"));

        catalog.Register(generatedFor, "Ns.Type.Other", () => { built = true; return Empty("Ns.Type.Other"); });
        catalog.Register(generatedFor, "Ns.Type.Other2", () => { built = true; return Empty("Ns.Type.Other2"); });

        Assert.False(catalog.TryGet("Ns.Type.Other", out _));
        Assert.Equal(["Ns.Type.Current"], catalog.Keys);
        var refusal = Assert.Throws<ScenarioContractException>(() => catalog.Definitions());
        Assert.False(built); // nothing generated for another contract is ever run, not even its factory
        var assembly = typeof(ScenarioContractTests).Assembly.GetName().Name!;
        Assert.Contains($"'{assembly}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"contract {generatedFor}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("2 scenarios", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("this Raun is contract 4", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Rebuild", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registration_made_by_hand_states_no_contract_and_is_listed()
    {
        // A host or a test that builds its own definitions compiles against this runtime's model
        // directly; there is no generated code between them to be out of date.
        var catalog = new ScenarioCatalog(contractVersion: 4);

        catalog.Register("Ns.Type.ByHand", () => Empty("Ns.Type.ByHand"));

        Assert.Single(catalog.Definitions());
    }

    [Fact]
    public void The_process_registry_takes_generated_registrations_for_its_own_contract()
    {
        const string key = "Ns.Type.GeneratedForThisRuntime";

        ScenarioRegistry.Register(ScenarioRegistry.ContractVersion, key, () => Empty(key));

        Assert.True(ScenarioRegistry.TryGet(key, out _));
    }
}
