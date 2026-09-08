using System;
using System.Collections.Generic;
using Raun.Model;

namespace Raun;

/// <summary>
/// The per-scenario record of which step claimed which resource identity with which verb, used to
/// detect — never to serialize — conflicting concurrent access. Two steps conflict when nothing
/// orders them (no dependency path in either direction through <see cref="ScenarioNode.DependsOn"/>,
/// <see cref="ScenarioNode.MergeSources"/>, or <see cref="ScenarioNode.Guards"/>), they claim the same
/// identity, and at least one verb is <see cref="LockMode.Exclusive"/>. The detection is structural:
/// it does not depend on whether the two steps happened to overlap in time, so the same scenario
/// fails the same way on every run.
/// </summary>
internal sealed class ResourceLedger
{
    private readonly object _lock = new();
    private readonly bool[][] _after;
    private readonly Dictionary<ResourceIdentity, List<Entry>> _claims = [];

    private readonly record struct Entry(int Node, string StepDisplayName, LifecycleVerb Verb);

    /// <summary>Builds the ledger for one scenario, precomputing its transitive "must run after" relation.</summary>
    public ResourceLedger(IReadOnlyList<ScenarioNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        _after = ComputeAfter(nodes);
    }

    /// <summary>
    /// Records that step <paramref name="nodeIndex"/> touches <paramref name="identity"/> with
    /// <paramref name="verb"/>. Throws <see cref="ResourceConflictException"/> when an unordered step
    /// already holds a claim on the identity and either verb is exclusive; the refused claim is not
    /// recorded. Re-claiming by the same step keeps the stronger verb.
    /// </summary>
    public void Claim(int nodeIndex, string stepDisplayName, ResourceIdentity identity, LifecycleVerb verb)
    {
        lock (_lock)
        {
            if (!_claims.TryGetValue(identity, out var entries))
            {
                entries = [];
                _claims[identity] = entries;
            }

            var exclusive = verb.ToLockMode() == LockMode.Exclusive;
            var own = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Node == nodeIndex)
                {
                    own = i;
                    continue;
                }

                if (IsOrdered(entry.Node, nodeIndex))
                {
                    continue;
                }

                if (exclusive || entry.Verb.ToLockMode() == LockMode.Exclusive)
                {
                    throw new ResourceConflictException(identity, stepDisplayName, verb, entry.StepDisplayName, entry.Verb);
                }
            }

            if (own >= 0)
            {
                if (verb.Precedence() > entries[own].Verb.Precedence())
                {
                    entries[own] = entries[own] with { Verb = verb };
                }
            }
            else
            {
                entries.Add(new Entry(nodeIndex, stepDisplayName, verb));
            }
        }
    }

    private bool IsOrdered(int a, int b) => _after[a][b] || _after[b][a];

    /// <summary>
    /// <c>after[i][j]</c> is true when node <c>i</c> must run after node <c>j</c>, transitively, over
    /// the ordering edges <see cref="ScenarioGraph.Predecessors"/> defines.
    /// </summary>
    private static bool[][] ComputeAfter(IReadOnlyList<ScenarioNode> nodes)
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
    }
}
