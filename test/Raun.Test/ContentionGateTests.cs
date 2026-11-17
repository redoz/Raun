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
        var refusedBy = gate.Acquire([]);
        Assert.Null(refusedBy);
    }

    [Fact]
    public void Exclusive_resource_has_one_slot_for_shared_and_exclusive_uses_alike()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<ExclusiveDb>()]));

        var refusedBy = gate.Acquire([Shared<ExclusiveDb>()]);
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.NotNull(gate.Acquire([Exclusive<ExclusiveDb>()]));

        gate.Release([Shared<ExclusiveDb>()]);
        Assert.Null(gate.Acquire([Exclusive<ExclusiveDb>()]));
    }

    [Fact]
    public void Shared_resource_admits_any_number_of_shared_uses()
    {
        var gate = new ContentionGate();
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(gate.Acquire([Shared<SharedCatalog>()]));
        }
    }

    [Fact]
    public void Exclusive_use_of_a_shared_resource_waits_for_zero_holders_and_blocks_new_ones()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<SharedCatalog>()]));

        var refusedBy = gate.Acquire([Exclusive<SharedCatalog>()]);
        Assert.Equal(typeof(SharedCatalog), refusedBy);

        gate.Release([Shared<SharedCatalog>()]);
        Assert.Null(gate.Acquire([Exclusive<SharedCatalog>()]));
        Assert.NotNull(gate.Acquire([Shared<SharedCatalog>()]));

        gate.Release([Exclusive<SharedCatalog>()]);
        Assert.Null(gate.Acquire([Shared<SharedCatalog>()]));
    }

    [Fact]
    public void Pooled_resource_caps_shared_holders_at_its_capacity()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<PooledSmtp>()]));
        Assert.Null(gate.Acquire([Shared<PooledSmtp>()]));

        var refusedBy = gate.Acquire([Shared<PooledSmtp>()]);
        Assert.Equal(typeof(PooledSmtp), refusedBy);

        gate.Release([Shared<PooledSmtp>()]);
        Assert.Null(gate.Acquire([Shared<PooledSmtp>()]));
    }

    [Fact]
    public void Exclusive_use_of_a_pooled_resource_takes_every_slot()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Exclusive<PooledSmtp>()]));
        Assert.NotNull(gate.Acquire([Shared<PooledSmtp>()]));

        gate.Release([Exclusive<PooledSmtp>()]);
        Assert.Null(gate.Acquire([Shared<PooledSmtp>()]));
        Assert.NotNull(gate.Acquire([Exclusive<PooledSmtp>()]));
    }

    [Fact]
    public void A_multi_use_set_is_granted_all_or_nothing()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Exclusive<ExclusiveDb>()]));

        // Catalog is free, Db is not: nothing may be taken, so Catalog stays free for an exclusive use.
        var refusedBy = gate.Acquire([Shared<SharedCatalog>(), Shared<ExclusiveDb>()]);
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.Null(gate.Acquire([Exclusive<SharedCatalog>()]));
    }

    [Fact]
    public void Duplicate_types_in_one_set_reduce_with_exclusive_winning()
    {
        var reduced = ContentionGate.Reduce([Shared<SharedCatalog>(), Exclusive<SharedCatalog>(), Shared<SharedCatalog>()]);
        var use = Assert.Single(reduced);
        Assert.Equal(LockMode.Exclusive, use.Mode);

        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<SharedCatalog>(), Exclusive<SharedCatalog>()]));
        Assert.NotNull(gate.Acquire([Shared<SharedCatalog>()])); // held exclusively, not shared
    }

    [Fact]
    public void A_type_without_exactly_one_kind_attribute_is_rejected()
    {
        var gate = new ContentionGate();

        var none = Assert.Throws<InvalidOperationException>(() => gate.Acquire([Shared<NoKind>()]));
        Assert.Contains(nameof(NoKind), none.Message, StringComparison.Ordinal);

        var two = Assert.Throws<InvalidOperationException>(() => gate.Acquire([Shared<TwoKinds>()]));
        Assert.Contains(nameof(TwoKinds), two.Message, StringComparison.Ordinal);

        var zero = Assert.Throws<InvalidOperationException>(() => gate.Acquire([Shared<ZeroCapacity>()]));
        Assert.Contains("at least 1", zero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Releasing_what_was_never_acquired_is_an_error()
    {
        var gate = new ContentionGate();
        Assert.Throws<InvalidOperationException>(() => gate.Release([Shared<SharedCatalog>()]));
    }

    [Fact]
    public void Releasing_the_same_use_twice_is_an_error()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<SharedCatalog>()]));
        gate.Release([Shared<SharedCatalog>()]);

        Assert.Throws<InvalidOperationException>(() => gate.Release([Shared<SharedCatalog>()]));
    }

    [Fact]
    public void Releasing_with_a_different_mode_than_it_was_acquired_is_an_error()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<SharedCatalog>()]));

        Assert.Throws<InvalidOperationException>(() => gate.Release([Exclusive<SharedCatalog>()]));
    }

    [Fact]
    public void A_failed_release_leaves_every_use_in_the_set_still_held()
    {
        var gate = new ContentionGate();
        Assert.Null(gate.Acquire([Shared<SharedCatalog>(), Exclusive<ExclusiveDb>()]));

        // SharedCatalog is validly held; PooledSmtp was never acquired. The whole release must
        // fail without touching SharedCatalog's slot.
        Assert.Throws<InvalidOperationException>(
            () => gate.Release([Shared<SharedCatalog>(), Shared<PooledSmtp>()]));

        // Still held shared: an exclusive acquire is refused.
        Assert.NotNull(gate.Acquire([Exclusive<SharedCatalog>()]));
    }
}
