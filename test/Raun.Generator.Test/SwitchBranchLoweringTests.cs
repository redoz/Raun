using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// `switch (await S)`: one arm per section, chosen by the compiler's own switch over the recorded
/// value; pattern variables are case-node outputs; `when` clauses lower like step arguments and their
/// step reads become the condition's dependencies; no match leaves every arm not taken.
/// </summary>
public class SwitchBranchLoweringTests
{
    private static GeneratorResult Generate() => GeneratorHarness.Run(SampleSources.OutcomeDsl + SampleSources.SwitchScenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string method)
        => result.Definitions().Single(d => d.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public void A_switch_has_one_arm_per_section_labelled_as_written()
    {
        var result = Generate();
        result.AssertCompiles();
        var condition = Scenario(result, "Route").Nodes.First(n => n.OperationName == "Submit");

        Assert.Equal(
            ["case Accepted { Total: > 1000 } big", "case Accepted accepted when accepted.Express", "case Rejected rejected", "default"],
            condition.Arms);
    }

    [Fact]
    public async Task The_first_matching_section_runs_with_its_variables()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Route").RunAsync();

        var review = results.Single(r => r.Node.OperationName == "Review");
        Assert.Equal(StepStatus.Passed, review.Status);
        Assert.Equal("review of 5000.00", review.DisplayName);
        Assert.All(
            results.Where(r => r.Node.OperationName is "Shipped" or "Told" or "StillPending"),
            r => Assert.Equal(StepStatus.NotTaken, r.Status));
    }

    [Fact]
    public async Task No_match_leaves_every_arm_not_taken_and_the_scenario_continues()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "NoMatch").RunAsync();

        Assert.All(results.Where(r => r.Node.OperationName is "Shipped" or "Told"),
            r => Assert.Equal("not taken: Submit matched no arm", r.SkipReason));
        Assert.Equal(StepStatus.Passed, results.Last(r => r.Node.OperationName == "StillPending").Status);
    }

    [Fact]
    public async Task A_local_reassigned_in_some_arms_merges_n_ways()
    {
        var result = Generate();
        result.AssertCompiles();
        var def = Scenario(result, "Merge");

        var merge = def.Nodes.Single(n => n.OperationName == "Merge");
        Assert.Equal(2, merge.MergeSources.Count);
        var results = await def.RunAsync();
        Assert.Equal("shipment S3 scheduled", results.Single(r => r.Node.OperationName == "Shipped").DisplayName);
    }

    [Fact]
    public async Task A_when_clause_reading_an_earlier_step_makes_the_condition_depend_on_it()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class WhenDependencyScenarios : OutcomeScenarios
            {
                [Scenario("when reads an earlier step")]
                public async Task Run()
                {
                    var first = await When.Submit("ok");
                    await Then.StillPending();
                    switch (await When.Submit("fast"))
                    {
                        case Accepted a when a.Shipment != ((Accepted)first).Shipment:
                            await Then.Shipped(a.Shipment);
                            break;
                    }
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        var def = result.Definitions().Single();
        var first = def.Nodes.First(n => n.OperationName == "Submit");
        var condition = def.Nodes.Last(n => n.OperationName == "Submit");

        Assert.Contains(first.Index, condition.DependsOn);
        Assert.Equal(StepStatus.Passed, (await def.RunAsync()).Single(r => r.Node.OperationName == "Shipped").Status);
    }

    [Theory]
    [InlineData("case Rejected r: return;", "RAUN003")]
    [InlineData("case Rejected r: throw new System.Exception();", "RAUN003")]
    [InlineData("case Rejected r when (await When.Submit(\"x\")) is Pending: break;", "RAUN007")]
    public async Task A_refused_section_is_reported_once(string section, string id)
    {
        var source = SampleSources.OutcomeDsl +
            $$"""

            public sealed class RefusedSwitchScenarios : OutcomeScenarios
            {
                [Scenario("refused switch")]
                public async Task Run()
                {
                    switch (await When.Submit("no"))
                    {
                        {{section}}
                        default: break;
                    }
                }
            }
            """;

        Assert.Single(await GeneratorHarness.DiagnoseAsync(source), d => d.Id == id);
    }

    [Fact]
    public async Task A_switch_on_something_other_than_an_awaited_step_is_RAUN011()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class NotAStepScenarios : OutcomeScenarios
            {
                [Scenario("switch on a constant")]
                public async Task Run()
                {
                    var outcome = await When.Submit("no");
                    switch (outcome)
                    {
                        default: await Then.StillPending(); break;
                    }
                }
            }
            """;

        Assert.Single(await GeneratorHarness.DiagnoseAsync(source), d => d.Id == "RAUN011");
    }
}
