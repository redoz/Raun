using Microsoft.Testing.Platform.Requests;
using Raun.Model;
using Raun.Running;

namespace Raun.Mtp;

/// <summary>
/// Adapts the platform's <c>--treenode-filter</c> to Raun's selection: each step is offered as its
/// encoded path plus the properties a <c>[key=value]</c> expression can match.
/// </summary>
#pragma warning disable TPEXP // TreeNodeFilter is an experimental Microsoft.Testing.Platform API.
internal sealed class TreeNodeSelector : NodeSelector
{
    private readonly TreeNodeFilter _filter;

    public TreeNodeSelector(TreeNodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step)
        => _filter.MatchesFilter(
            ScenarioNodePath.For(definition, step),
            ScenarioNodePath.Properties(definition, step));
}
#pragma warning restore TPEXP
