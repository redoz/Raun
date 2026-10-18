using Raun.Model;
using Xunit;

namespace Raun.Test;

/// <summary>
/// WaitsFor is an ordering-only edge. The model validates it like any other edge, and the conflict
/// ledger treats two nodes joined by it as ordered — which is the whole point: the statement after an
/// <c>if</c> must never be reported as racing the inside of the <c>if</c>.
/// </summary>
public class WaitsForTests
{
    private static ScenarioNode Node(int index, int[]? dependsOn = null, int[]? waitsFor = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn ?? [],
        WaitsFor = waitsFor ?? [],
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
    public void Validate_accepts_a_waits_for_edge()
        => Def(Node(0), Node(1, dependsOn: [0]), Node(2, waitsFor: [1])).Validate();

    [Fact]
    public void Validate_rejects_an_out_of_range_wait()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Def(Node(0, waitsFor: [7])).Validate());
        Assert.Contains("waits for out-of-range node 7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_rejects_waiting_for_itself()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Def(Node(0, waitsFor: [0])).Validate());
        Assert.Contains("waits for itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_detects_a_cycle_through_a_wait()
    {
        var def = Def(Node(0, waitsFor: [1]), Node(1, dependsOn: [0]));

        var ex = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ledger_treats_nodes_joined_by_a_wait_as_ordered()
    {
        var identity = new ResourceIdentity(typeof(Resources.User), "jane@x");
        var ledger = new ResourceLedger([Node(0), Node(1, dependsOn: [0]), Node(2, dependsOn: [0], waitsFor: [1])]);

        ledger.Claim(1, "arm edits", identity, ResourceEffect.Update);
        ledger.Claim(2, "after the if reads", identity, ResourceEffect.Read); // ordered via WaitsFor: no conflict
    }
}
