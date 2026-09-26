using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// `if`/`else` lowers into guarded nodes plus synthetic merge (phi) nodes. `DependsOn` keeps its
/// all-of meaning throughout: a following statement never depends on an arm's node, only on the
/// condition or on a merge — and it WAITS for every arm's last steps through `WaitsFor`, so nothing
/// after the `if` runs concurrently with what is inside it.
/// </summary>
public class ConditionalLoweringTests
{
    private static ScenarioDefinition Lower(string scenario)
    {
        var result = GeneratorHarness.Run(SampleSources.ConditionalDsl + scenario);
        result.AssertCompiles();
        return Assert.Single(result.Definitions());
    }

    [Fact]
    public void If_else_lowers_condition_arms_and_a_merge()
    {
        var def = Lower(SampleSources.IfElseScenario);

        // 0 PatientExists, 1 IsPriority (condition), 2 CreateUrgent, 3 CreateStandard,
        // 4 «merge appointment», 5 AppointmentExists
        Assert.Equal(7, def.Nodes.Count);

        Assert.NotNull(def.Nodes[1].EvaluateCondition);
        Assert.Equal([new Guard(1, true)], def.Nodes[2].Guards);
        Assert.Equal([new Guard(1, false)], def.Nodes[3].Guards);

        Assert.True(def.Nodes[4].IsSynthetic);
        Assert.Equal([2, 3], def.Nodes[4].MergeSources);
        Assert.Empty(def.Nodes[4].DependsOn);

        // The consumer joins on the merge, never on an arm.
        Assert.Equal([4], def.Nodes[5].DependsOn);
        Assert.Empty(def.Nodes[5].Guards);
    }

    [Fact]
    public void Condition_node_is_an_ordinary_discoverable_step()
    {
        var def = Lower(SampleSources.IfElseScenario);

        Assert.False(def.Nodes[1].IsSynthetic);
        Assert.Equal("IsPriority", def.Nodes[1].OperationName);
        Assert.Equal("the patient is priority", def.Nodes[1].DisplayNameTemplate);
        Assert.Equal("Given", def.Nodes[1].Phase);
    }

    [Fact]
    public void Same_named_locals_in_sibling_arms_are_two_locals_and_need_no_merge()
    {
        // Each arm declares its own `appointment`; nothing after the `if` can see either, so there
        // is nothing to merge. Keyed by name, the two looked like one local defined twice.
        var def = Lower(
            """

            public static class SiblingLocalScenarios
            {
                [Scenario("sibling locals")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Alice");
                    if (await Given.IsPriority())
                    {
                        var appointment = await When.CreateUrgent(patient);
                        await Then.AppointmentExists(appointment);
                    }
                    else
                    {
                        var appointment = await When.CreateStandard(patient);
                        await Then.AppointmentExists(appointment);
                    }
                }
            }
            """);

        Assert.DoesNotContain(def.Nodes, n => n.IsSynthetic);
    }

    [Fact]
    public void Bare_if_guards_the_arm_and_inserts_no_merge()
    {
        var def = Lower(SampleSources.BareIfScenario);

        // 0 PatientExists, 1 IsPriority, 2 Notify — nothing is assigned, so there is no phi.
        Assert.Equal(4, def.Nodes.Count);
        Assert.Equal([new Guard(1, true)], def.Nodes[2].Guards);
        Assert.DoesNotContain(def.Nodes, n => n.IsSynthetic);
    }

    [Fact]
    public void Conditional_overwrite_merges_the_arm_against_a_pass_through_of_the_parent()
    {
        var def = Lower(SampleSources.ConditionalOverwriteScenario);

        // 0 PatientExists, 1 CreateStandard (parent def), 2 IsPriority, 3 CreateUrgent (arm),
        // 4 pass-through of 1 guarded false, 5 «merge appointment», 6 AppointmentExists
        Assert.Equal(8, def.Nodes.Count);

        Assert.Equal([new Guard(2, true)], def.Nodes[3].Guards);

        Assert.True(def.Nodes[4].IsSynthetic);
        Assert.Equal([new Guard(2, false)], def.Nodes[4].Guards);
        Assert.Equal([1], def.Nodes[4].MergeSources);

        Assert.True(def.Nodes[5].IsSynthetic);
        Assert.Equal([3, 4], def.Nodes[5].MergeSources);
        Assert.Equal([5], def.Nodes[6].DependsOn);
    }

