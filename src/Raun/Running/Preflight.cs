using Raun.Model;

namespace Raun.Running;

/// <summary>
/// Run-level setup, reported as its own test node — the mirror of a scenario's teardown node.
/// </summary>
/// <remarks>
/// <para>
/// Modelled as a one-node <see cref="ScenarioDefinition"/> and run through the ordinary
/// <c>ScenarioScheduler</c>. That is not a trick for its own sake: it means preflight inherits
/// timing, log collection, the ambient <see cref="ScenarioContext.Current"/>, and exception-to-status
/// mapping from code that is already tested, instead of growing a parallel reporting path.
/// </para>
/// <para>
/// The ambient context matters in particular: anything logging through
/// <c>RaunLoggerProvider</c> while preflight runs — including a system under test starting up — is
/// collected onto this node with no further wiring.
/// </para>
/// </remarks>
public static class Preflight
{
    /// <summary>Scenario id of the synthetic preflight definition; with <see cref="StepId"/> this
    /// forms the stable <c>raun:preflight</c> node uid.</summary>
    public const string ScenarioId = "raun";

    /// <summary>Step id of the single preflight node.</summary>
    public const string StepId = "preflight";

    /// <summary>Reason recorded on scenario steps that never ran because preflight failed.</summary>
    public const string FailedSkipReason = "preflight failed";

    /// <summary>Builds the one-node definition wrapping <paramref name="preflight"/>.</summary>
    public static ScenarioDefinition Definition(Func<ScenarioContext, Task> preflight) => new()
    {
        ScenarioId = ScenarioId,
        DisplayName = "Preflight",
        // Its own identity so runners group it apart from scenarios rather than filing it under an
        // empty namespace and class; stated here rather than left to an adapter's split of MethodName.
        MethodName = "Raun.Preflight",
        Namespace = "",
        TypeName = "Raun",
        Nodes =
        [
            new ScenarioNode
            {
                Index = 0,
                StepId = StepId,
                Phase = "Given",
                OperationName = "Preflight",
                DisplayNameTemplate = "Preflight",
                DependsOn = [],
                Invoke = async (_, ctx) =>
                {
                    await preflight(ctx).ConfigureAwait(false);
                    return null;
                },
            },
        ],
    };
}
