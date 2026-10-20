using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator collects every [Tag(key, value)] a scenario is subject to — the scenario method,
/// its containing classes, the assembly, and any attribute whose own class carries [Tag]s — onto
/// ScenarioDefinition.Tags, assembly first and the scenario method last, without duplicates.
/// A scenario subject to none gets no Tags initializer at all.
/// </summary>
public class TagLoweringTests
{
    private static string WithAssemblyTags(string assemblyAttributes, string scenario)
        => SampleSources.Dsl.Replace("namespace Demo;", assemblyAttributes + "\nnamespace Demo;", StringComparison.Ordinal)
            + scenario;

    private static ScenarioDefinition Lower(string source)
    {
        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        return result.Definitions().Single();
    }

    private static IEnumerable<string> Pairs(ScenarioDefinition definition)
        => definition.Tags.Select(t => t.Key + "=" + t.Value);

    [Fact]
    public void Tags_from_assembly_class_and_method_are_collected_outermost_first()
    {
        var definition = Lower(WithAssemblyTags(
            """[assembly: Tag("suite", "appointments")]""",
            """

            [Tag("area", "booking")]
            public sealed class TaggedScenarios : AppointmentSuite
            {
                [Scenario("booking")]
                [Tag("owner", "payments")]
                [Tag("area", "billing")]
                public async Task Booking()
                {
                    await Given.PatientExists("Jane");
                }
            }
            """));

        Assert.Equal(["suite=appointments", "area=booking", "owner=payments", "area=billing"], Pairs(definition));
    }

    [Fact]
    public void Nested_classes_contribute_outer_class_first()
    {
        var definition = Lower(SampleSources.Dsl +
            """

            [Tag("level", "outer")]
            public static class Outer
            {
                [Tag("level", "inner")]
                public sealed class TaggedScenarios : AppointmentSuite
                {
                    [Scenario("booking")]
                    public async Task Booking()
                    {
                        await Given.PatientExists("Jane");
                    }
                }
            }
            """);

        Assert.Equal(["level=outer", "level=inner"], Pairs(definition));
    }

    [Fact]
    public void A_custom_attribute_carrying_tags_expands_to_those_tags()
    {
        var definition = Lower(SampleSources.Dsl +
            """

            [Tag("area", "auth")]
            [Tag("owner", "identity")]
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class AuthorizationAttribute : System.Attribute;

            [Tag("speed", "slow")]
            [Authorization]
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class SlowAuthorizationAttribute : System.Attribute;

            public sealed class TaggedScenarios : AppointmentSuite
            {
                [Scenario("login")]
                [SlowAuthorization]
                public async Task Login()
                {
                    await Given.PatientExists("Jane");
                }
            }
            """);

        Assert.Equal(["speed=slow", "area=auth", "owner=identity"], Pairs(definition));
    }

    [Fact]
    public void A_tag_reached_twice_is_kept_once_at_its_first_position()
    {
        var definition = Lower(SampleSources.Dsl +
            """

            [Tag("area", "auth")]
            [System.AttributeUsage(System.AttributeTargets.All)]
            public sealed class AuthorizationAttribute : System.Attribute;

            [Tag("area", "auth")]
            public sealed class TaggedScenarios : AppointmentSuite
            {
                [Scenario("login")]
                [Authorization]
                [Tag("Area", "Auth")]
                public async Task Login()
                {
                    await Given.PatientExists("Jane");
                }
            }
            """);

        Assert.Equal(["area=auth"], Pairs(definition));
    }

    [Fact]
    public void A_scenario_with_no_tags_emits_no_Tags_initializer()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        Assert.DoesNotContain("Tags =", result.GeneratedSource, StringComparison.Ordinal);
        Assert.Empty(result.Definitions().Single().Tags);
    }
}
