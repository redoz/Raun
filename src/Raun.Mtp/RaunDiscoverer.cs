using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;
using Raun.Reporting;
using Raun.Running;

namespace Raun.Mtp;

/// <summary>
/// Turns a lowered <see cref="ScenarioDefinition"/> into Microsoft.Testing.Platform
/// <see cref="TestNode"/>s — one per <see cref="ScenarioNode"/> (step), with no parent scenario
/// node. (Decision ①: no runner renders the MTP hierarchy today, so a parent node would be a
/// dangling flat sibling; scenario identity lives in the display-name prefix instead.)
/// </summary>
/// <remarks>
/// Each node gets:
/// <list type="bullet">
///   <item>a stable <c>{ScenarioId}:{StepId}</c> uid so single-step run filters resolve to the
///   same node that discovery emitted;</item>
///   <item>a numbered leaf display name (<c>"1. {step}"</c>, group member <c>"2.1 {step}"</c>) from
///   <see cref="StepNumbering"/>; the reporter refines runtime-bound names at execution time;</item>
///   <item>a <see cref="TestFileLocationProperty"/> for "go to source" when the step's source is
///   known;</item>
///   <item>the <see cref="DiscoveredTestNodeStateProperty"/>.</item>
/// </list>
/// This is a pure function (no MTP session machinery), so it is unit-testable directly and reused
/// by the framework's discovery request path.
/// </remarks>
internal static class RaunDiscoverer
{
    /// <summary>
    /// Builds the per-step <see cref="TestNode"/> list for one scenario, keeping only the steps
    /// <paramref name="selector"/> matches (all of them when it is <see langword="null"/>).
    /// </summary>
    public static IReadOnlyList<TestNode> BuildNodes(ScenarioDefinition definition, NodeSelector? selector = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var labels = StepNumbering.Compute(definition);
        var nodes = new List<TestNode>(definition.Nodes.Count);
        foreach (var step in definition.Nodes)
        {
            // Merge/pass-through nodes are graph plumbing, not business steps: discovering them would
            // put "«merge appt»" in the user's test list. The HTML report keeps them.
            if (step.IsSynthetic)
            {
                continue;
            }

            if (selector is not null && !selector.Matches(definition, step))
            {
                continue;
            }

            nodes.Add(BuildNode(definition, step, labels));
        }

        return nodes;
    }

    /// <summary>Builds the <see cref="TestNode"/> for a single scenario step.</summary>
    public static TestNode BuildNode(ScenarioDefinition definition, ScenarioNode step, IReadOnlyDictionary<int, string> labels)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(labels);

        var node = new TestNode
        {
            Uid = StepUid.Of(definition, step),
            DisplayName = StepNumbering.Format(labels, step, step.DisplayNameTemplate),
        };

        node.Properties.Add(DiscoveredTestNodeStateProperty.CachedInstance);
        node.Properties.Add(ScenarioTestIdentity.Create(definition));

        if (TryMakeFileLocation(step, out var location))
        {
            node.Properties.Add(location);
        }

        return node;
    }

    private static bool TryMakeFileLocation(ScenarioNode step, out TestFileLocationProperty location)
    {
        // SourceLine is 1-based, or 0 when unknown; without a file there is nothing to navigate to.
        if (!string.IsNullOrEmpty(step.SourceFile) && step.SourceLine > 0)
        {
            var position = new LinePosition(step.SourceLine, 0);
            location = new TestFileLocationProperty(step.SourceFile, new LinePositionSpan(position, position));
            return true;
        }

        location = null!;
        return false;
    }
}
