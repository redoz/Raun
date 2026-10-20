using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Mtp;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Phase 3 behavioral tests for discovery. Raun's MTP framework turns each registered
/// <see cref="ScenarioDefinition"/> into one <see cref="TestNode"/> per <see cref="ScenarioNode"/>
/// (step nodes only, no parent scenario node), with a stable <c>{ScenarioId}:{StepId}</c> uid, a
/// <c>{scenario} ▸ {step}</c> display name, a <see cref="TestFileLocationProperty"/> for "go to
/// source", and the <see cref="DiscoveredTestNodeStateProperty"/>. These tests drive the pure
/// node-builder directly so they don't need MTP's session machinery.
/// </summary>
public class RaunDiscovererTests
{
    private static ScenarioNode Node(int index, string stepId, string template, string? file = null, int line = 0, string? group = null, bool synthetic = false, bool teardown = false) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        SourceFile = file,
        SourceLine = line,
        DependsOn = [],
        GroupId = group,
        IsSynthetic = synthetic,
        IsTeardown = teardown,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(string id = "scn", string display = "my scenario", string method = "Ns.Scn", params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = method,
        Nodes = nodes,
    };

    [Fact]
    public void Emits_one_node_per_step()
    {
        var definition = Definition(
            nodes:
            [
                Node(0, "a", "step a"),
                Node(1, "b", "step b"),
                Node(2, "c", "step c"),
            ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal(3, nodes.Count);
    }

    [Fact]
    public void Uid_combines_scenario_id_and_step_id()
    {
        var definition = Definition(
            id: "scn-1",
            nodes:
            [
                Node(0, "step-0", "first"),
                Node(1, "step-1", "second"),
            ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal("scn-1:step-0", nodes[0].Uid.Value);
        Assert.Equal("scn-1:step-1", nodes[1].Uid.Value);
    }

    [Fact]
    public void Standalone_step_display_name_is_numbered_without_scenario_prefix()
    {
        var definition = Definition(
            display: "patient booking",
            nodes: [Node(0, "a", "patient Jane exists")]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }

    [Fact]
    public void Group_member_display_name_uses_sub_number_without_trailing_dot()
    {
        var definition = Definition(
            nodes:
            [
                Node(0, "clean", "the database is clean"),
                Node(1, "p", "patient Jane exists", group: "g1"),
                Node(2, "s", "an available slot exists", group: "g1"),
                Node(3, "c", "creating an appointment"),
            ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal("1. the database is clean", nodes[0].DisplayName);
        Assert.Equal("2.1 patient Jane exists", nodes[1].DisplayName);
        Assert.Equal("2.2 an available slot exists", nodes[2].DisplayName);
        Assert.Equal("3. creating an appointment", nodes[3].DisplayName);
    }

    [Fact]
    public void Node_carries_discovered_state_property()
    {
        var definition = Definition(nodes: [Node(0, "a", "step a")]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        Assert.NotEmpty(node.Properties.OfType<DiscoveredTestNodeStateProperty>());
    }

    [Fact]
    public void Node_carries_file_location_when_source_is_known()
    {
        var definition = Definition(
            nodes: [Node(0, "a", "step a", file: @"C:\src\Booking.cs", line: 42)]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        var location = Assert.Single(node.Properties.OfType<TestFileLocationProperty>());
        Assert.Equal(@"C:\src\Booking.cs", location.FilePath);
        Assert.Equal(42, location.LineSpan.Start.Line);
        Assert.Equal(42, location.LineSpan.End.Line);
    }

    [Fact]
    public void Node_carries_method_identity_for_namespace_class_method_grouping()
    {
        // Runners (VS Test Explorer, the VSTest bridge) build their namespace -> class -> method tree
        // from a TestMethodIdentifierProperty; without it the step nodes fall under "<Empty Namespace>"
        // / "<Empty Class>". Namespace/type still come from the scenario method's FQN, but the method
        // node is the human scenario name so the tree reads
        // AppointmentTests -> Scenarios -> <scenario name> -> steps.
        var definition = Definition(
            display: "book an appointment",
            method: "MyApp.Booking.Scenarios.BookAppointment",
            nodes: [Node(0, "a", "step a")]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        var id = Assert.Single(node.Properties.OfType<TestMethodIdentifierProperty>());
        Assert.Equal("MyApp.Booking", id.Namespace);
        Assert.Equal("Scenarios", id.TypeName);
        Assert.Equal("book an appointment", id.MethodName);
    }

    [Fact]
    public void Node_omits_file_location_when_source_is_unknown()
    {
        // No file / line 0 -> no "go to source" target, so no location property is attached.
        var definition = Definition(nodes: [Node(0, "a", "step a", file: null, line: 0)]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        Assert.Empty(node.Properties.OfType<TestFileLocationProperty>());
    }

    [Fact]
    public void Synthetic_merge_nodes_are_not_discovered()
    {
        var definition = Definition(nodes:
        [
            Node(0, "a", "step a"),
            Node(1, "m", "«merge appt»", synthetic: true),
            Node(2, "b", "step b"),
        ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal(2, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.DisplayName.Contains("merge", StringComparison.Ordinal));
    }

    [Fact]
    public void Numbering_has_no_gap_where_a_synthetic_node_sits()
    {
        var definition = Definition(nodes:
        [
            Node(0, "a", "step a"),
            Node(1, "m", "«merge appt»", synthetic: true),
            Node(2, "b", "step b"),
        ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal("1. step a", nodes[0].DisplayName);
        Assert.Equal("2. step b", nodes[1].DisplayName);
    }

    [Fact]
    public void Teardown_nodes_are_discovered_unlike_synthetic_ones()
    {
        // The two markers are opposites: a merge node is graph plumbing and stays hidden, a teardown
        // node is a real reportable step. Conflating them would hide a failing cleanup from CI.
        var definition = Definition(nodes:
        [
            Node(0, "a", "step a"),
            Node(1, "m", "«merge»", synthetic: true),
            Node(2, "t", "Teardown", teardown: true),
        ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, n => n.DisplayName.Contains("Teardown", StringComparison.Ordinal));
    }

    [Fact]
    public void Teardown_node_takes_the_final_step_number()
    {
        var definition = Definition(nodes:
        [
            Node(0, "a", "step a"),
            Node(1, "b", "step b"),
            Node(2, "t", "Teardown", teardown: true),
        ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal("3. Teardown", nodes[^1].DisplayName);
    }

    [Fact]
    public void Discovered_node_carries_the_scenario_tags_as_metadata()
    {
        var definition = new ScenarioDefinition
        {
            ScenarioId = "scn",
            DisplayName = "my scenario",
            MethodName = "Ns.Scn",
            Tags = [new("area", "auth"), new("owner", "payments")],
            Nodes = [Node(0, "a", "step a")],
        };

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        Assert.Equal(
            ["area=auth", "owner=payments"],
            node.Properties.OfType<TestMetadataProperty>().Select(p => p.Key + "=" + p.Value).Order(StringComparer.Ordinal));
    }
}
