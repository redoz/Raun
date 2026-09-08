using System.Globalization;
using Raun.Model;

namespace Raun.Reporting;

/// <summary>
/// Computes per-step display labels for a scenario so a runner that sorts sibling leaves
/// lexically (VS Test Explorer) renders them in execution order. A standalone step takes the next
/// top-level number; a parallel/array group (consecutive nodes sharing a non-null
/// <see cref="ScenarioNode.GroupId"/>) takes one top-level number with sub-numbered members. Both
/// the number and the sub-index are zero-padded to a per-scenario width so lexical order equals
/// numeric order (≤9 steps render <c>1</c>–<c>9</c>; ≥10 render <c>01</c>…). Pure and runner-free,
/// so it is unit-testable and shared by discovery and execution.
/// </summary>
public static class StepNumbering
{
    /// <summary>
    /// Maps each <see cref="ScenarioNode.Index"/> to its label (e.g. <c>"1"</c>, <c>"2.1"</c>, or
    /// zero-padded <c>"02.01"</c> for large scenarios). Group membership is resolved by first
    /// encounter so the result is robust to a non-contiguous group.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Compute(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var topLevelByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var subCounterByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var assignments = new List<(int Index, int Top, int Sub)>(definition.Nodes.Count);
        var nextTop = 0;
        var maxGroupSize = 1;

        foreach (var node in definition.Nodes.OrderBy(n => n.Index))
        {
            if (node.IsSynthetic)
            {
                // Consumes no number (users must not see a gap in 1, 2, 3) but still gets a label so
                // the HTML report can render it. It reuses the previous top-level number, which would
                // read as a duplicate — harmless, because a synthetic never reaches a runner's tree.
                assignments.Add((node.Index, nextTop, 0));
            }
            else if (node.GroupId is null)
            {
                // Standalone step: consume the next top-level number (sub 0 = no sub-index).
                assignments.Add((node.Index, ++nextTop, 0));
            }
            else if (!topLevelByGroup.TryGetValue(node.GroupId, out var top))
            {
                // First member of a group: consume one top-level number, start its sub-counter at 1.
                topLevelByGroup[node.GroupId] = ++nextTop;
                subCounterByGroup[node.GroupId] = 1;
                assignments.Add((node.Index, nextTop, 1));
            }
            else
            {
                // Later member: reuse the group's top-level number, advance its sub-counter.
                var sub = ++subCounterByGroup[node.GroupId];
                assignments.Add((node.Index, top, sub));
                if (sub > maxGroupSize)
                {
                    maxGroupSize = sub;
                }
            }
        }

        var topWidth = Digits(nextTop);
        var subWidth = Digits(maxGroupSize);

        var labels = new Dictionary<int, string>(assignments.Count);
        foreach (var (index, top, sub) in assignments)
        {
            var topText = top.ToString(CultureInfo.InvariantCulture).PadLeft(topWidth, '0');
            labels[index] = sub == 0
                ? topText
                : topText + "." + sub.ToString(CultureInfo.InvariantCulture).PadLeft(subWidth, '0');
        }

        return labels;
    }

    /// <summary>
    /// Composes a leaf display name from a step's computed label and its (runtime-formatted) text.
    /// A standalone step reads <c>"{label}. {step}"</c>; a group member reads <c>"{label} {step}"</c>
    /// (no trailing dot). Both call sites use this so the discovered tree and the running tree agree.
    /// </summary>
    public static string Format(IReadOnlyDictionary<int, string> labels, ScenarioNode node, string stepText)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(node);

        var label = labels[node.Index];
        return node.GroupId is null ? label + ". " + stepText : label + " " + stepText;
    }

    /// <summary>Decimal digit-width of a count (at least 1).</summary>
    private static int Digits(int value) => value < 10 ? 1 : value.ToString(CultureInfo.InvariantCulture).Length;
}
