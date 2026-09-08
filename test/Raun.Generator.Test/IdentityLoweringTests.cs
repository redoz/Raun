using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator has the declaring symbols, so the definition carries the namespace and type name
/// directly instead of leaving every adapter to split <c>MethodName</c> on dots.
/// </summary>
public class IdentityLoweringTests
{
    [Fact]
    public void A_scenario_carries_its_namespace_and_declaring_type_name()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());

        Assert.Equal("Demo", def.Namespace);
        Assert.Equal("BookingScenarios", def.TypeName);
        Assert.Equal("Demo.BookingScenarios.Booking", def.MethodName);
    }

    [Fact]
    public void A_nested_declaring_type_is_joined_with_plus_while_the_method_name_stays_dotted()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.NestedTypeScenario);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());

        Assert.Equal("Demo", def.Namespace);
        Assert.Equal("Outer+Inner", def.TypeName);
        Assert.Equal("Demo.Outer.Inner.Run", def.MethodName);
    }
}
