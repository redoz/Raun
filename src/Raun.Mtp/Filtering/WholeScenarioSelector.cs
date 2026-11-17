using Raun.Model;
using Raun.Running;

namespace Raun.Mtp;

internal sealed class WholeScenarioSelector : NodeSelector
{
    private readonly Func<ScenarioDefinition, bool> _selects;

    private WholeScenarioSelector(Func<ScenarioDefinition, bool> selects) => _selects = selects;

    public static WholeScenarioSelector ForScenarioIds(IReadOnlySet<string> scenarioIds)
    {
        ArgumentNullException.ThrowIfNull(scenarioIds);
        return new WholeScenarioSelector(definition => scenarioIds.Contains(definition.ScenarioId));
    }

    public static WholeScenarioSelector Widening(NodeSelector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new WholeScenarioSelector(definition =>
        {
            foreach (var step in definition.Nodes)
            {
                if (!step.IsSynthetic && inner.Matches(definition, step))
                {
                    return true;
                }
            }

            return false;
        });
    }

    public bool Selects(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _selects(definition);
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step) => Selects(definition);
}
