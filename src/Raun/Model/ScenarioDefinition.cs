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

    /// <summary>Namespace of the declaring type; empty for the global namespace, and empty when the
    /// definition came from a generator that predates this member (readers then split
    /// <see cref="MethodName"/>).</summary>
    public string Namespace { get; init; } = "";

    /// <summary>Simple name of the declaring type, nested types joined with <c>+</c>
    /// (<c>Outer+Inner</c>); empty when unknown, as for <see cref="Namespace"/>.</summary>
    public string TypeName { get; init; } = "";

    /// <summary>Optional display name for the scenario's declaring class (from a <c>[DisplayName]</c>
    /// attribute); when null the runner uses the real type name.</summary>
    public string? ClassDisplayName { get; init; }

    /// <summary>Source file of the scenario method, if known.</summary>
    public string? SourceFile { get; init; }

    /// <summary>1-based source line of the scenario method, or 0 if unknown.</summary>
    public int SourceLine { get; init; }

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

        for (var i = 0; i < count; i++)
        {
            var node = Nodes[i];
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

            foreach (var guard in node.Guards)
            {
                if (guard.ConditionIndex < 0 || guard.ConditionIndex >= count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') has a guard on out-of-range node {guard.ConditionIndex}.");
                }

                if (Nodes[guard.ConditionIndex].EvaluateCondition is null)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') is guarded on step {guard.ConditionIndex} "
                        + $"('{Nodes[guard.ConditionIndex].OperationName}'), which has no EvaluateCondition.");
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
            // condition with opposite WhenValue — so at most one can pass. The generator guarantees
            // this; without the check a violation would surface as a baffling double-write.
            for (var a = 0; a < node.MergeSources.Count; a++)
            {
                for (var b = a + 1; b < node.MergeSources.Count; b++)
                {
                    if (!AreExclusive(Nodes[node.MergeSources[a]], Nodes[node.MergeSources[b]]))
                    {
                        throw new InvalidOperationException(
                            $"Merge step {node.Index} sources {node.MergeSources[a]} and {node.MergeSources[b]} "
                            + "are not mutually exclusive (no shared condition with opposite guard values).");
                    }
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

    /// <summary>True when two candidate producers can never both run: some condition guards both
    /// with opposite <see cref="Guard.WhenValue"/>s.</summary>
    private static bool AreExclusive(ScenarioNode left, ScenarioNode right)
    {
        foreach (var l in left.Guards)
        {
            foreach (var r in right.Guards)
            {
                if (l.ConditionIndex == r.ConditionIndex && l.WhenValue != r.WhenValue)
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

        // Merge sources and waits-for edges are real ordering edges (a merge selects one producer's
        // output; a wait holds a node until another is terminal), so a cycle through either is as
        // fatal as one through DependsOn and must be walked the same way.
        if (HasCycleThrough(Nodes[index].DependsOn, state)
            || HasCycleThrough(Nodes[index].MergeSources, state)
            || HasCycleThrough(Nodes[index].WaitsFor, state))
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
