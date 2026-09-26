using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Guards the invariant the framework rests on: a scenario that compiles is either lowered or
/// diagnosed — never silently dropped. <c>ScenarioAwaiters</c> decides what compiles, the analyzer
/// what is diagnosed, and <c>ScenarioParser</c> what is lowered; they are maintained by hand and can
/// drift (an awaiter overload once made <c>await ….ToArray();</c> compile while the parser still
/// rejected it, and the scenario vanished from the test list). Every sample scenario is compiled and
/// its definitions counted, and every <c>*Scenario</c> constant must be listed here so a new sample
/// cannot dodge the check.
/// </summary>
public class LoweringCoverageTests
{
    private static readonly Dictionary<string, string> DslFor = new()
    {
        [nameof(SampleSources.NamedArgScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.RuntimeNameScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.LinearScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.TupleScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.ArrayScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.LinqScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.TeardownOnSuccessScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.GenericStepScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.VoidTupleScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.VoidArrayScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.VoidLinqScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.EmptyArrayScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.EmptyLinqScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.NestedTypeScenario)] = SampleSources.Dsl,
        [nameof(SampleSources.ResourceScenario)] = SampleSources.ResourceDsl,
        [nameof(SampleSources.BookingScenario)] = SampleSources.ResourceDsl,
        [nameof(SampleSources.LineageScenario)] = SampleSources.ResourceDsl,
        [nameof(SampleSources.IfElseScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.BareIfScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.ConditionalOverwriteScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.NestedIfScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.OperatorTrueScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.ElseIfChainScenario)] = SampleSources.ConditionalDsl,
        [nameof(SampleSources.TeardownScenario)] = SampleSources.TeardownDsl,
        [nameof(SampleSources.UsesScenario)] = SampleSources.UsesDsl,
        [nameof(SampleSources.UsesFreeScenario)] = SampleSources.UsesDsl,
        [nameof(SampleSources.UsesSitesScenario)] = SampleSources.UsesDsl,
        [nameof(SampleSources.CompositionScenario)] = SampleSources.CompositionDsl,
    };

    private static IEnumerable<string> ScenarioConstants() =>
        typeof(SampleSources)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Scenario", StringComparison.Ordinal))
            .Select(f => f.Name);

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var name in ScenarioConstants())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void Every_sample_scenario_constant_is_paired_with_its_dsl()
    {
        var unlisted = ScenarioConstants().Where(name => !DslFor.ContainsKey(name)).ToList();

        Assert.True(unlisted.Count == 0, "add these SampleSources constants to DslFor: " + string.Join(", ", unlisted));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_compilable_sample_scenario_is_lowered_not_dropped(string name)
    {
        var scenario = (string)typeof(SampleSources).GetField(name)!.GetRawConstantValue()!;
        var result = GeneratorHarness.Run(DslFor[name] + scenario);
        result.AssertCompiles();

        var declared = Regex.Count(scenario, @"\[Scenario\(", RegexOptions.None, TimeSpan.FromSeconds(1));

        Assert.Equal(declared, result.Definitions().Count);
    }
}
