namespace Raun.Model;

/// <summary>
/// The stable identity of one step node as a runner sees it: <c>{ScenarioId}:{StepId}</c>. One
/// spelling, defined once, so a filter built from a discovered node resolves to the node the run
/// reports, whichever adapter or sink produced either side.
/// </summary>
public static class StepUid
{
    /// <summary>The uid for <paramref name="stepId"/> of scenario <paramref name="scenarioId"/>.</summary>
    public static string Of(string scenarioId, string stepId) => scenarioId + ":" + stepId;

    /// <summary>The uid for <paramref name="step"/> of <paramref name="definition"/>.</summary>
    public static string Of(ScenarioDefinition definition, ScenarioNode step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);
        return Of(definition.ScenarioId, step.StepId);
    }
}