    [Fact]
    public void Nested_ifs_stack_guards()
    {
        var def = Lower(SampleSources.NestedIfScenario);

        // 0 PatientExists, 1 IsPriority, 2 HasCapacity, 3 Notify
        Assert.Equal([new Guard(1, true)], def.Nodes[2].Guards);
        Assert.Equal([new Guard(1, true), new Guard(2, true)], def.Nodes[3].Guards);
    }

    [Fact]
    public async Task If_arm_runs_and_else_arm_is_not_taken_end_to_end()
    {
        var result = GeneratorHarness.Run(SampleSources.ConditionalDsl + SampleSources.IfElseScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Equal(StepStatus.Passed, results[2].Status);      // CreateUrgent (IsPriority == true)
        Assert.Equal(StepStatus.NotTaken, results[3].Status);    // CreateStandard
        Assert.Equal(StepStatus.Passed, results[4].Status);      // merge
        Assert.Equal(StepStatus.Passed, results[5].Status);      // AppointmentExists
    }

    [Fact]
    public async Task Conditional_overwrite_executes_and_the_consumer_sees_the_arm_value()
    {
        var result = GeneratorHarness.Run(
            SampleSources.ConditionalDsl + SampleSources.ConditionalOverwriteScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.True(
            r.Status is StepStatus.Passed or StepStatus.NotTaken,
            $"step {r.Node.Index} was {r.Status}: {r.SkipReason}{r.Exception}"));
        Assert.Equal(StepStatus.NotTaken, results[4].Status);   // pass-through (condition was true)
        Assert.Equal(StepStatus.Passed, results[5].Status);     // merge took the arm value
    }

    [Fact]
    public async Task Operator_true_condition_gates_the_arm_end_to_end()
    {
        // Capacity is not bool; it defines `operator true`. The generator emits
        // `static o => ((Capacity)o!) ? true : false`, so Roslyn — not the scheduler — resolves it.
        var result = GeneratorHarness.Run(
            SampleSources.ConditionalDsl + SampleSources.OperatorTrueScenario);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.Equal(StepStatus.Passed, results[1].Status);   // HasCapacity
        Assert.Equal(StepStatus.Passed, results[2].Status);   // Notify ran: the guard held
    }

    [Fact]
    public async Task Else_if_chains_give_n_way_routing_without_switch_support()
    {
        // An `else if` is an IfStatementSyntax inside the else arm, so it recurses through ParseIf:
        // guards stack, merges chain, and exactly one arm runs. This is why a switch expression would
        // be ergonomics rather than new capability.
        var result = GeneratorHarness.Run(
            SampleSources.ConditionalDsl + SampleSources.ElseIfChainScenario);
        result.AssertCompiles();
        var def = Assert.Single(result.Definitions());

        var results = await def.RunAsync();

        Assert.All(results, r => Assert.True(
            r.Status is StepStatus.Passed or StepStatus.NotTaken,
            $"step {r.Node.Index} ({r.Node.OperationName}) was {r.Status}: {r.SkipReason}{r.Exception}"));

        // IsPriority is true, so exactly the first arm runs and the consumer still gets a value.
        Assert.Single(results, r => r.Node.OperationName == "CreateUrgent" && r.Status == StepStatus.Passed);
        Assert.Equal(StepStatus.Passed, results[^1].Status);
    }

    // -- Ordering after an if: WaitsFor ----------------------------------------------------------------
    //
    // Found by the resource conflict ledger through a packaged consumer: a statement after a bare `if`
    // depended on the condition only, so it could run concurrently with the arm. WaitsFor closes that
    // hole without giving DependsOn a not-taken cascade.

    private static ScenarioDefinition LowerBody(string body) =>
        Lower(
            $$"""

            public static class WaitScenarios
            {
                [Scenario("waits")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Jane");
            {{body}}
                }
            }
            """);

    [Fact]
    public void Statement_after_a_bare_if_waits_for_the_arm()
    {
        // 0 PatientExists, 1 IsPriority, 2 Notify (arm), 3 Notify (after the if), 4 Teardown
        var def = LowerBody(
            """
                    if (await Given.IsPriority())
                        await When.Notify(patient);

                    await When.Notify(patient);
            """);

        Assert.Equal([0, 1], def.Nodes[3].DependsOn);   // values: the patient; order: the condition
        Assert.Equal([2], def.Nodes[3].WaitsFor);       // and the arm's last step, ordering only
        Assert.Empty(def.Nodes[3].Guards);
        Assert.Empty(def.Nodes[2].WaitsFor);
    }

    [Fact]
    public void Statement_after_an_if_else_waits_for_both_arms()
    {
        // 0 PatientExists, 1 IsPriority, 2 CreateUrgent, 3 CreateStandard, 4 «merge», 5 AppointmentExists
        var def = Lower(SampleSources.IfElseScenario);

        Assert.Equal([4], def.Nodes[5].DependsOn);
        Assert.Equal([2, 3], def.Nodes[5].WaitsFor);
    }

    [Fact]
    public void Statement_after_an_arm_ending_in_a_parallel_group_waits_for_every_member()
    {
        // 0 PatientExists, 1 IsPriority, 2 CreateUrgent, 3 CreateStandard (parallel, branch-local), 4 Notify
        var def = LowerBody(
            """
                    if (await Given.IsPriority())
                    {
                        var (a, b) = await (When.CreateUrgent(patient), When.CreateStandard(patient));
                    }

                    await When.Notify(patient);
            """);

        Assert.Equal([2, 3], def.Nodes[4].WaitsFor);
    }

    [Fact]
    public void A_nested_if_propagates_its_arm_to_the_outer_statement()
    {
        // 0 PatientExists, 1 IsPriority, 2 HasCapacity (inner condition), 3 Notify (inner arm),
        // 4 Notify (after the outer if). The outer arm's tail is the inner condition PLUS the inner
        // arm the inner if left waiting, so the outer post-statement waits for both.
        var def = LowerBody(
            """
                    if (await Given.IsPriority())
                    {
                        if (await Given.HasCapacity())
                            await When.Notify(patient);
                    }

                    await When.Notify(patient);
            """);

        Assert.Equal([0, 1], def.Nodes[4].DependsOn);
        Assert.Equal([2, 3], def.Nodes[4].WaitsFor);
    }

    [Fact]
    public void Waits_do_not_leak_into_the_next_next_statement()
    {
        // Only the statement immediately after the if waits; the one after that joins on it as usual.
        var def = LowerBody(
            """
                    if (await Given.IsPriority())
                        await When.Notify(patient);

                    await When.Notify(patient);
                    await When.Notify(patient);
            """);

        Assert.Equal([2], def.Nodes[3].WaitsFor);
        Assert.Empty(def.Nodes[4].WaitsFor);
        Assert.Equal([0, 3], def.Nodes[4].DependsOn);
    }

    [Fact]
    public async Task Statement_after_a_bare_if_runs_after_the_arm_end_to_end()
    {
        var result = GeneratorHarness.Run(SampleSources.ConditionalDsl +
            """

            public static class WaitRunScenarios
            {
                [Scenario("notify then confirm")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Jane");

                    if (await Given.IsPriority())
                        await When.Notify(patient);

                    await When.Notify(patient);
                }
            }
            """);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        // 2 = arm Notify (IsPriority is true), 3 = the Notify after the if. Both pass, and the second
        // did not start before the first finished.
        Assert.Equal(StepStatus.Passed, results[2].Status);
        Assert.Equal(StepStatus.Passed, results[3].Status);
        Assert.True(results[3].StartedAt >= results[2].StartedAt + results[2].Duration);
    }
}
