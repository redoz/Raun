using System.Reflection;
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// The declaration surface for cross-scenario contention: token types carry exactly one kind
/// attribute, [Uses&lt;T&gt;] declares a need with a mode, and a definition carries its reduced uses.
/// </summary>
public class ContendedResourceAttributeTests
{
    [Fact]
    public void Kind_attributes_target_types_only_and_are_not_inherited()
    {
        foreach (var kind in new[] { typeof(ExclusiveResourceAttribute), typeof(SharedResourceAttribute), typeof(PooledResourceAttribute) })
        {
            var usage = kind.GetCustomAttribute<AttributeUsageAttribute>();
            Assert.NotNull(usage);
            Assert.Equal(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, usage.ValidOn);
            Assert.False(usage.Inherited);
        }
    }

    [Fact]
    public void Pooled_resource_exposes_its_capacity()
    {
        var attr = typeof(PooledSmtp).GetCustomAttribute<PooledResourceAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(2, attr.Capacity);
    }

    [Fact]
    public void Uses_targets_methods_classes_and_assemblies_and_allows_multiple()
    {
        var usage = typeof(UsesAttribute<>).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, usage.ValidOn);
        Assert.True(usage.AllowMultiple);
    }

    [Fact]
    public void A_bare_use_is_shared_and_a_mode_can_be_given()
    {
        Assert.Equal(LockMode.Shared, new UsesAttribute<SharedCatalog>().Mode);
        Assert.Equal(LockMode.Exclusive, new UsesAttribute<SharedCatalog>(LockMode.Exclusive).Mode);
    }

    [Fact]
    public void A_definition_has_no_uses_unless_given_some()
    {
        var bare = new ScenarioDefinition { ScenarioId = "s", DisplayName = "s", MethodName = "N.s", Nodes = [] };
        Assert.Empty(bare.Uses);

        var declared = new ScenarioDefinition
        {
            ScenarioId = "s", DisplayName = "s", MethodName = "N.s", Nodes = [],
            Uses = [new ContendedResourceUse(typeof(ExclusiveDb), LockMode.Exclusive)],
        };
        var use = Assert.Single(declared.Uses);
        Assert.Equal(typeof(ExclusiveDb), use.Resource);
        Assert.Equal(LockMode.Exclusive, use.Mode);
    }
}
