using System.Collections;
using Raun.Model;

namespace Raun;

/// <summary>Records a step's resource operations and role-named links.</summary>
/// <remarks>Effects declare access for conflict validation. Links declare no access. Step code performs external operations.</remarks>
public sealed class ResourceContext
{
    readonly object _lock = new();
    readonly List<ResourceObservation> _effects = [];
    readonly List<ResourceLink> _links = [];
    readonly Dictionary<(ResourceIdentity Identity, Type? ActionType), int> _index = [];
    readonly ResourceIdentityResolver _resolver;
    readonly ResourceLabelScope _labels;
    readonly TimeProvider _timeProvider;
    readonly string _stepId;
    string _stepDisplayName;
    ResourceLedger? _ledger;
    int _nodeIndex;
    Action<ResourceEffect, ResourceIdentity, ResourceConflictException?, string?, string?, ResourceAction?>? _observer;

    /// <summary>Creates a recorder for one step.</summary>
    public ResourceContext(string stepId, string stepDisplayName, ResourceIdentityResolver resolver, TimeProvider timeProvider)
        : this(stepId, stepDisplayName, resolver, timeProvider, ResourceLabelScope.Standalone()) { }

    internal ResourceContext(string stepId, string stepDisplayName, ResourceIdentityResolver resolver, TimeProvider timeProvider, ResourceLabelScope labels)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _stepId = stepId; _stepDisplayName = stepDisplayName; _resolver = resolver; _timeProvider = timeProvider; _labels = labels;
    }

    internal void SetDisplayName(string displayName) { lock (_lock) { _stepDisplayName = displayName; } }
    /// <summary>The frozen operation observations recorded by this step.</summary>
    public IReadOnlyList<ResourceObservation> Effects { get { lock (_lock) { return _effects.ToArray(); } } }
    /// <summary>The role-named links recorded by this step.</summary>
    public IReadOnlyList<ResourceLink> Links { get { lock (_lock) { return _links.ToArray(); } } }
    /// <summary>Registers a key selector for an external representation.</summary>
    public void Identify<T>(Func<T, ResourceKey> selector) => _resolver.Identify(selector);
    /// <summary>Registers a label selector for an external representation.</summary>
    public void Label<T>(Func<T, string?> selector) => _resolver.Label(selector);

    /// <summary>Records a domain action and its implied access on one value or a supported collection.</summary>
    public ValueTask Apply<T>(ResourceAction action, T resource) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(action);
        return RecordValue(action.Effect, resource, action);
    }
    /// <summary>Records Create with exclusive access.</summary>
    public ValueTask Create<T>(T resource) where T : notnull => RecordValue(ResourceEffect.Create, resource);
    /// <summary>Records Read by resource type and key, with shared access and no snapshot.</summary>
    public ValueTask Read<T>(ResourceKey key) where T : notnull => Record(ResourceEffect.Read, new ResourceIdentity(typeof(T), key), null);
    /// <summary>Records Read with shared access.</summary>
    public ValueTask Read<T>(T resource) where T : notnull => RecordValue(ResourceEffect.Read, resource);
    /// <summary>Records Update with exclusive access.</summary>
    public ValueTask Update<T>(T resource) where T : notnull => RecordValue(ResourceEffect.Update, resource);
    /// <summary>Records Delete with exclusive access.</summary>
    public ValueTask Delete<T>(T resource) where T : notnull => RecordValue(ResourceEffect.Delete, resource);

    private IEnumerable<(Type Type, object Value)> Values<T>(T resource) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(resource);
        var type = typeof(T);
        if (resource is IEnumerable values && resource is not string && !_resolver.HasIdentity(type))
        {
            var sequence = type.GetInterfaces().Append(type).FirstOrDefault(contract => contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            var elementType = type.IsArray ? type.GetElementType() : sequence?.GenericTypeArguments[0];
            foreach (var item in values)
            {
                var value = item ?? throw new ArgumentException("A resource collection cannot contain null.", nameof(resource));
                yield return (elementType ?? value.GetType(), value);
            }
        }
        else { yield return (type, resource); }
    }

    private async ValueTask RecordValue<T>(ResourceEffect effect, T resource, ResourceAction? action = null) where T : notnull
    {
        foreach (var (sourceType, value) in Values(resource))
        {
            var identity = value is ResourceIdentity key ? key : _resolver.Resolve(sourceType, value);
            await Record(effect, identity, value is ResourceIdentity ? null : value, sourceType, action).ConfigureAwait(false);
        }
    }

    /// <summary>Records a named link. This operation records no effect and grants no access.</summary>
    /// <remarks>Target type validation uses canonical identity. Repeating the same source, role, and target records one link.</remarks>
    public ValueTask Link<TSource, TTarget>(string role, TSource source, TTarget target, Type? expectedResourceType = null)
        where TSource : notnull where TTarget : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var pending = new List<ResourceLink>();
        foreach (var (sourceType, sourceValue) in Values(source))
        {
            var sourceIdentity = sourceValue is ResourceIdentity sourceKey ? _resolver.Normalize(sourceKey) : _resolver.Resolve(sourceType, sourceValue);
            foreach (var (targetType, targetValue) in Values(target))
            {
                var targetIdentity = targetValue is ResourceIdentity targetKey ? _resolver.Normalize(targetKey) : _resolver.Resolve(targetType, targetValue);
                if (sourceIdentity == targetIdentity) { throw new ArgumentException("A link cannot target its source identity.", nameof(target)); }
                if (expectedResourceType is not null && _resolver.Normalize(new ResourceIdentity(expectedResourceType, targetIdentity.Key)).Kind != targetIdentity.Kind)
                { throw new ArgumentException("The link target does not match its canonical resource expectation.", nameof(target)); }
                pending.Add(new ResourceLink { Source = sourceIdentity, Role = role, Target = targetIdentity });
            }
        }
        lock (_lock) { foreach (var link in pending) { if (!_links.Contains(link)) { _links.Add(link); } } }
        return ValueTask.CompletedTask;
    }

    /// <summary>Records a link using the owning action's declared role schema. This operation grants no access.</summary>
    public ValueTask Link<TSource, TTarget>(ResourceAction owner, string role, TSource source, TTarget target)
        where TSource : notnull where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(owner);
        var definition = owner.GetRelationship(role);
        return Link(role, source, target, definition.ResourceType);
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
    internal void AttachObserver(Action<ResourceEffect, ResourceIdentity, ResourceConflictException?, string?, string?> observer)
        => _observer = (verb, identity, conflict, snapshot, label, _) => observer(verb, identity, conflict, snapshot, label);

    internal void AttachActionObserver(Action<ResourceEffect, ResourceIdentity, ResourceConflictException?, string?, string?, ResourceAction?> observer)
        => _observer = observer;

    ValueTask Record(ResourceEffect verb, ResourceIdentity identity, object? data, Type? sourceType = null, ResourceAction? action = null)
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
        _observer?.Invoke(verb, identity, null, snapshot, label, action);

        lock (_lock)
        {
            if (_index.TryGetValue(effectKey, out int idx))
            {
                // Dedup: keep the stronger verb; use latest non-null data.
                // Timestamp/StepId/StepDisplayName are intentionally preserved from the first-seen touch
                // so later same-step calls don't reset the clock.
                ResourceObservation existing = _effects[idx];
                ResourceEffect stronger = verb.Precedence() > existing.Effect.Precedence() ? verb : existing.Effect;
                _effects[idx] = existing with
                {
                    Effect = stronger, Data = data ?? existing.Data,
                    DataSnapshot = data is null ? existing.DataSnapshot : snapshot,
                    DataSnapshotCaptured = true,
                    Label = data is null ? existing.Label : label,
                };
            }
            else
            {
                _index[effectKey] = _effects.Count;
                _effects.Add(new ResourceObservation
                {
                    Effect = verb,
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
