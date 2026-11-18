using Raun.Model;
using Raun.Running;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Selection when a scenario is one test: a scenario is either selected whole or not at all, whether
/// it was named by its own uid or reached through a filter that matches one of its steps.
/// </summary>
public class WholeScenarioSelectorTests
{
    private static ScenarioNode Node(int index, string stepId, bool synthetic = false) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = stepId,
        DependsOn = [],
        Kind = synthetic ? NodeKind.Case : NodeKind.Step,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(string id, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = id,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
    };

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    private static bool[] Matches(NodeSelector selector, ScenarioDefinition definition)
        => [.. definition.Nodes.Select(n => selector.Matches(definition, n))];

    [Fact]
    public void A_named_scenario_selects_every_one_of_its_steps()
    {
        var scn = Definition("scn", Node(0, "a"), Node(1, "b"), Node(2, "c"));
        var other = Definition("other", Node(0, "a"));

        var selector = WholeScenarioSelector.ForScenarioIds(Set("scn"));

        Assert.Equal([true, true, true], Matches(selector, scn));
        Assert.Equal([false], Matches(selector, other));
    }

    [Fact]
    public void A_step_uid_selects_nothing_when_scenarios_are_the_tests()
    {
        var scn = Definition("scn", Node(0, "a"), Node(1, "b"));

        var selector = WholeScenarioSelector.ForScenarioIds(Set("scn:a"));

        Assert.Equal([false, false], Matches(selector, scn));
    }

    [Fact]
    public void A_filter_that_matches_one_step_selects_the_whole_scenario()
    {
        var scn = Definition("scn", Node(0, "a"), Node(1, "b"), Node(2, "c"));
        var other = Definition("other", Node(0, "a"), Node(1, "b"));

        var selector = WholeScenarioSelector.Widening(new UidNodeSelector(Set("scn:b")));

        Assert.Equal([true, true, true], Matches(selector, scn));
        Assert.Equal([false, false], Matches(selector, other));
    }

    [Fact]
    public void A_filter_that_matches_only_plumbing_selects_nothing()
    {
        var scn = Definition("scn", Node(0, "a"), Node(1, "merge", synthetic: true));

        var selector = WholeScenarioSelector.Widening(new UidNodeSelector(Set("scn:merge")));

        Assert.Equal([false, false], Matches(selector, scn));
    }
}
