using Raun.Model;

namespace Raun.Running;

/// <summary>
/// Decides which of a scenario's steps a discovery or run request selects. One abstraction serves
/// discovery, scenario selection and target expansion, so "selected" means the same thing in all
/// three. A <see langword="null"/> selector is the runner's no-filter case and selects everything;
/// callers check for null rather than allocating a match-all instance.
/// </summary>
public abstract class NodeSelector
{
    /// <summary>True when <paramref name="step"/> of <paramref name="definition"/> is selected.</summary>
    public abstract bool Matches(ScenarioDefinition definition, ScenarioNode step);
}

/// <summary>
/// Selects steps by uid (<c>{ScenarioId}:{StepId}</c>, see <see cref="StepUid"/>): the identity
/// every host and sink shares, and so the one way to name a step that needs no host.
/// </summary>
public sealed class UidNodeSelector : NodeSelector
{
    private readonly IReadOnlySet<string> _uids;

    public UidNodeSelector(IReadOnlySet<string> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);
        _uids = uids;
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step)
        => _uids.Contains(StepUid.Of(definition, step));
}
