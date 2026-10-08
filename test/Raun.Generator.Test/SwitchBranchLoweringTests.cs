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

    /// <summary>One scenario, <c>Run</c>, with <paramref name="body"/> as its body.</summary>
    private static string OneScenario(string body) => SampleSources.OutcomeDsl +
        $$"""

        public sealed class OneSwitchScenarios : OutcomeScenarios
        {
            [Scenario("one switch")]
            public async Task Run()
            {
                {{body}}
            }
        }
        """;

    private static async Task<IReadOnlyList<StepResult>> RunOne(string body)
    {
        var result = GeneratorHarness.Run(OneScenario(body));
        result.AssertCompiles();
        return await result.Definitions().Single().RunAsync();
    }

    [Theory]
    [InlineData("ok", "shipment S1 scheduled")]
    [InlineData("no", "shipment S3 scheduled")]
    public async Task Without_a_default_a_local_keeps_its_earlier_value_when_no_section_matches(string key, string shipped)
    {
        var body = $$"""
            var outcome = await When.Submit("ok");
            switch (await When.Submit("{{key}}"))
            {
                case Rejected:
                    outcome = await When.Submit("fast");
                    break;
            }

            await Then.Shipped(((Accepted)outcome).Shipment);
            """;

        var result = GeneratorHarness.Run(OneScenario(body));
        result.AssertCompiles();
        var def = result.Definitions().Single();
        var merge = def.Nodes.Single(n => n.OperationName == "Merge");
        Assert.Equal(2, merge.MergeSources.Count);
        Assert.Contains(def.Nodes, n => n.Guards.Any(g => g.Arm == Guard.NoArm));

        var results = await def.RunAsync();
        var reader = results.Single(r => r.Node.OperationName == "Shipped");
        Assert.Equal(StepStatus.Passed, reader.Status);
        Assert.Equal(shipped, reader.DisplayName);
    }

    [Theory]
    [InlineData("no", "shipment S3 scheduled")]
    [InlineData("ok", "shipment S1 scheduled")]
    public async Task With_a_default_the_arm_that_ran_decides_what_a_merged_local_holds(string key, string shipped)
    {
        // "ok" takes the default, which leaves the local alone: the reader sees the earlier value.
        var results = await RunOne($$"""
            var outcome = await When.Submit("ok");
            switch (await When.Submit("{{key}}"))
            {
                case Rejected:
                    outcome = await When.Submit("fast");
                    break;
                default:
                    break;
            }

            await Then.Shipped(((Accepted)outcome).Shipment);
            """);

        Assert.Equal(shipped, results.Single(r => r.Node.OperationName == "Shipped").DisplayName);
    }

    [Fact]
    public async Task A_section_written_as_a_block_ending_in_break_is_an_arm()
    {
        var results = await RunOne("""
            switch (await When.Submit("no"))
            {
                case Rejected rejected:
                {
                    await Then.Told(rejected.Reason);
                    break;
                }
                default:
                {
                    await Then.StillPending();
                    break;
                }
            }
            """);

        Assert.Equal(StepStatus.Passed, results.Single(r => r.Node.OperationName == "Told").Status);
        Assert.Equal(StepStatus.NotTaken, results.Single(r => r.Node.OperationName == "StillPending").Status);
    }

    [Theory]
    [InlineData("ok", "Shipped")]
    [InlineData("pending", "StillPending")]
    [InlineData("no", "Told")]
    public async Task A_default_in_the_middle_is_taken_only_when_nothing_else_matches(string key, string ran)
    {
        var results = await RunOne($$"""
            switch (await When.Submit("{{key}}"))
            {
                case Rejected rejected:
                    await Then.Told(rejected.Reason);
                    break;
                default:
                    await Then.StillPending();
                    break;
                case Accepted accepted:
                    await Then.Shipped(accepted.Shipment);
                    break;
            }
            """);

        Assert.All(
            results.Where(r => r.Node.OperationName is "Shipped" or "StillPending" or "Told"),
            r => Assert.Equal(r.Node.OperationName == ran ? StepStatus.Passed : StepStatus.NotTaken, r.Status));
    }

    [Theory]
    [InlineData("no", StepStatus.Passed)]
    [InlineData("pending", StepStatus.Passed)]
    [InlineData("ok", StepStatus.NotTaken)]
    public async Task A_section_with_several_labels_is_one_arm(string key, StepStatus pending)
    {
        var source = OneScenario($$"""
            switch (await When.Submit("{{key}}"))
            {
                case Rejected:
                case Pending:
                    await Then.StillPending();
                    break;
                case Accepted accepted:
                    await Then.Shipped(accepted.Shipment);
                    break;
            }
            """);
        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        var def = result.Definitions().Single();

        Assert.Equal(["case Rejected case Pending", "case Accepted accepted"], def.Nodes.First(n => n.OperationName == "Submit").Arms);
        var results = await def.RunAsync();
        Assert.Equal(pending, results.Single(r => r.Node.OperationName == "StillPending").Status);
    }

    [Fact]
    public void A_label_written_over_several_lines_is_shown_on_one()
    {
        var result = GeneratorHarness.Run(OneScenario("""
            switch (await When.Submit("fast"))
            {
                case Accepted accepted
                    when accepted.Express:
                    await Then.Shipped(accepted.Shipment);
                    break;
            }
            """));
        result.AssertCompiles();

        Assert.Equal(
            ["case Accepted accepted when accepted.Express"],
            result.Definitions().Single().Nodes.First(n => n.OperationName == "Submit").Arms);
    }

    [Theory]
    [InlineData("case int n:")]
    [InlineData("case var n:")]
    public void An_exhaustive_switch_without_a_default_generates_no_warning(string last)
    {
        var source = OneScenario($$"""
            switch (await When.Count("ab"))
            {
                case 0:
                    await Then.StillPending();
                    break;
                {{last}}
                    await Then.Counted(n);
                    break;
            }
            """);
        GeneratorHarness.Run(source).AssertCompiles();

        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
    }

    [Fact]
    public async Task A_variable_declared_by_a_when_clause_is_RAUN007_once()
    {
        // Not supported this release: only the case pattern's own variables are step outputs.
        var diagnostics = await GeneratorHarness.DiagnoseAsync(OneScenario("""
            switch (await When.Submit("ok"))
            {
                case Accepted accepted when accepted.Shipment is string shipment:
                    await Then.Shipped(shipment);
                    break;
            }
            """));

        var raun007 = Assert.Single(diagnostics, d => d.Id == "RAUN007");
        Assert.Contains("'shipment'", raun007.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task Under_RAUN011_a_when_clause_problem_is_reported_in_the_same_build()
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(OneScenario("""
            var outcome = await When.Submit("no");
            switch (outcome)
            {
                case Rejected r when (await When.Submit("x")) is Pending:
                    break;
                default:
                    break;
            }
            """));

        Assert.Single(diagnostics, d => d.Id == "RAUN011");
        Assert.Single(diagnostics, d => d.Id == "RAUN007");
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

    [Theory]
    [InlineData("", "nothing")]       // 0
    [InlineData("abc", "many")]       // 150
    [InlineData("a", "some 50")]      // 50
    public async Task A_switch_on_a_value_type_result_picks_the_arm_and_binds_unboxed(string id, string expected)
    {
        var source = SampleSources.OutcomeDsl +
            $$"""

            public sealed class CountScenarios : OutcomeScenarios
            {
                [Scenario("count")]
                public async Task Run()
                {
                    switch (await When.Count("{{id}}"))
                    {
                        case 0:
                            await Then.Told("nothing");
                            break;
                        case > 100:
                            await Then.Told("many");
                            break;
                        case int n:
                            await Then.Told("some " + n);
                            break;
                    }
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        var ran = Assert.Single(results, r => r.Node.OperationName == "Told" && r.Status == StepStatus.Passed);
        Assert.Equal("customer told " + expected, ran.DisplayName);
    }
}
