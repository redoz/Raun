using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator unions every [Uses&lt;T&gt;] a scenario is subject to — its steps' DSL methods, the
/// scenario method, its classes, the assembly — onto ScenarioDefinition.Uses, one entry per type
/// with Exclusive winning. A scenario subject to none gets no Uses initializer at all.
/// </summary>
public class UsesLoweringTests
{
    private static ScenarioDefinition Lower(string scenario)
    {
        var result = GeneratorHarness.Run(SampleSources.UsesDsl + scenario);
        result.AssertCompiles();
        return result.Definitions().Single();
    }

    [Fact]
    public void Uses_from_every_site_are_unioned_and_reduced_per_type()
    {
        var definition = Lower(SampleSources.UsesScenario);

        var uses = definition.Uses.OrderBy(u => u.Resource.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Audit", "Database", "Smtp"], uses.Select(u => u.Resource.Name));
        Assert.Equal(LockMode.Shared, uses[0].Mode);     // assembly
        Assert.Equal(LockMode.Exclusive, uses[1].Mode);  // step's Exclusive beats class's Shared
        Assert.Equal(LockMode.Shared, uses[2].Mode);     // scenario method + step, both Shared
        Assert.All(uses, u => Assert.True(typeof(IContendedResource).IsAssignableFrom(u.Resource)));
    }

    [Fact]
    public void Only_the_assembly_and_the_called_steps_count_for_a_scenario_without_its_own_uses()
    {
        var definition = Lower(SampleSources.UsesFreeScenario);

        var use = Assert.Single(definition.Uses);
        Assert.Equal("Audit", use.Resource.Name);
        Assert.Equal(LockMode.Shared, use.Mode);
    }

    [Fact]
    public void Scenario_method_and_containing_class_sites_are_collected_on_their_own()
    {
        var definition = Lower(SampleSources.UsesSitesScenario);

        var uses = definition.Uses.OrderBy(u => u.Resource.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Audit", "Cache", "Queue"], uses.Select(u => u.Resource.Name));
        Assert.Equal(LockMode.Shared, uses[0].Mode);     // assembly
        Assert.Equal(LockMode.Shared, uses[1].Mode);     // containing class only
        Assert.Equal(LockMode.Exclusive, uses[2].Mode);  // scenario method only
    }

    [Fact]
    public void A_scenario_subject_to_no_uses_emits_no_Uses_initializer()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        Assert.DoesNotContain("Uses =", result.GeneratedSource, StringComparison.Ordinal);
        Assert.Empty(result.Definitions().Single().Uses);
    }
}
