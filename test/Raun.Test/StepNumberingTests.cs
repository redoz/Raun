using Raun.Model;
using Raun.Reporting;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Unit tests for <see cref="StepNumbering"/>: standalone steps take the next top-level
/// number; a parallel/array group (nodes sharing a GroupId) takes one top-level number with
/// sub-numbered members; numbers are zero-padded to a per-scenario width so a runner that sorts
/// sibling leaves lexically renders them in execution order.
/// </summary>
public class StepNumberingTests
{
    private static ScenarioNode Node(int index, string? group = null, bool synthetic = false) => new()
    {
        Index = index,
        StepId = "s" + index,
        Phase = "Given",
        OperationName = "Op" + index,
        DisplayNameTemplate = "step " + index,
        DependsOn = [],
        GroupId = group,
        IsSynthetic = synthetic,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    [Fact]
    public void Linear_scenario_numbers_each_step_sequentially()
    {
        var labels = StepNumbering.Compute(Def(Node(0), Node(1), Node(2), Node(3)));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2", labels[1]);
        Assert.Equal("3", labels[2]);
        Assert.Equal("4", labels[3]);
    }

    [Fact]
    public void Single_step_is_numbered_one()
    {
        var labels = StepNumbering.Compute(Def(Node(0)));

        Assert.Equal("1", Assert.Single(labels.Values));
    }

    [Fact]
    public void Tuple_group_consumes_one_top_level_number_with_sub_indices()
    {
        // standalone, group(g1) x2, standalone, standalone
        var labels = StepNumbering.Compute(
            Def(Node(0), Node(1, "g1"), Node(2, "g1"), Node(3), Node(4)));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2.1", labels[1]);
        Assert.Equal("2.2", labels[2]);
        Assert.Equal("3", labels[3]);
        Assert.Equal("4", labels[4]);
    }

    [Fact]
    public void Group_at_start_takes_top_level_one()
    {
        // group(g0) x2 first, then two standalones
        var labels = StepNumbering.Compute(
            Def(Node(0, "g0"), Node(1, "g0"), Node(2), Node(3)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("2", labels[2]);
        Assert.Equal("3", labels[3]);
    }

    [Fact]
    public void Array_group_of_three_sub_numbers_all_members()
    {
        var labels = StepNumbering.Compute(
            Def(Node(0, "g0"), Node(1, "g0"), Node(2, "g0"), Node(3), Node(4)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("1.3", labels[2]);
        Assert.Equal("2", labels[3]);
        Assert.Equal("3", labels[4]);
    }

    [Fact]
    public void Two_groups_in_one_scenario_each_take_their_own_top_level_number()
    {
        var labels = StepNumbering.Compute(
            Def(Node(0, "ga"), Node(1, "ga"), Node(2), Node(3, "gb"), Node(4, "gb"), Node(5)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("2", labels[2]);
        Assert.Equal("3.1", labels[3]);
        Assert.Equal("3.2", labels[4]);
        Assert.Equal("4", labels[5]);
    }

    [Fact]
    public void Ten_or_more_top_level_steps_zero_pad_the_number()
    {
        var nodes = new ScenarioNode[12];
        for (var i = 0; i < 12; i++)
        {
            nodes[i] = Node(i);
        }

        var labels = StepNumbering.Compute(Def(nodes));

        Assert.Equal("01", labels[0]);
        Assert.Equal("09", labels[8]);
        Assert.Equal("10", labels[9]);
        Assert.Equal("12", labels[11]);
    }

    [Fact]
    public void Group_with_ten_or_more_members_zero_pads_the_sub_index()
    {
        // one standalone, then a group of 10 — top-level stays width 1, sub-index pads to width 2.
        var nodes = new ScenarioNode[11];
        nodes[0] = Node(0);
        for (var i = 1; i < 11; i++)
        {
            nodes[i] = Node(i, "g");
        }

        var labels = StepNumbering.Compute(Def(nodes));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2.01", labels[1]);
        Assert.Equal("2.09", labels[9]);
        Assert.Equal("2.10", labels[10]);
    }

    [Fact]
    public void Labels_sort_lexically_into_execution_order()
    {
        // 12 top-level numbers where #2 is a group of 11 members (the spec's worked example):
        // index 0 standalone; indices 1..11 group "g"; indices 12..21 standalone.
        var nodes = new ScenarioNode[22];
        nodes[0] = Node(0);
        for (var i = 1; i <= 11; i++)
        {
            nodes[i] = Node(i, "g");
        }

        for (var i = 12; i < 22; i++)
        {
            nodes[i] = Node(i);
        }

        var labels = StepNumbering.Compute(Def(nodes));

        var inIndexOrder = labels.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var inLexicalOrder = labels.Values.OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.Equal(inIndexOrder, inLexicalOrder);
    }

    [Fact]
    public void Format_standalone_step_uses_trailing_dot()
    {
        var def = Def(Node(0));
        var labels = StepNumbering.Compute(def);

        Assert.Equal("1. the database is clean",
            StepNumbering.Format(labels, def.Nodes[0], "the database is clean"));
    }

    [Fact]
    public void Format_group_member_omits_the_trailing_dot()
    {
        var def = Def(Node(0), Node(1, "g1"), Node(2, "g1"));
        var labels = StepNumbering.Compute(def);

        Assert.Equal("2.1 patient Jane exists",
            StepNumbering.Format(labels, def.Nodes[1], "patient Jane exists"));
    }

    [Fact]
    public void Synthetic_nodes_consume_no_number_and_leave_no_gap()
    {
        var labels = StepNumbering.Compute(
            Def(Node(0), Node(1, synthetic: true), Node(2), Node(3, synthetic: true), Node(4)));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2", labels[2]);
        Assert.Equal("3", labels[4]);
    }

    [Fact]
    public void Synthetic_nodes_still_get_a_label_for_the_html_report()
    {
        // The HTML report keeps merge nodes (it wants the merge diamond), so a label must exist —
        // it simply does not consume a top-level number.
        var labels = StepNumbering.Compute(Def(Node(0), Node(1, synthetic: true), Node(2)));

        Assert.True(labels.ContainsKey(1));
    }
}
