using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Raun.Generator.Emit;
using Raun.Testing;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The contract between generated code and the runtime has a number. The generator writes it into
/// every registration; the runtime refuses a registration that states another.
/// </summary>
public partial class GeneratorContractTests
{
    [Fact]
    public void The_generator_and_the_runtime_agree_on_the_contract_number()
        => Assert.Equal(ScenarioRegistry.ContractVersion, ScenarioEmitter.ContractVersion);

    [Fact]
    public void Every_registration_states_the_contract_it_was_generated_for()
    {
        var registry = GeneratorHarness.RunGeneratedFiles(SampleSources.Dsl + SampleSources.LinearScenario)["RaunScenarios.g.cs"];

        var version = ScenarioEmitter.ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(
            $"global::Raun.ScenarioRegistry.Register({version}, \"Demo.BookingScenarios.Booking\", Scenario_Demo_BookingScenarios_Booking);",
            registry,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every runtime type the generated code names, across the snapshot scenarios, beside the
    /// contract number. A change here is a change to what generated code needs from the runtime:
    /// if code generated before it can no longer run on the new runtime, raise the number (in
    /// <c>ScenarioEmitter</c> and <c>ScenarioRegistry</c>) before accepting. It sees types only —
    /// a changed signature or meaning is still for the author to notice.
    /// </summary>
    [Fact]
    public void Contract_surface()
    {
        string[] sources =
        [
            ScenarioCaseGenerationTests.Source("[ScenarioCase(\"reader\", \"Alice\")] [ScenarioCase(\"anonymous\", \"Bob\")]"),
            SampleSources.Dsl + SampleSources.LinearScenario,
            SampleSources.Dsl + SampleSources.TupleScenario,
            SampleSources.Dsl + SampleSources.ArrayScenario,
            SampleSources.Dsl + SampleSources.RuntimeNameScenario,
            ReturnTitleTests.Source,
            SampleSources.ConditionalDsl + SampleSources.IfElseScenario,
            SampleSources.OutcomeDsl + SampleSources.SwitchScenario,
            SampleSources.ResourceDsl + SampleSources.ResourceScenario,
            StepClassTests.Clinic + StepClassTests.Cancellation,
            SampleSources.UsesDsl + SampleSources.UsesScenario,
        ];

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            foreach (var generated in GeneratorHarness.RunDriver(source).GetRunResult().Results.SelectMany(result => result.GeneratedSources))
            {
                foreach (Match match in RuntimeName().Matches(generated.SourceText.ToString()))
                {
                    names.Add(match.Value);
                }
            }
        }

        var surface = new StringBuilder()
            .Append("contract ").Append(ScenarioEmitter.ContractVersion).Append('\n');
        foreach (var name in names)
        {
            surface.Append(name).Append('\n');
        }

        Snapshot.Verify(surface.ToString(), directory: "Snapshots");
    }

    [GeneratedRegex(@"global::Raun(\.[A-Za-z_]\w*)+")]
    private static partial Regex RuntimeName();
}
