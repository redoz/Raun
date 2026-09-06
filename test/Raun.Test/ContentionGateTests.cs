using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Admission across scenarios: a scenario's whole use set is granted at once or not at all, an
/// exclusive resource has one slot, a shared resource has any number for shared uses, a pooled
/// resource has a capacity, and an exclusive use of any of them waits for zero holders.
/// </summary>
public class ContentionGateTests
{
    private static ContendedResourceUse Shared<T>() where T : IContendedResource => new(typeof(T), LockMode.Shared);
    private static ContendedResourceUse Exclusive<T>() where T : IContendedResource => new(typeof(T), LockMode.Exclusive);

    [Fact]
    public void An_empty_use_set_is_always_admitted()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([], out var refusedBy));
        Assert.Null(refusedBy);
    }

    [Fact]
    public void Exclusive_resource_has_one_slot_for_shared_and_exclusive_uses_alike()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<ExclusiveDb>()], out _));

        Assert.False(gate.TryAcquire([Shared<ExclusiveDb>()], out var refusedBy));
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.False(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));

        gate.Release([Shared<ExclusiveDb>()]);
        Assert.True(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));
    }

    [Fact]
    public void Shared_resource_admits_any_number_of_shared_uses()
    {
        var gate = new ContentionGate();
        for (var i = 0; i < 50; i++)
        {
            Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));
        }
    }

    [Fact]
    public void Exclusive_use_of_a_shared_resource_waits_for_zero_holders_and_blocks_new_ones()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));

        Assert.False(gate.TryAcquire([Exclusive<SharedCatalog>()], out var refusedBy));
        Assert.Equal(typeof(SharedCatalog), refusedBy);

        gate.Release([Shared<SharedCatalog>()]);
        Assert.True(gate.TryAcquire([Exclusive<SharedCatalog>()], out _));
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>()], out _));

        gate.Release([Exclusive<SharedCatalog>()]);
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));
    }

    [Fact]
    public void Pooled_resource_caps_shared_holders_at_its_capacity()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));

        Assert.False(gate.TryAcquire([Shared<PooledSmtp>()], out var refusedBy));
        Assert.Equal(typeof(PooledSmtp), refusedBy);

        gate.Release([Shared<PooledSmtp>()]);
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
    }

    [Fact]
    public void Exclusive_use_of_a_pooled_resource_takes_every_slot()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Exclusive<PooledSmtp>()], out _));
        Assert.False(gate.TryAcquire([Shared<PooledSmtp>()], out _));

        gate.Release([Exclusive<PooledSmtp>()]);
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
        Assert.False(gate.TryAcquire([Exclusive<PooledSmtp>()], out _));
    }

    [Fact]
    public void A_multi_use_set_is_granted_all_or_nothing()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));

        // Catalog is free, Db is not: nothing may be taken, so Catalog stays free for an exclusive use.
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>(), Shared<ExclusiveDb>()], out var refusedBy));
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.True(gate.TryAcquire([Exclusive<SharedCatalog>()], out _));
    }

    [Fact]
    public void Duplicate_types_in_one_set_reduce_with_exclusive_winning()
    {
        var reduced = ContentionGate.Reduce([Shared<SharedCatalog>(), Exclusive<SharedCatalog>(), Shared<SharedCatalog>()]);
        var use = Assert.Single(reduced);
        Assert.Equal(LockMode.Exclusive, use.Mode);

        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>(), Exclusive<SharedCatalog>()], out _));
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>()], out _)); // held exclusively, not shared
    }

    [Fact]
    public void A_type_without_exactly_one_kind_attribute_is_rejected()
    {
        var gate = new ContentionGate();

        var none = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<NoKind>()], out _));
        Assert.Contains(nameof(NoKind), none.Message, StringComparison.Ordinal);

        var two = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<TwoKinds>()], out _));
        Assert.Contains(nameof(TwoKinds), two.Message, StringComparison.Ordinal);

        var zero = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<ZeroCapacity>()], out _));
        Assert.Contains("at least 1", zero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Releasing_what_was_never_acquired_is_an_error()
    {
        var gate = new ContentionGate();
        Assert.Throws<InvalidOperationException>(() => gate.Release([Shared<SharedCatalog>()]));
    }
}
