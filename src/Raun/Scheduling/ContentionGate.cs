using System;
using System.Collections.Generic;
using System.Reflection;

namespace Raun.Scheduling;

/// <summary>
/// Admission control across scenarios (concurrent-scenarios design). A scenario's whole use set is
/// granted at once or not at all, so a scenario never holds one resource while waiting for another:
/// no hold-and-wait, no deadlock. The gate never waits — the run loop retries a refused scenario
/// whenever a running one finishes. Kinds are read from the token type's attributes once and cached.
/// </summary>
public sealed class ContentionGate
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, Slots> _slots = [];
    private readonly Dictionary<Type, int> _capacities = [];

    /// <summary>Holders of one resource: shared holders, or one exclusive holder.</summary>
    private sealed class Slots
    {
        public int Holders { get; set; }

        public bool Exclusive { get; set; }
    }

    /// <summary>
    /// Tries to take every use in <paramref name="uses"/>. Returns false, taking nothing, when any
    /// of them is refused; <paramref name="refusedBy"/> then names the first refusing resource.
    /// </summary>
    /// <exception cref="InvalidOperationException">A resource type does not carry exactly one kind
    /// attribute, or its pool capacity is below 1.</exception>
    public bool TryAcquire(IReadOnlyList<ContendedResourceUse> uses, out Type? refusedBy)
    {
        ArgumentNullException.ThrowIfNull(uses);
        var reduced = Reduce(uses);

        lock (_lock)
        {
            foreach (var use in reduced)
            {
                var capacity = CapacityOf(use.Resource);
                var slots = SlotsOf(use.Resource);
                if (!Admits(slots, capacity, use.Mode))
                {
                    refusedBy = use.Resource;
                    return false;
                }
            }

            foreach (var use in reduced)
            {
                var slots = _slots[use.Resource];
                if (use.Mode == LockMode.Exclusive)
                {
                    slots.Exclusive = true;
                }
                else
                {
                    slots.Holders++;
                }
            }

            refusedBy = null;
            return true;
        }
    }

    /// <summary>Gives back every use in <paramref name="uses"/>, which must have been acquired.</summary>
    public void Release(IReadOnlyList<ContendedResourceUse> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);

        lock (_lock)
        {
            foreach (var use in Reduce(uses))
            {
                if (!_slots.TryGetValue(use.Resource, out var slots)
                    || (use.Mode == LockMode.Exclusive ? !slots.Exclusive : slots.Holders == 0))
                {
                    throw new InvalidOperationException(
                        $"'{use.Resource.Name}' was released with mode {use.Mode} but was not held that way.");
                }

                if (use.Mode == LockMode.Exclusive)
                {
                    slots.Exclusive = false;
                }
                else
                {
                    slots.Holders--;
                }
            }
        }
    }

    /// <summary>One use per type; <see cref="LockMode.Exclusive"/> wins over <see cref="LockMode.Shared"/>.</summary>
    internal static List<ContendedResourceUse> Reduce(IReadOnlyList<ContendedResourceUse> uses)
    {
        var byType = new Dictionary<Type, LockMode>();
        foreach (var use in uses)
        {
            byType[use.Resource] = byType.TryGetValue(use.Resource, out var existing) && existing == LockMode.Exclusive
                ? LockMode.Exclusive
                : use.Mode;
        }

        var reduced = new List<ContendedResourceUse>(byType.Count);
        foreach (var pair in byType)
        {
            reduced.Add(new ContendedResourceUse(pair.Key, pair.Value));
        }

        return reduced;
    }

    // capacity 0 = unbounded ([SharedResource]); 1 = [ExclusiveResource]; N = [PooledResource(N)].
    private static bool Admits(Slots slots, int capacity, LockMode mode)
    {
        if (slots.Exclusive)
        {
            return false;
        }

        if (mode == LockMode.Exclusive)
        {
            return slots.Holders == 0;
        }

        return capacity == 0 || slots.Holders < capacity;
    }

    private Slots SlotsOf(Type resource)
    {
        if (!_slots.TryGetValue(resource, out var slots))
        {
            slots = new Slots();
            _slots[resource] = slots;
        }

        return slots;
    }

    private int CapacityOf(Type resource)
    {
        if (_capacities.TryGetValue(resource, out var known))
        {
            return known;
        }

        var exclusive = resource.IsDefined(typeof(ExclusiveResourceAttribute), inherit: false);
        var shared = resource.IsDefined(typeof(SharedResourceAttribute), inherit: false);
        var pooled = resource.GetCustomAttribute<PooledResourceAttribute>(inherit: false);
        var kinds = (exclusive ? 1 : 0) + (shared ? 1 : 0) + (pooled is null ? 0 : 1);
        if (kinds != 1)
        {
            throw new InvalidOperationException(
                $"'{resource.Name}' must carry exactly one of [ExclusiveResource], [SharedResource], [PooledResource]; it carries {kinds}.");
        }

        if (pooled is { Capacity: < 1 })
        {
            throw new InvalidOperationException(
                $"'{resource.Name}' declares [PooledResource({pooled.Capacity})]; the capacity must be at least 1.");
        }

        var capacity = exclusive ? 1 : shared ? 0 : pooled!.Capacity;
        _capacities[resource] = capacity;
        return capacity;
    }
}
