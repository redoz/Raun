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
    readonly Dictionary<(ResourceIdentity Identity, Type? ActionType), int> _index = [];
    readonly ResourceIdentityResolver _resolver;
    readonly ResourceLabelScope _labels;
    readonly TimeProvider _timeProvider;
    readonly string _stepId;
    string _stepDisplayName;
    ResourceLedger? _ledger;
    int _nodeIndex;
    Action<LifecycleVerb, ResourceIdentity, ResourceConflictException?, string?, string?, ResourceAction?>? _observer;

    /// <summary>Creates a tracer bound to one step, resolving identities via <paramref name="resolver"/>.</summary>
    public ResourceContext(
        string stepId,
        string stepDisplayName,
        ResourceIdentityResolver resolver,
        TimeProvider timeProvider)
        : this(stepId, stepDisplayName, resolver, timeProvider, ResourceLabelScope.Standalone()) { }

    internal ResourceContext(string stepId, string stepDisplayName, ResourceIdentityResolver resolver,
        TimeProvider timeProvider, ResourceLabelScope labels)
    {
        _labels = labels;
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _stepId = stepId;
        _stepDisplayName = stepDisplayName;
        _resolver = resolver;
        _timeProvider = timeProvider;
    }

    internal void SetDisplayName(string displayName)
    {
        lock (_lock) { _stepDisplayName = displayName; }
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

    /// <summary>Records a domain action on a resource instance.</summary>
    public ValueTask Apply<T>(ResourceAction action, T resource) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(action);
        return resource is ResourceIdentity identity ? Apply(action, identity)
            : Record(action.Effect, _resolver.Resolve(resource), resource, typeof(T), action);
    }

    /// <summary>Records a domain action by key, without an object snapshot.</summary>
    public ValueTask Apply(ResourceAction action, ResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Record(action.Effect, identity, data: null, action: action);
    }

    /// <summary>Records a new resource by key, without an object snapshot.</summary>
    public ValueTask Create(ResourceIdentity identity) => Record(LifecycleVerb.Create, identity, data: null);

    /// <summary>Records an existing resource by key, without an object snapshot.</summary>
    public ValueTask Load(ResourceIdentity identity) => Record(LifecycleVerb.Load, identity, data: null);

    /// <summary>Records bringing a new <paramref name="resource"/> into existence (exclusive).</summary>
    public ValueTask Create<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Create, _resolver.Resolve(resource), resource, typeof(T));

    /// <summary>Records fetching an existing resource of type <typeparamref name="T"/> by key (shared, no instance).</summary>
    public ValueTask Load<T>(ResourceKey key)
        where T : notnull
        => Record(LifecycleVerb.Load, new ResourceIdentity(typeof(T), key), data: null);

    /// <summary>Records fetching an existing resource instance (shared, with data snapshot).</summary>
    public ValueTask Load<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Load, _resolver.Resolve(resource), resource, typeof(T));

    /// <summary>Records a shared use of an existing resource by identity, without an instance.</summary>
    public ValueTask Read(ResourceIdentity identity)
        => Record(LifecycleVerb.Read, identity, data: null);

    /// <summary>Records a shared use of an in-scope <paramref name="resource"/>.</summary>
    public ValueTask Read<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Read, _resolver.Resolve(resource), resource, typeof(T));

    /// <summary>Records a durable reference to a resource by identity, plus lineage from each subject.</summary>
    public ValueTask Reference(ResourceIdentity target, params object[] subjects)
    {
        return RecordRelated(LifecycleVerb.Reference, target, null, null, subjects);
    }

    /// <summary>Records the produced resource keeping a durable reference to <paramref name="resource"/> (shared),
    /// plus a lineage relation from each subject to it.</summary>
    public ValueTask Reference<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        return RecordRelated(LifecycleVerb.Reference, target, resource, typeof(T), subjects);
    }

    /// <summary>Records consuming a resource by identity, plus lineage from each subject.</summary>
    public ValueTask Consume(ResourceIdentity target, params object[] subjects)
    {
        return RecordRelated(LifecycleVerb.Consume, target, null, null, subjects);
    }

    /// <summary>Records consuming/using-up <paramref name="resource"/> into the produced resource (shared),
    /// plus a lineage relation from each subject to it.</summary>
    public ValueTask Consume<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        return RecordRelated(LifecycleVerb.Consume, target, resource, typeof(T), subjects);
    }

    /// <summary>Records a mutation of an existing resource by identity (exclusive, without an instance).</summary>
    public ValueTask Edit(ResourceIdentity identity)
        => Record(LifecycleVerb.Edit, identity, data: null);

    /// <summary>Records a mutation of an existing <paramref name="resource"/> (exclusive).</summary>
    public ValueTask Edit<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Edit, _resolver.Resolve(resource), resource, typeof(T));

    /// <summary>Records removing a resource by identity (exclusive, without an instance).</summary>
    public ValueTask Delete(ResourceIdentity identity)
        => Record(LifecycleVerb.Delete, identity, data: null);

    /// <summary>Records removing a <paramref name="resource"/> (exclusive).</summary>
    public ValueTask Delete<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Delete, _resolver.Resolve(resource), resource, typeof(T));

    /// <summary>Record a relationship after validating its canonical endpoints.</summary>
    /// <remarks>The expected type identifies a canonical group, including all mapped representations. Key-only targets have no data snapshot.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public ValueTask Relate<TSource, TTarget>(ResourceRelation relation, TSource source, TTarget target, Type? expectedResourceType = null)
        where TSource : notnull where TTarget : notnull
    {
        if (!Enum.IsDefined(relation)) { throw new ArgumentOutOfRangeException(nameof(relation)); }
        return RelateCore(relation, source, target, expectedResourceType, null);
    }

    /// <summary>Record a named Reference or Consume action and its relationship.</summary>
    /// <remarks>
    /// The action applies to the target. Both relationship kinds use shared access and do not delete the target.
    /// If the same named action has the same snapshot and label, Raun adds the relationship without a second log or trace event.
    /// Changed observations and explicit Apply calls still produce events.
    /// </remarks>
    public ValueTask Relate<TSource, TTarget>(ResourceAction action, TSource source, TTarget target, Type? expectedResourceType = null)
        where TSource : notnull where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(action);
        var relation = action.Effect switch
        {
            LifecycleVerb.Reference => ResourceRelation.Reference,
            LifecycleVerb.Consume => ResourceRelation.Consume,
            _ => throw new ArgumentException("Use a Reference or Consume action for a relationship.", nameof(action)),
        };
        return RelateCore(relation, source, target, expectedResourceType, action);
    }

    private ValueTask RelateCore<TSource, TTarget>(ResourceRelation relation, TSource source, TTarget target, Type? expectedResourceType, ResourceAction? action)
        where TSource : notnull where TTarget : notnull
    {
        var sourceIdentity = _resolver.Resolve(source);
        var targetIdentity = _resolver.Resolve(target);
        if (sourceIdentity == targetIdentity) { throw new ArgumentException("A resource relationship cannot target its own canonical identity.", nameof(target)); }
        if (expectedResourceType is not null
            && _resolver.Normalize(new ResourceIdentity(expectedResourceType, targetIdentity.Key)).Kind != targetIdentity.Kind)
        { throw new ArgumentException("The relationship target does not belong to the expected canonical resource group.", nameof(target)); }
        var verb = relation == ResourceRelation.Reference ? LifecycleVerb.Reference : LifecycleVerb.Consume;
        return RecordRelated(verb, targetIdentity, target is ResourceIdentity ? null : target, typeof(TTarget), [sourceIdentity], action);
    }

    private ValueTask RecordRelated(LifecycleVerb kind, ResourceIdentity target, object? data, Type? sourceType, object[] subjects, ResourceAction? action = null)
    {
        target = _resolver.Normalize(target);
        var identities = (subjects ?? []).Select(subject => subject is ResourceIdentity identity
            ? _resolver.Normalize(identity) : _resolver.Resolve(subject.GetType(), subject)).Distinct().ToArray();
        lock (_lock)
        {
            foreach (var source in identities)
            {
                if (source == target) { throw new ArgumentException("A relationship cannot target its own canonical identity.", nameof(subjects)); }
                if (_lineage.Exists(edge => edge.Subject == source && edge.Target == target && edge.Kind != kind))
                { throw new ArgumentException("Conflicting relationship kinds name the same canonical endpoints.", nameof(subjects)); }
            }
            // Record claims first: a refused shared target claim must not leave accepted lineage.
            var result = Record(kind, target, data, sourceType, action, reuseObservation: action is not null);
            foreach (var source in identities)
            {
                if (!_lineage.Exists(edge => edge.Subject == source && edge.Target == target && edge.Kind == kind))
                { _lineage.Add(new ResourceLineageRelation { Subject = source, Target = target, Kind = kind }); }
            }
            return result;
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
        => _observer = (verb, identity, conflict, snapshot, label, _) => observer(verb, identity, conflict, snapshot, label);

    internal void AttachActionObserver(Action<LifecycleVerb, ResourceIdentity, ResourceConflictException?, string?, string?, ResourceAction?> observer)
        => _observer = observer;

    ValueTask Record(LifecycleVerb verb, ResourceIdentity identity, object? data, Type? sourceType = null, ResourceAction? action = null, bool reuseObservation = false)
    {
        identity = _resolver.Normalize(identity);
        if (action?.AttributeType is null) { action = null; }
        var effectKey = (identity, action?.AttributeType);
        // The ledger first: a refused claim is a ResourceConflictException that fails the step, and it
        // records no effect — the exception carries the identity instead.
        try
        {
            _ledger?.Claim(_nodeIndex, _stepDisplayName, identity, verb);
        }
        catch (ResourceConflictException ex)
        {
            _observer?.Invoke(verb, identity, ex, null, null, action);
            throw;
        }

        var snapshot = ResourcePresentation.Capture(data);
        var label = _labels.Resolve(_resolver, identity, sourceType ?? data?.GetType(), data);
        var notify = true;
        if (reuseObservation)
        {
            lock (_lock)
            {
                // Attaching lineage to an unchanged named action is not a second observation.
                // Apply calls and changed snapshots must still notify logs and telemetry.
                if (_index.TryGetValue(effectKey, out var existingIndex))
                {
                    var existing = _effects[existingIndex];
                    notify = existing.Verb != verb || existing.DataSnapshot != snapshot || existing.Label != label;
                }
            }
        }
        if (notify) { _observer?.Invoke(verb, identity, null, snapshot, label, action); }

        lock (_lock)
        {
            if (_index.TryGetValue(effectKey, out int idx))
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
                _index[effectKey] = _effects.Count;
                _effects.Add(new ResourceEffect
                {
                    Verb = verb,
                    Action = action,
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
