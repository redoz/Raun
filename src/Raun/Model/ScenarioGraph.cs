namespace Raun.Model;

/// <summary>
/// The ordering edges of a scenario graph, in one place so the scheduler, the conflict ledger, and
/// filtered runs all agree on what "must run before" means.
/// </summary>
internal static class ScenarioGraph
{
    /// <summary>
    /// The nodes that must run before <paramref name="node"/>: its <see cref="ScenarioNode.DependsOn"/>
    /// (all-of), its <see cref="ScenarioNode.MergeSources"/> (a merge selects one source's output, so
    /// it runs after every candidate), its <see cref="ScenarioNode.WaitsFor"/> (ordering only), and
    /// each guard's condition (a guarded node runs after the condition that decides it).
    /// </summary>
    public static IEnumerable<int> Predecessors(ScenarioNode node)
    {
        foreach (var dep in node.DependsOn)
        {
            yield return dep;
        }

        foreach (var source in node.MergeSources)
        {
            yield return source;
        }

        foreach (var wait in node.WaitsFor)
        {
            yield return wait;
        }

        foreach (var guard in node.Guards)
        {
            yield return guard.ConditionIndex;
        }
    }

    /// <summary>The transitive predecessor closure of <paramref name="roots"/>, roots included.
    /// Out-of-range indices are ignored (<see cref="ScenarioDefinition.Validate"/> rejects them).</summary>
    public static HashSet<int> Closure(IReadOnlyList<ScenarioNode> nodes, IEnumerable<int> roots)
    {
        var closure = new HashSet<int>();
        var stack = new Stack<int>(roots);
        while (stack.Count > 0)
        {
            var index = stack.Pop();
            if (index < 0 || index >= nodes.Count || !closure.Add(index))
            {
                continue;
            }

            foreach (var predecessor in Predecessors(nodes[index]))
            {
                stack.Push(predecessor);
            }
        }

        return closure;
    }
    /// <summary>
    /// <c>after[i][j]</c> is true when node <c>i</c> must run after node <c>j</c>, transitively, over
    /// the ordering edges <see cref="ScenarioGraph.Predecessors"/> defines.
    /// </summary>
    internal static bool[][] ComputeAfter(IReadOnlyList<ScenarioNode> nodes)
    {
        var count = nodes.Count;
        var after = new bool[count][];
        var stack = new Stack<int>();

        for (var i = 0; i < count; i++)
        {
            var row = new bool[count];
            stack.Clear();
            foreach (var predecessor in ScenarioGraph.Predecessors(nodes[i]))
            {
                stack.Push(predecessor);
            }

            while (stack.Count > 0)
            {
                var pred = stack.Pop();
                if (pred < 0 || pred >= count || row[pred])
                {
                    continue;
                }

                row[pred] = true;
                foreach (var predecessor in ScenarioGraph.Predecessors(nodes[pred]))
                {
                    stack.Push(predecessor);
                }
            }

            after[i] = row;
        }

        return after;
    }}
