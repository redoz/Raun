using System;

namespace Raun;

/// <summary>
/// Marks a <b>token type</b> that scenarios contend for in the system under test — one database
/// a few scenarios must have to themselves, a pool of three SMTP servers, a serial port. The type
/// is a name, never an instance: Raun does not construct, inject, or scope it. Carry exactly one
/// of <see cref="ExclusiveResourceAttribute"/>, <see cref="SharedResourceAttribute"/>,
/// <see cref="PooledResourceAttribute"/>, and declare a need with <see cref="UsesAttribute{T}"/>.
/// Not to be confused with <see cref="IResource{TSelf}"/>, which is data a step traces.
/// </summary>
#pragma warning disable CA1040 // Avoid empty interfaces — a deliberate marker, like IPhase; it enables the compile-time constraint on Uses<T>.
public interface IContendedResource;
#pragma warning restore CA1040

/// <summary>One holder at a time. Every use, shared or exclusive, takes the single slot.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class ExclusiveResourceAttribute : Attribute;

/// <summary>Any number of shared holders; an exclusive use waits for zero holders and blocks new ones.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class SharedResourceAttribute : Attribute;

/// <summary>Up to <see cref="Capacity"/> shared holders; an exclusive use takes every slot.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class PooledResourceAttribute : Attribute
{
    /// <summary>A pool of <paramref name="capacity"/> holders.</summary>
    /// <param name="capacity">How many scenarios may hold the resource at once; at least 1.</param>
    public PooledResourceAttribute(int capacity) => Capacity = capacity;

    /// <summary>How many scenarios may hold the resource at once.</summary>
    public int Capacity { get; }
}

/// <summary>
/// Declares that the scenario(s) this is placed on need <typeparamref name="T"/>. On a DSL step
/// method it means every scenario that calls the step; on a <c>[Scenario]</c> method, that
/// scenario; on a class, every scenario declared in it; on the assembly, every scenario. All sites
/// are additive; per type, <see cref="LockMode.Exclusive"/> wins. The whole set is acquired
/// atomically before the scenario starts and released after its teardown.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class UsesAttribute<T> : Attribute
    where T : IContendedResource
{
    /// <summary>A shared use: one slot of the resource.</summary>
    public UsesAttribute()
    {
    }

    /// <summary>A use with an explicit mode.</summary>
    /// <param name="mode"><see cref="LockMode.Shared"/> takes one slot; <see cref="LockMode.Exclusive"/> takes every slot.</param>
    public UsesAttribute(LockMode mode) => Mode = mode;

    /// <summary>The access this use needs.</summary>
    public LockMode Mode { get; } = LockMode.Shared;
}

/// <summary>One reduced use of a contended resource by a scenario, as emitted by the generator.</summary>
/// <param name="Resource">The token type (implements <see cref="IContendedResource"/>).</param>
/// <param name="Mode">The strongest mode any site declared for this type.</param>
public readonly record struct ContendedResourceUse(Type Resource, LockMode Mode);
