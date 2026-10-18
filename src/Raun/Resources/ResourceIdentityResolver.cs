using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Raun;

/// <summary>
/// Resolves a resource instance to its <see cref="ResourceIdentity"/> via the layered key chain
/// (first match wins), per the resourcing design:
/// <list type="number">
/// <item><description><see cref="IResource{TSelf}"/>'s static-abstract <c>KeyFor</c> — the runtime
/// form of a <c>[ResourceKey]</c>/<c>[Resource(Key)]</c> annotation (hand-written or generator-emitted).</description></item>
/// <item><description>A registered selector (<c>Identify&lt;T&gt;(selector)</c>), for types you can't annotate.</description></item>
/// <item><description><see cref="IResourceIdentity"/>, for runtime-computed keys.</description></item>
/// <item><description>Whole-record value equality (via <see cref="object.ToString"/>), the fallback
/// for immutable identity-only records.</description></item>
/// </list>
/// Because link 1 projects from a stable key member, a <c>with</c>-edited record keeps its key.
/// </summary>
public sealed class ResourceIdentityResolver
{
    private readonly ResourceIdentityGroups _groups = new();

    public ResourceIdentification Identify<T>() => new(this, new ResourceKind(typeof(T)));
    public ResourceIdentification Identify(string kind) => new(this, new ResourceKind(kind));
    public void ApplyMappings(IReadOnlyList<ResourceMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _groups.Apply(mappings);
    }
    public ResourceIdentity Normalize(ResourceIdentity identity) => _groups.Normalize(identity);
    internal void AddRepresentation(ResourceKind kind, Type source, Func<object, ResourceKey> key) => _groups.Add(kind, source, key);

    private readonly ConcurrentDictionary<Type, Func<object, ResourceKey>> _selectors = new();
    private readonly ConcurrentDictionary<Type, Func<object, ResourceKey>?> _keyForInvokers = new();

    private readonly ConcurrentDictionary<Type, Func<object, string?>> _labels = new();
    private readonly ConcurrentDictionary<Type, Func<object, string?>> _labelInvokers = new();

    /// <summary>Registers a label selector. The last registration for a type wins.</summary>
    public void Label<T>(Func<T, string?> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _labels[typeof(T)] = instance => selector((T)instance);
    }

    internal string ResolveLabel(ResourceIdentity identity, object instance)
        => ResolveLabel(identity, instance.GetType(), instance);

    internal string ResolveLabel(ResourceIdentity identity, Type sourceType, object instance)
    {
        return TryResolveSourceLabel(sourceType, instance, out var label) ? label : ResourcePresentation.Display(identity);
    }

    internal bool TryResolveSourceLabel(Type sourceType, object instance, out string label)
    {
        if (_labels.TryGetValue(sourceType, out var selector)
            && selector(instance) is { } registered && !string.IsNullOrWhiteSpace(registered))
        {
            label = registered;
            return true;
        }
        var alternate = _groups.Snapshot(sourceType).Label?.Invoke(instance);
        if (!string.IsNullOrWhiteSpace(alternate)) { label = alternate; return true; }
        var self = _labelInvokers.GetOrAdd(sourceType, BuildLabelInvoker)(instance);
        label = self ?? string.Empty;
        return !string.IsNullOrWhiteSpace(self);
    }

    private static bool HasContract(Type type)
        => Array.Exists(type.GetInterfaces(), i => i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IResource<>) && i.GenericTypeArguments[0] == type);

    private static Func<object, string?> BuildLabelInvoker(Type type)
    {
        if (!HasContract(type))
        {
            return _ => null;
        }

        // Dispatch through the interface, including its default and explicit implementations.
        return typeof(ResourceIdentityResolver).GetMethod(nameof(InvokeLabel), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).CreateDelegate<Func<object, string?>>();
    }

    private static string? InvokeLabel<T>(object value) where T : IResource<T> => T.LabelFor((T)value);

    /// <summary>
    /// Registers a key selector for <typeparamref name="T"/> (chain link 2), for types that can't
    /// carry a <c>KeyFor</c>. The last registration for a type wins.
    /// </summary>
    public void Identify<T>(Func<T, ResourceKey> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selectors[typeof(T)] = instance => selector((T)instance);
    }

    /// <summary>Resolves the identity of <paramref name="instance"/> using its static type.</summary>
    public ResourceIdentity Resolve<T>(T instance)
        where T : notnull
        => Resolve(typeof(T), instance);

    /// <summary>Resolves the identity of <paramref name="instance"/> for a runtime <paramref name="type"/>.</summary>
    public ResourceIdentity Resolve(Type type, object instance)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(instance);
        if (instance is ResourceIdentity supplied && (type == typeof(ResourceIdentity) || Nullable.GetUnderlyingType(type) == typeof(ResourceIdentity)))
        {
            return Normalize(supplied);
        }
        var snapshot = _groups.Snapshot(type);
        var key = snapshot.Key is { } extractor ? extractor(instance) : ResolveKey(type, instance);
        var identity = new ResourceIdentity(snapshot.Kind, key);
        if (key.Value is not null) { _groups.CompleteResolution(snapshot.Revision); }
        return identity;
    }

    internal bool HasIdentity(Type type) => _groups.Snapshot(type).Key is not null
        || _keyForInvokers.GetOrAdd(type, BuildKeyForInvoker) is not null || _selectors.ContainsKey(type)
        || typeof(IResourceIdentity).IsAssignableFrom(type);

    /// <summary>Resolves only explicit resource contracts or selectors on the declared type.</summary>
    public bool TryResolve(Type declaredType, object instance, out ResourceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(declaredType);
        ArgumentNullException.ThrowIfNull(instance);
        var type = declaredType;
        if ((type == typeof(ResourceIdentity) || Nullable.GetUnderlyingType(type) == typeof(ResourceIdentity))
            && instance is ResourceIdentity supplied)
        {
            identity = Normalize(supplied);
            return true;
        }
        if (_groups.Snapshot(type).Key is not null
            || _keyForInvokers.GetOrAdd(type, BuildKeyForInvoker) is not null
            || _selectors.ContainsKey(type)
            || typeof(IResourceIdentity).IsAssignableFrom(type))
        {
            identity = Resolve(type, instance);
            return true;
        }
        identity = default;
        return false;
    }

    private ResourceKey ResolveKey(Type type, object instance)
    {
        // 1. IResource<TSelf>.KeyFor (the runtime form of [ResourceKey]/[Resource(Key)]).
        var keyFor = _keyForInvokers.GetOrAdd(type, BuildKeyForInvoker);
        if (keyFor is not null)
        {
            return keyFor(instance);
        }

        // 2. Registered selector.
        if (_selectors.TryGetValue(type, out var selector))
        {
            return selector(instance);
        }

        // 3. Runtime-computed key.
        if (instance is IResourceIdentity identity)
        {
            return identity.GetResourceKey();
        }

        // 4. Whole-record value-equality fallback.
        return new ResourceKey(instance.ToString() ?? type.FullName ?? type.Name);
    }

    private static Func<object, ResourceKey>? BuildKeyForInvoker(Type type)
    {
        if (!HasContract(type))
        {
            return null;
        }

        // Dispatch through the contract, including explicit implementations, as labels do.
        return typeof(ResourceIdentityResolver).GetMethod(nameof(InvokeKey), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).CreateDelegate<Func<object, ResourceKey>>();
    }

    private static ResourceKey InvokeKey<T>(object value) where T : IResource<T> => T.KeyFor((T)value);
}
