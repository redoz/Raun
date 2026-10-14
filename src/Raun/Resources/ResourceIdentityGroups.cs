namespace Raun;

/// <summary>Owns configuration, equivalence roots, and the first-resolution freeze boundary.</summary>
internal sealed class ResourceIdentityGroups
{
    private readonly object _gate = new();
    private Dictionary<ResourceKind, ResourceKind> _parents = [];
    private Dictionary<Type, Func<object, ResourceKey>> _explicit = [];
    private Dictionary<Type, ResourceMapping> _defaults = [];
    private bool _frozen;
    private int _revision;

    internal void Add(ResourceKind kind, Type source, Func<object, ResourceKey> key)
    {
        lock (_gate)
        {
            EnsureMutable();
            var parents = new Dictionary<ResourceKind, ResourceKind>(_parents);
            Union(parents, kind, new ResourceKind(source));
            _parents = parents;
            _explicit[source] = key;
            _revision++;
        }
    }

    internal void Apply(IReadOnlyList<ResourceMapping> mappings)
    {
        lock (_gate)
        {
            var parents = new Dictionary<ResourceKind, ResourceKind>(_parents);
            var defaults = new Dictionary<Type, ResourceMapping>(_defaults);
            var changed = false;
            foreach (var mapping in mappings)
            {
                ArgumentNullException.ThrowIfNull(mapping);
                if (defaults.TryGetValue(mapping.SourceType, out var existing))
                {
                    if (existing.OwnerType != mapping.OwnerType)
                    {
                        throw new InvalidOperationException($"Resource representation '{mapping.SourceType}' has conflicting owners '{existing.OwnerType}' and '{mapping.OwnerType}'.");
                    }
                    continue;
                }
                EnsureMutable();
                Union(parents, new ResourceKind(mapping.OwnerType), new ResourceKind(mapping.SourceType));
                defaults.Add(mapping.SourceType, mapping);
                changed = true;
            }
            if (changed)
            {
                _parents = parents;
                _defaults = defaults;
                _revision++;
            }
        }
    }

    internal (ResourceKind Kind, Func<object, ResourceKey>? Key, Func<object, string?>? Label, int Revision) Snapshot(Type type)
    {
        lock (_gate)
        {
            _defaults.TryGetValue(type, out var mapping);
            _explicit.TryGetValue(type, out var explicitKey);
            return (Root(_parents, new ResourceKind(type)), explicitKey ?? mapping?.Key, mapping?.Label, _revision);
        }
    }

    internal ResourceIdentity Normalize(ResourceIdentity identity)
    {
        lock (_gate)
        {
            if (!identity.Kind.IsValid || identity.Key.Value is null) { return identity; }
            _frozen = true;
            return new ResourceIdentity(Root(_parents, identity.Kind), identity.Key);
        }
    }

    internal void CompleteResolution(int revision)
    {
        lock (_gate)
        {
            if (_revision != revision)
            {
                throw new InvalidOperationException("Resource mappings changed while a key was being resolved. Configure mappings before execution.");
            }
            _frozen = true;
        }
    }

    private void EnsureMutable()
    {
        if (_frozen)
        {
            throw new InvalidOperationException("Resource mappings are frozen after the first successful identity resolution. Configure mappings before execution.");
        }
    }

    private static ResourceKind Root(Dictionary<ResourceKind, ResourceKind> parents, ResourceKind kind)
    {
        while (parents.TryGetValue(kind, out var parent)) { kind = parent; }
        return kind;
    }

    private static void Union(Dictionary<ResourceKind, ResourceKind> parents, ResourceKind target, ResourceKind source)
    {
        var targetRoot = Root(parents, target);
        var sourceRoot = Root(parents, source);
        if (targetRoot == sourceRoot) { return; }
        if (targetRoot.Name is not null && sourceRoot.Name is not null)
        {
            throw new InvalidOperationException($"Resource kinds '{targetRoot}' and '{sourceRoot}' cannot be merged because both have distinct names.");
        }
        if (sourceRoot.Name is not null) { parents[targetRoot] = sourceRoot; }
        else { parents[sourceRoot] = targetRoot; }
    }
}
