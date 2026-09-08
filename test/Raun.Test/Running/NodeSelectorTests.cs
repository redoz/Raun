using Raun.Model;
using Raun.Running;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Selection is one abstraction shared by discovery, scenario selection and target expansion: a
/// null selector takes everything, a uid selector takes the nodes the runner named.
/// </summary>
public class NodeSelectorTests
{
    private static ScenarioNode Node(int index, string stepId) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"step {index}",
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "a scenario",
        MethodName = "Demo.Scenarios.ASscenario",
        Nodes = nodes,
    };

    [Fact]
    public void A_uid_selector_matches_only_the_named_nodes()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [StepUid.Of("scn", "b")], StringComparer.OrdinalIgnoreCase));

        Assert.False(selector.Matches(definition, definition.Nodes[0]));
        Assert.True(selector.Matches(definition, definition.Nodes[1]));
    }

    [Fact]
    public void A_uid_selector_ignores_case_like_the_runner_does()
    {
        var definition = Definition(Node(0, "a"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [StepUid.Of("scn", "a").ToUpperInvariant()], StringComparer.OrdinalIgnoreCase));

        Assert.True(selector.Matches(definition, definition.Nodes[0]));
    }

    [Fact]
    public void A_null_selector_selects_every_scenario_and_leaves_targets_open()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));

        Assert.Single(RunLoop.SelectScenarios([definition], selector: null));
        Assert.Null(RunLoop.SelectTargets(definition, selector: null));
    }

    [Fact]
    public void A_selector_that_matches_one_step_selects_the_scenario_and_that_target()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [StepUid.Of("scn", "b")], StringComparer.OrdinalIgnoreCase));

        Assert.Single(RunLoop.SelectScenarios([definition], selector));
        var targets = RunLoop.SelectTargets(definition, selector);
        Assert.NotNull(targets);
        Assert.Equal([1], targets);
    }

    [Fact]
    public void A_selector_that_matches_nothing_selects_no_scenario()
        => Assert.Empty(RunLoop.SelectScenarios(
            [Definition(Node(0, "a"))],
            new UidNodeSelector(new HashSet<string>(["scn:zzz"], StringComparer.OrdinalIgnoreCase))));
}
