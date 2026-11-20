namespace Raun.Model;

/// <summary>
/// What a <see cref="ScenarioNode"/> is. The generator states it; nothing derives it from the
/// node's other members or from its name.
/// </summary>
/// <remarks>
/// A branch's condition is not a kind of its own: it is a <see cref="Step"/> the scenario wrote,
/// which also selects an arm (<see cref="ScenarioNode.SelectArm"/>).
/// </remarks>
public enum NodeKind
{
    /// <summary>A step the scenario wrote: one call of a step class method.</summary>
    Step = 0,

    /// <summary>The setup of a scenario with a world: the first node, which creates the world and on
    /// which every step depends. Reported like a step, but unnumbered.</summary>
    Setup,

    /// <summary>The scenario's one teardown node. Not part of the graph: the scheduler runs the
    /// registered cleanups after every other node is terminal, and reports them under this node.
    /// It has no body of its own.</summary>
    Teardown,

    /// <summary>Plumbing: binds the variables of one arm's pattern from the condition's recorded
    /// value. Runs, but is not a step anyone wrote.</summary>
    Case,

    /// <summary>Plumbing: selects the value of a local assigned in the arms of a branch from the one
    /// arm that ran (<see cref="ScenarioNode.MergeSources"/>); with a single source it passes the
    /// value of an untaken branch through. Never invoked.</summary>
    Merge,

    /// <summary>Plumbing: gathers the results of an array group into the one array its local holds,
    /// once, so every step that reads the local reads the same array. Runs, but is not a step anyone
    /// wrote.</summary>
    Array,
}
