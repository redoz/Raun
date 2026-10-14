namespace Raun;

/// <summary>
/// Type-safe resource identity via CRTP: the domain record IS the identity, projecting its key at
/// the TYPE level (static-abstract <see cref="KeyFor"/>) so generic trace code needs no instance
/// and no reflection. Implemented by hand — a one-line <c>KeyFor</c> on the record. Because the key
/// is projected from a stable member, a <c>with</c>-edited copy keeps its identity. Types you cannot
/// annotate use <see cref="IResourceIdentity"/> or a selector registered via
/// <see cref="ResourceContext.Identify{T}"/> instead.
/// </summary>
/// <typeparam name="TSelf">The implementing type itself (the curiously-recurring parameter).</typeparam>
public interface IResource<TSelf>
    where TSelf : IResource<TSelf>
{
    /// <summary>Projects the resource key for <paramref name="instance"/>.</summary>
    static abstract ResourceKey KeyFor(TSelf instance);

    /// <summary>Optional human-readable label, captured at each resource reference.</summary>
    static virtual string? LabelFor(TSelf instance) => null;
}
