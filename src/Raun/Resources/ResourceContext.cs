using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Raun.Model;

namespace Raun;

/// <summary>
/// The imperative resource surface hanging off <see cref="ScenarioContext.Resources"/>: each lifecycle
/// verb resolves the resource's identity and records a <see cref="ResourceEffect"/> for the report's
/// resource lane. No resource locks are taken and nothing waits; the lock here is solely for per-step
/// dedup correctness. The API is async end to end so call sites
/// (<c>await ctx.Resources.Create(user)</c>) stay stable if a verb ever has to await.
/// </summary>
public sealed class ResourceContext
{
    readonly object _lock = new();
    // Effects in first-seen order; _index maps identity → its position in _effects.
    readonly List<ResourceEffect> _effects = [];
    readonly List<ResourceLineageRelation> _lineage = [];
    readonly Dictionary<ResourceIdentity, int> _index = [];
    readonly ResourceIdentityResolver _resolver;
    readonly TimeProvider _timeProvider;
    readonly string _stepId;
    readonly string _stepDisplayName;
    ResourceLedger? _ledger;
    int _nodeIndex;
    Action<LifecycleVerb, ResourceIdentity, ResourceConflictException?, string?, string?>? _observer;

    /// <summary>Creates a tracer bound to one step, resolving identities via <paramref name="resolver"/>.</summary>
    public ResourceContext(
        string stepId,
        string stepDisplayName,
        ResourceIdentityResolver resolver,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _stepId = stepId;
        _stepDisplayName = stepDisplayName;
        _resolver = resolver;
        _timeProvider = timeProvider;
    }

    /// <summary>Effects recorded by this step's verbs, in first-seen order (one per identity).</summary>
    public IReadOnlyList<ResourceEffect> Effects
    {
        get { lock (_lock) { return _effects.ToArray(); } }
    }

    /// <summary>Lineage relations recorded by this step's producer-side References/Consumes targets.</summary>
    public IReadOnlyList<ResourceLineageRelation> Lineage
    {
        get { lock (_lock) { return _lineage.ToArray(); } }
    }

    /// <summary>
    /// Registers a key selector for <typeparamref name="T"/> (resolver chain link 2), for resource
    /// types you can't annotate with <c>[ResourceKey]</c>/<c>KeyFor</c>.
    /// </summary>
    public void Identify<T>(Func<T, ResourceKey> selector) => _resolver.Identify(selector);

    /// <summary>Registers a label selector for resource references.</summary>
    public void Label<T>(Func<T, string?> selector) => _resolver.Label(selector);

    /// <summary>Records bringing a new <paramref name="resource"/> into existence (exclusive).</summary>
    public ValueTask Create<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Create, _resolver.Resolve(resource), resource);

    /// <summary>Records fetching an existing resource of type <typeparamref name="T"/> by key (shared, no instance).</summary>
    public ValueTask Load<T>(ResourceKey key)
        where T : notnull
        => Record(LifecycleVerb.Load, new ResourceIdentity(typeof(T), key), data: null);

    /// <summary>Records fetching an existing resource instance (shared, with data snapshot).</summary>
    public ValueTask Load<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Load, _resolver.Resolve(resource), resource);

    /// <summary>Records a shared use of an in-scope <paramref name="resource"/>.</summary>
    public ValueTask Read<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Read, _resolver.Resolve(resource), resource);

    /// <summary>Records the produced resource keeping a durable reference to <paramref name="resource"/> (shared),
    /// plus a lineage relation from each subject to it.</summary>
    public ValueTask Reference<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        RecordLineage(LifecycleVerb.Reference, target, subjects);
        return Record(LifecycleVerb.Reference, target, resource);
    }

    /// <summary>Records consuming/using-up <paramref name="resource"/> into the produced resource (shared),
    /// plus a lineage relation from each subject to it.</summary>
    public ValueTask Consume<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        RecordLineage(LifecycleVerb.Consume, target, subjects);
        return Record(LifecycleVerb.Consume, target, resource);
    }

    /// <summary>Records a mutation of an existing <paramref name="resource"/> (exclusive).</summary>
    public ValueTask Edit<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Edit, _resolver.Resolve(resource), resource);

    /// <summary>Records removing a <paramref name="resource"/> (exclusive).</summary>
    public ValueTask Delete<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Delete, _resolver.Resolve(resource), resource);

    void RecordLineage(LifecycleVerb kind, ResourceIdentity target, object[] subjects)
    {
        if (subjects is null || subjects.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var subject in subjects)
            {
                var subjectIdentity = _resolver.Resolve(subject.GetType(), subject);
                _lineage.Add(new ResourceLineageRelation { Subject = subjectIdentity, Target = target, Kind = kind });
            }
        }
    }

    /// <summary>Wires this step's verbs to the scenario's conflict ledger. Called by the scheduler; a
    /// context built outside it (a DSL method under unit test) has no ledger and its verbs only trace.</summary>
    internal void AttachLedger(ResourceLedger ledger, int nodeIndex)
    {
        _ledger = ledger;
        _nodeIndex = nodeIndex;
    }

    /// <summary>Notifies <see cref="ScenarioContext"/> of each recorded effect, or of a refused claim
    /// (non-null exception), so the resource story reaches the step's log and span in order.</summary>
    internal void AttachObserver(Action<LifecycleVerb, ResourceIdentity, ResourceConflictException?, string?, string?> observer)
        => _observer = observer;

    ValueTask Record(LifecycleVerb verb, ResourceIdentity identity, object? data)
    {
        // The ledger first: a refused claim is a ResourceConflictException that fails the step, and it
        // records no effect — the exception carries the identity instead.
        try
        {
            _ledger?.Claim(_nodeIndex, _stepDisplayName, identity, verb);
        }
        catch (ResourceConflictException ex)
        {
            _observer?.Invoke(verb, identity, ex, null, null);
            throw;
        }

        var snapshot = ResourcePresentation.Capture(data);
        var label = data is null ? ResourcePresentation.Display(identity) : _resolver.ResolveLabel(identity, data);
        _observer?.Invoke(verb, identity, null, snapshot, label);

        lock (_lock)
        {
            if (_index.TryGetValue(identity, out int idx))
            {
                // Dedup: keep the stronger verb; use latest non-null data.
                // Timestamp/StepId/StepDisplayName are intentionally preserved from the first-seen touch
                // so later same-step calls don't reset the clock.
                ResourceEffect existing = _effects[idx];
                LifecycleVerb stronger = verb.Precedence() > existing.Verb.Precedence() ? verb : existing.Verb;
                _effects[idx] = existing with
                {
                    Verb = stronger, Data = data ?? existing.Data,
                    DataSnapshot = data is null ? existing.DataSnapshot : snapshot,
                    DataSnapshotCaptured = true,
                    Label = data is null ? existing.Label : label,
                };
            }
            else
            {
                _index[identity] = _effects.Count;
                _effects.Add(new ResourceEffect
                {
                    Verb = verb,
                    Identity = identity,
                    Data = data,
                    DataSnapshot = snapshot,
                    DataSnapshotCaptured = true,
                    Label = label,
                    StepId = _stepId,
                    StepDisplayName = _stepDisplayName,
                    Timestamp = _timeProvider.GetUtcNow(),
                });
            }
        }
        return ValueTask.CompletedTask;
    }
}
