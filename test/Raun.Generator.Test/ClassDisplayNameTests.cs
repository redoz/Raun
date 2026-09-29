using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A [DisplayName] on the scenario's declaring class flows into
/// ScenarioDefinition.ClassDisplayName; without it the value is null.</summary>
public class ClassDisplayNameTests
{
    private const string WithDisplayName =
        """

        [System.ComponentModel.DisplayName("Appointment booking")]
        public sealed class NamedScenarios : AppointmentSuite
        {
            [Scenario("booking")]
            public async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                await Then.Greet(patient);
            }
        }
        """;

    [Fact]
    public void DisplayName_attribute_sets_ClassDisplayName()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + WithDisplayName);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        Assert.Equal("Appointment booking", def.ClassDisplayName);
    }

    [Fact]
    public void Absent_DisplayName_leaves_ClassDisplayName_null()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        Assert.Null(def.ClassDisplayName);
    }
}
