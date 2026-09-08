using System.Reflection;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The <c>[Scenario]</c> authoring attribute used to live in <c>Raun.Xunit</c> and derived from
/// xUnit's <c>FactAttribute</c> so xUnit's discovery could find it. Discovery is Raun's own now, so
/// the attribute is a plain marker the core ships (in namespace <c>Raun</c>, so authoring stays
/// <c>using Raun;</c>, and against <c>Raun</c> alone). The source generator matches it by metadata name and reads the
/// display-name constructor argument and the <c>Timeout</c> named argument, so the shape must be
/// preserved: first ctor parameter is the display name, plus a <c>Timeout</c> (ms) property.
/// </summary>
public class ScenarioAttributeTests
{
    private static Type ScenarioAttributeType => typeof(global::Raun.ScenarioAttribute);

    [Fact]
    public void Lives_in_Raun_namespace_so_authoring_is_using_Raun()
    {
        Assert.Equal("Raun", ScenarioAttributeType.Namespace);
    }

    [Fact]
    public void Is_a_plain_attribute_not_an_xunit_fact()
    {
        // Raun discovers scenarios itself; the attribute must not drag in xUnit's FactAttribute
        // (which the core does not even reference).
        Assert.True(typeof(Attribute).IsAssignableFrom(ScenarioAttributeType));

        for (var t = ScenarioAttributeType.BaseType; t is not null; t = t.BaseType)
        {
            Assert.NotEqual("Xunit.FactAttribute", t.FullName);
        }
    }

    [Fact]
    public void Only_targets_methods()
    {
        var usage = ScenarioAttributeType.GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method, usage!.ValidOn);
    }

    [Fact]
    public void First_constructor_argument_is_the_display_name()
    {
        // The generator reads ConstructorArguments[0] as the scenario display name.
        var attr = new global::Raun.ScenarioAttribute("customer books an appointment");
        Assert.Equal("customer books an appointment", attr.ScenarioDisplayName);
    }

    [Fact]
    public void Exposes_a_timeout_named_property_in_milliseconds()
    {
        // The generator reads the named "Timeout" argument for the per-scenario timeout.
        var attr = new global::Raun.ScenarioAttribute("s") { Timeout = 1500 };
        Assert.Equal(1500, attr.Timeout);
    }
}
