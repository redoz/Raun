using Raun.Model;

namespace Raun;

/// <summary>Names observed in one execution, with visibility defined by graph order.</summary>
internal sealed class ResourceLabelMemory(IReadOnlyList<ScenarioNode> nodes)
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceIdentity, Dictionary<int, string>> _observations = [];
    private readonly bool[][] _after = ScenarioGraph.ComputeAfter(nodes);

    internal ResourceLabelScope ForStep(int index) => new(this, index);

    internal string Resolve(int reader, ResourceIdentityResolver resolver, ResourceIdentity identity, Type? sourceType, object? value)
    {
        identity = resolver.Normalize(identity);
        string? observed = null;
        if (value is not null && value is not ResourceIdentity && sourceType is not null)
        {
            try
            {
                if (resolver.TryResolveSourceLabel(sourceType, value, out var label)) { observed = label; }
            }
            catch
            {
                // A user label selector is presentation only. Keep earlier usable evidence.
            }
        }
        lock (_gate)
        {
            if (!_observations.TryGetValue(identity, out var producers))
            {
                producers = [];
                _observations[identity] = producers;
            }
            if (observed is not null) { producers[reader] = observed; }
            if (producers.TryGetValue(reader, out var own)) { return own; }
            var teardown = reader >= 0 && reader < nodes.Count && nodes[reader].IsTeardown;
            var eligible = producers.Keys.Where(p => p >= 0 && p < nodes.Count
                && reader >= 0 && reader < nodes.Count && (teardown || _after[reader][p])).ToArray();
            var selected = eligible.Where(p => !eligible.Any(other => other != p && _after[other][p]))
                .DefaultIfEmpty(-1).Max();
            return selected < 0 ? ResourcePresentation.Display(identity) : producers[selected];
        }
    }
}

internal sealed class ResourceLabelScope(ResourceLabelMemory memory, int nodeIndex)
{
    internal static ResourceLabelScope Standalone() => new ResourceLabelMemory([]).ForStep(-1);
    internal ResourceLabelScope ForStep(int index) => memory.ForStep(index);
    internal string Resolve(ResourceIdentityResolver resolver, ResourceIdentity identity, Type? sourceType, object? value)
        => memory.Resolve(nodeIndex, resolver, identity, sourceType, value);
}
