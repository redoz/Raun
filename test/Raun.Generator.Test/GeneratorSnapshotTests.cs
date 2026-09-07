using System.Threading.Tasks;
using VerifyXunit;
using Xunit;
using static VerifyXunit.Verifier;

namespace Raun.Generator.Test;

/// <summary>
/// Snapshots of the generated source so the lowering output stays easy to review and changes are
/// caught. Behavioral correctness is covered by the *LoweringTests; these guard the exact shape.
/// </summary>
public class GeneratorSnapshotTests
{
    [Fact]
    public Task Linear_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.LinearScenario))
            .UseDirectory("Snapshots");

    [Fact]
    public Task Tuple_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.TupleScenario))
            .UseDirectory("Snapshots");

    [Fact]
    public Task Array_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.ArrayScenario))
            .UseDirectory("Snapshots");

    /// <summary>Pins the runtime display-name formatter's shape: a string concatenation of literals
    /// and parenthesized <c>__inputs.Get&lt;…&gt;(i)</c> holes, not a hand-escaped interpolation.</summary>
    [Fact]
    public Task RuntimeName_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.RuntimeNameScenario))
            .UseDirectory("Snapshots");

    [Fact]
    public Task PathBearing_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs"))
            .UseDirectory("Snapshots");

    [Fact]
    public Task EntryPoint() =>
        Verify(GeneratorHarness.RunGeneratedFiles(
                SampleSources.Dsl + SampleSources.LinearScenario)["RaunProgram.g.cs"])
            .UseDirectory("Snapshots");

    [Fact]
    public Task Conditional_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.ConditionalDsl + SampleSources.IfElseScenario))
            .UseDirectory("Snapshots");

    [Fact]
    public Task Resource_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.ResourceDsl + SampleSources.ResourceScenario))
            .UseDirectory("Snapshots");

    [Fact]
    public Task Uses_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.UsesDsl + SampleSources.UsesScenario))
            .UseDirectory("Snapshots");
}
