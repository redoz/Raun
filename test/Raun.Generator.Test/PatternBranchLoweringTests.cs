using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// `if (await S is P)`: the condition is the step; the pattern picks the arm (arm 0 matches, arm 1
/// is the else); the pattern's variables are step outputs inside the arm that binds them, read
/// through a hidden case node.
/// </summary>
public class PatternBranchLoweringTests
{
    private static GeneratorResult Generate() => GeneratorHarness.Run(SampleSources.OutcomeDsl + SampleSources.IsPatternScenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string method)
        => result.Definitions().Single(d => d.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public void An_is_pattern_condition_has_two_arms_labelled_by_its_pattern()
    {
        var result = Generate();
        result.AssertCompiles();
        var condition = Scenario(result, "Rejection").Nodes.Single(n => n.OperationName == "Submit");

        Assert.NotNull(condition.SelectArm);
        Assert.Equal(["is Rejected rejected", "else"], condition.Arms);
    }

    [Fact]
    public void A_pattern_variable_is_read_through_a_case_node_guarded_on_its_arm()
    {
        var def = Scenario(Generate(), "Rejection");
        var told = def.Nodes.Single(n => n.OperationName == "Told");
        var caseNode = def.Nodes.Single(n => n.IsSynthetic && n.OperationName == "Case");

        Assert.Equal([new Guard(caseNode.DependsOn[0], 0)], caseNode.Guards.Where(g => g.ConditionIndex == caseNode.DependsOn[0]));
        Assert.Contains(caseNode.Index, told.DependsOn);
    }

    [Fact]
    public async Task The_matching_arm_runs_with_the_bound_value_and_the_other_is_not_taken()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Rejection").RunAsync();

        var told = results.Single(r => r.Node.OperationName == "Told");
        Assert.Equal(StepStatus.Passed, told.Status);
        Assert.Equal("customer told out of stock", told.DisplayName);
        var pending = results.Single(r => r.Node.OperationName == "StillPending");
        Assert.Equal(StepStatus.NotTaken, pending.Status);
        Assert.Equal("not taken: Submit took is Rejected rejected", pending.SkipReason);
    }

    [Fact]
    public async Task An_is_not_pattern_binds_its_variable_in_the_else_arm()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "NotRejected").RunAsync();

        Assert.Equal(StepStatus.Passed, results.Single(r => r.Node.OperationName == "Shipped").Status);
        Assert.Equal("shipment S1 scheduled", results.Single(r => r.Node.OperationName == "Shipped").DisplayName);
        Assert.Equal(StepStatus.NotTaken, results.Single(r => r.Node.OperationName == "StillPending").Status);
    }

    private static GeneratorResult GenerateShapes() => GeneratorHarness.Run(SampleSources.OutcomeDsl + SampleSources.IsPatternShapesScenario);

    private static async Task<IReadOnlyList<StepResult>> RunShape(string method)
    {
        var result = GenerateShapes();
        result.AssertCompiles();
        return await Scenario(result, method).RunAsync();
    }

    [Theory]
    [InlineData("Fallback", "shipment F1 scheduled")]
    [InlineData("NoFallback", "shipment S1 scheduled")]
    public async Task A_pattern_variable_assigned_in_the_other_arm_is_merged_after_the_branch(string method, string shipped)
    {
        var results = await RunShape(method);

        var step = results.Single(r => r.Node.OperationName == "Shipped");
        Assert.Equal(StepStatus.Passed, step.Status);
        Assert.Equal(shipped, step.DisplayName);
    }

    [Fact]
    public async Task A_pattern_variable_reassigned_in_its_own_arm_passes_through_the_inner_branch()
    {
        var results = await RunShape("Rechecked");

        var told = results.Single(r => r.Node.OperationName == "Told");
        Assert.Equal(StepStatus.Passed, told.Status);
        Assert.Equal("customer told recalled", told.DisplayName);
    }

    [Fact]
    public async Task A_parenthesized_not_binds_its_variable_in_the_else_arm()
    {
        var results = await RunShape("ParenthesizedNot");

        var shipped = results.Single(r => r.Node.OperationName == "Shipped");
        Assert.Equal(StepStatus.Passed, shipped.Status);
        Assert.Equal("shipment S1 scheduled", shipped.DisplayName);
        Assert.Equal(StepStatus.NotTaken, results.Single(r => r.Node.OperationName == "StillPending").Status);
    }

    [Fact]
    public async Task A_pattern_binding_several_variables_reads_each_from_one_case_node()
    {
        var results = await RunShape("BigOrder");

        Assert.Single(results, r => r.Node.OperationName == "Case");
        var review = results.Single(r => r.Node.OperationName == "Review");
        Assert.Equal(StepStatus.Passed, review.Status);
        Assert.Equal("review of 5000.00", review.DisplayName);
        var shipped = results.Single(r => r.Node.OperationName == "Shipped");
        Assert.Equal(StepStatus.Passed, shipped.Status);
        Assert.Equal("shipment S2 scheduled", shipped.DisplayName);
    }

    [Fact]
    public void A_pattern_declaring_no_variables_adds_no_case_node()
    {
        var def = Scenario(GenerateShapes(), "Waiting");

        Assert.DoesNotContain(def.Nodes, n => n.OperationName == "Case");
        Assert.Equal(["is Pending", "else"], def.Nodes.Single(n => n.OperationName == "Submit").Arms);
    }

    [Fact]
    public async Task One_of_several_pattern_variables_is_refused_as_a_merge_source_with_the_reason()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class SeveralScenarios : OutcomeScenarios
            {
                [Scenario("several")]
                public async Task Run()
                {
                    if (await When.Submit("no") is not Accepted { Shipment: var shipment, Total: var total })
                        shipment = await When.Reroute();
                    else
                        await Then.Review(total);
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN017", diagnostic.Id);
        Assert.Contains("'shipment' is one of several variables bound by one pattern", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_compound_condition_is_refused_with_the_reason()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class CompoundScenarios : OutcomeScenarios
            {
                [Scenario("compound")]
                public async Task Run()
                {
                    if (await When.Submit("no") is Rejected && true)
                        await Then.StillPending();
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN011", diagnostic.Id);
    }
}
