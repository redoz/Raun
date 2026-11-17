using System.Runtime.CompilerServices;
using Raun.Model;
using Raun.Running;

namespace Raun.Mtp.Filtering;

/// <summary>
/// Selection for <see cref="TestGranularity.Scenario"/>: a scenario is one test, so it is selected
/// whole or not at all. Expressed as an ordinary <see cref="NodeSelector"/> that answers the same
/// for every step of a scenario, so the run loop and scheduler need to know nothing about it.
/// </summary>
internal sealed class WholeScenarioSelector : NodeSelector
{
    private readonly Func<ScenarioDefinition, bool> _selects;

    // Keyed on the definition instance: the answer is asked once per step, and for a widened filter
    // each answer walks every step. The table holds no definition alive.
    private readonly ConditionalWeakTable<ScenarioDefinition, object> _answers = [];

    private WholeScenarioSelector(Func<ScenarioDefinition, bool> selects) => _selects = selects;

    /// <summary>Selects the scenarios named by uid, which for a scenario test is its
    /// <see cref="ScenarioDefinition.ScenarioId"/>. A step uid names no scenario and selects nothing.</summary>
    public static WholeScenarioSelector ForScenarioIds(IReadOnlySet<string> scenarioIds)
    {
        ArgumentNullException.ThrowIfNull(scenarioIds);
        return new WholeScenarioSelector(definition => scenarioIds.Contains(definition.ScenarioId));
    }

    /// <summary>Selects every scenario in which <paramref name="inner"/> matches at least one real
    /// step — how a path filter written against steps applies when scenarios are the tests.</summary>
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

    /// <summary>True when <paramref name="definition"/> is selected.</summary>
    public bool Selects(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return (bool)_answers.GetValue(definition, d => _selects(d));
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step) => Selects(definition);
}
