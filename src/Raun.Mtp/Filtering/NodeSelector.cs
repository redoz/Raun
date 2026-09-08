using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// Decides which of a scenario's steps a discovery or run request selects. One abstraction serves
/// discovery, scenario selection and target expansion, so "selected" means the same thing in all
/// three. A <see langword="null"/> selector is the platform's no-filter case and selects everything;
/// callers check for null rather than allocating a match-all instance.
/// </summary>
internal abstract class NodeSelector
{
    /// <summary>True when <paramref name="step"/> of <paramref name="definition"/> is selected.</summary>
    public abstract bool Matches(ScenarioDefinition definition, ScenarioNode step);
}

/// <summary>
/// Selects the step nodes a runner named by uid (<c>{ScenarioId}:{StepId}</c>) — what an IDE sends
/// when the user runs one test, and what <c>--filter-uid</c> carries.
/// </summary>
internal sealed class UidNodeSelector : NodeSelector
{
    private readonly ISet<string> _uids;

    public UidNodeSelector(ISet<string> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);
        _uids = uids;
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step)
        => _uids.Contains(StepUid.Of(definition, step));
}
