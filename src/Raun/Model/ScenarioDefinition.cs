namespace Raun.Model;

/// <summary>
/// A fully lowered scenario: its metadata plus the ordered execution graph. Emitted by the
/// generator and handed to the scheduler.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>Stable, runner-independent id for the scenario.</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Human-readable scenario name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Fully-qualified name of the originating <c>[Scenario]</c> method.</summary>
    public required string MethodName { get; init; }

    /// <summary>Namespace of the declaring type; empty for the global namespace. Runners group
    /// scenarios by it, and it is a segment of the tree-filter path.</summary>
    public required string Namespace { get; init; }

    /// <summary>Simple name of the declaring type, nested types joined with <c>+</c>
    /// (<c>Outer+Inner</c>). Stated, never derived from <see cref="MethodName"/>: a dotted split
    /// cannot tell a nested type from a namespace.</summary>
    public required string TypeName { get; init; }

    /// <summary>Optional display name for the scenario's declaring class (from a <c>[DisplayName]</c>
    /// attribute); when null the runner uses the real type name.</summary>
    public string? ClassDisplayName { get; init; }

    /// <summary>The <c>[Scenario]</c> method itself, for a world that reads per-scenario settings from
    /// its attributes. The generator records it for a scenario with a world; null otherwise.</summary>
    public System.Reflection.MethodInfo? Method { get; init; }

    /// <summary>What a step's <see cref="ScenarioContext.Scenario"/> reports.</summary>
    public ScenarioInfo Info => _info ??= new ScenarioInfo(ScenarioId, DisplayName, MethodName, Method);

    private ScenarioInfo? _info;

    /// <summary>Source file of the scenario method, if known.</summary>
    public string? SourceFile { get; init; }

    /// <summary>1-based source line of the scenario method, or 0 if unknown.</summary>
    public int SourceLine { get; init; }

    /// <summary>Source file of the declaring type, if known.</summary>
    public string? TypeSourceFile { get; init; }

    /// <summary>1-based source line of the declaring type, or 0 if unknown.</summary>
    public int TypeSourceLine { get; init; }

    /// <summary>Scenario-wide timeout, or null for none.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>When this scenario's <see cref="Cleanup.Optional"/> teardowns run; from
    /// <c>[Teardown(Run.…)]</c>, defaulting to <see cref="Run.Always"/> when the attribute is absent.</summary>
    public Run TeardownPolicy { get; init; } = Run.Always;

    /// <summary>
    /// The contended resources this scenario needs, reduced per type (exclusive wins), from every
    /// <c>[Uses&lt;T&gt;]</c> on its steps' DSL methods, the scenario method, its classes, and the
    /// assembly. The run loop acquires the whole set before the scenario starts. Empty when nothing
    /// is declared.
    /// </summary>
    public IReadOnlyList<ContendedResourceUse> Uses { get; init; } = [];

    /// <summary>
    /// The scenario's <c>key=value</c> tags from every <see cref="TagAttribute"/> on the assembly, its
    /// containing classes (outermost first), and the scenario method, in that order, with the tags
    /// of an attribute whose class carries <c>[Tag]</c>s expanded in place. A pair reached twice
    /// (compared case-insensitively) is kept once, at its first position. Empty when none.
    /// </summary>
    public IReadOnlyList<ScenarioTag> Tags { get; init; } = [];

    /// <summary>Complete generated resource defaults for this application.</summary>
    public IReadOnlyList<ResourceMapping> ResourceMappings { get; init; } = [];

    /// <summary>The graph nodes in source order.</summary>
    public required IReadOnlyList<ScenarioNode> Nodes { get; init; }

    /// <summary>
    /// Verifies graph invariants the scheduler relies on: every dependency references an existing
    /// node, no node depends on itself, and the graph is acyclic.
    /// </summary>
    /// <exception cref="InvalidOperationException">A dependency is invalid or the graph has a cycle.</exception>
    public void Validate()
    {
        var count = Nodes.Count;
        var teardown = -1;
        var setup = -1;

        for (var i = 0; i < count; i++)
        {
            var node = Nodes[i];

            // Every edge names a node by its position in the list; the scheduler uses the two
            // interchangeably.
            if (node.Index != i)
            {
                throw new InvalidOperationException(
                    $"Node at position {i} ('{node.OperationName}') has Index {node.Index}; a node's Index is its position.");
            }

            ValidateKind(node);
            if (node.Kind == NodeKind.Teardown)
            {
                if (teardown >= 0)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{DisplayName}' has two teardown nodes ({teardown} and {i}); it has one or none.");
                }

                teardown = i;
            }

            if (node.Kind == NodeKind.Setup)
            {
                if (setup >= 0)
                {
                    throw new InvalidOperationException(
                        $"Scenario '{DisplayName}' has two setup nodes ({setup} and {i}); it has one or none.");
                }

                setup = i;
            }
            foreach (var dep in node.DependsOn)
            {
                if (dep < 0 || dep >= count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') depends on out-of-range node {dep}.");
                }

                if (dep == node.Index)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') depends on itself.");
                }
            }

            foreach (var wait in node.WaitsFor)
            {
                if (wait < 0 || wait >= count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') waits for out-of-range node {wait}.");
                }

                if (wait == node.Index)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') waits for itself.");
                }
            }

            if (node.Arms.Count > 0 && node.SelectArm is null)
            {
                throw new InvalidOperationException(
                    $"Step {node.Index} ('{node.OperationName}') has {node.Arms.Count} arm(s) but no SelectArm to choose one.");
            }

            if (node.SelectArm is not null && node.Arms.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Step {node.Index} ('{node.OperationName}') has a SelectArm but no arms to choose from.");
            }

            foreach (var guard in node.Guards)
            {
                if (guard.ConditionIndex < 0 || guard.ConditionIndex >= count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') has a guard on out-of-range node {guard.ConditionIndex}.");
                }

                var condition = Nodes[guard.ConditionIndex];
                if (condition.SelectArm is null)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') is guarded on step {guard.ConditionIndex} "
                        + $"('{condition.OperationName}'), which has no SelectArm.");
                }

                // -1 is the "matched no arm" outcome of a switch without a default: a real choice.
                if (guard.Arm < Guard.NoArm || guard.Arm >= condition.Arms.Count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') is guarded on arm {guard.Arm} of step "
                        + $"{guard.ConditionIndex} ('{condition.OperationName}'), which has {condition.Arms.Count} arm(s) "
                        + $"(and {Guard.NoArm} for matching none).");
                }
            }

            foreach (var source in node.MergeSources)
            {
                if (source < 0 || source >= count)
                {
                    throw new InvalidOperationException(
                        $"Merge step {node.Index} references out-of-range source {source}.");
                }

                if (source == node.Index)
                {
                    throw new InvalidOperationException($"Merge step {node.Index} references itself.");
                }
            }

            // Merge sources must be mutually exclusive — every pair must be guarded on a common
            // condition, on different arms — so at most one can pass. The generator guarantees this;
            // without the check a violation would surface as a baffling double-write.
            for (var a = 0; a < node.MergeSources.Count; a++)
            {
                for (var b = a + 1; b < node.MergeSources.Count; b++)
                {
                    if (!AreExclusive(Nodes[node.MergeSources[a]], Nodes[node.MergeSources[b]]))
                    {
                        throw new InvalidOperationException(
                            $"Merge step {node.Index} sources {node.MergeSources[a]} and {node.MergeSources[b]} "
                            + "are not mutually exclusive (no shared condition guarding them on different arms).");
                    }
                }
            }
        }

        // The teardown node is not in the graph: it runs after everything else, so nothing can be
        // ordered after it or read a value from it.
        if (teardown >= 0)
        {
            foreach (var node in Nodes)
            {
                if (node.DependsOn.Contains(teardown) || node.WaitsFor.Contains(teardown) || node.MergeSources.Contains(teardown)
                    || node.Guards.Any(guard => guard.ConditionIndex == teardown))
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') refers to the teardown node {teardown}, which runs after every other node.");
                }
            }
        }

        // DFS cycle detection over the index-addressed dependency graph (0 = unvisited,
        // 1 = on stack, 2 = done).
        var state = new int[count];
        for (var i = 0; i < count; i++)
        {
            if (state[i] == 0 && HasCycle(i, state))
            {
                throw new InvalidOperationException(
                    $"Scenario '{DisplayName}' has a dependency cycle involving step {i}.");
            }
        }
    }

    /// <summary>What a node's <see cref="ScenarioNode.Kind"/> promises about its other members.</summary>
    private static void ValidateKind(ScenarioNode node)
    {
        var name = $"Step {node.Index} ('{node.OperationName}')";
        if (node.Kind == NodeKind.Merge)
        {
            if (node.MergeSources.Count == 0)
            {
                throw new InvalidOperationException($"{name} is a merge without sources to select from.");
            }

            if (node.SelectArm is not null)
            {
                throw new InvalidOperationException($"{name} is a merge and cannot be a branch condition.");
            }

            return;
        }

        if (node.MergeSources.Count > 0)
        {
            throw new InvalidOperationException($"{name} has merge sources but is a {node.Kind}; only a merge selects among sources.");
        }

        if (node.Kind == NodeKind.Teardown)
        {
            if (node.DependsOn.Count > 0 || node.WaitsFor.Count > 0 || node.Guards.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{name} is the teardown node and cannot depend on, wait for or be guarded by another node: it runs after all of them.");
            }

            return;
        }

        if (node.Invoke is null)
        {
            throw new InvalidOperationException($"{name} is a {node.Kind} and has no Invoke to run.");
        }
    }

    /// <summary>True when two candidate producers can never both run: some condition guards both,
    /// on different arms.</summary>
    private static bool AreExclusive(ScenarioNode left, ScenarioNode right)
    {
        foreach (var l in left.Guards)
        {
            foreach (var r in right.Guards)
            {
                if (l.ConditionIndex == r.ConditionIndex && l.Arm != r.Arm)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool HasCycle(int index, int[] state)
    {
        state[index] = 1;

        // Merge sources, waits-for edges and guards are real ordering edges (a merge selects one
        // producer's output; a wait holds a node until another is terminal; a guarded node cannot
        // start before its condition chose an arm), so a cycle through any of them is as fatal as
        // one through DependsOn and must be walked the same way. Without the guards a guard cycle
        // passed validation and stalled the scheduler at run time.
        if (HasCycleThrough(Nodes[index].DependsOn, state)
            || HasCycleThrough(Nodes[index].MergeSources, state)
            || HasCycleThrough(Nodes[index].WaitsFor, state)
            || HasCycleThrough([.. Nodes[index].Guards.Select(guard => guard.ConditionIndex)], state))
        {
            return true;
        }

        state[index] = 2;
        return false;
    }

    private bool HasCycleThrough(IReadOnlyList<int> edges, int[] state)
    {
        foreach (var edge in edges)
        {
            if (state[edge] == 1)
            {
                return true;
            }

            if (state[edge] == 0 && HasCycle(edge, state))
            {
                return true;
            }
        }

        return false;
    }
}
