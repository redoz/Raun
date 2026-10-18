using System;
using System.Linq;
using System.Threading.Tasks;
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// Verifies the C1 <see cref="ResourceContext"/> tracer: each lifecycle verb records a
/// <see cref="ResourceObservation"/> with the resolved identity, owning step, and an injected timestamp.
/// </summary>
public class ResourceContextTests
{
    static ResourceContext NewContext(out FixedTimeProvider clock)
    {
        clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero));
        return new ResourceContext("step-7", "given a user", new ResourceIdentityResolver(), clock);
    }

    [Fact]
    public async Task Create_records_an_exclusive_effect_with_resolved_identity()
    {
        var ctx = NewContext(out var clock);

        await ctx.Create(new User("admin@acme.com"));

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(ResourceEffect.Create, effect.Effect);
        Assert.Equal(LockMode.Exclusive, effect.Mode);
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), effect.Identity);
        Assert.Equal("step-7", effect.StepId);
        Assert.Equal("given a user", effect.StepDisplayName);
        Assert.Equal(clock.GetUtcNow(), effect.Timestamp);
    }

    [Fact]
    public async Task Load_records_a_shared_effect_by_key_with_no_data()
    {
        var ctx = NewContext(out _);

        await ctx.Read<User>("admin@acme.com");

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(ResourceEffect.Read, effect.Effect);
        Assert.Equal(LockMode.Shared, effect.Mode);
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), effect.Identity);
        Assert.Null(effect.Data);
    }

    [Fact]
    public async Task Same_identity_dedups_to_the_strongest_verb()
    {
        var ctx = NewContext(out _);
        var user = new User("admin@acme.com");

        await ctx.Read(user);
        await ctx.Update(user with { Suspended = true });
        await ctx.Delete(user);

        // All three touches have the same identity — dedup collapses to a single effect.
        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(ResourceEffect.Delete, effect.Effect);
        Assert.Equal(LockMode.Exclusive, effect.Mode);
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), effect.Identity);
        Assert.NotNull(effect.Data);
    }

    [Fact]
    public async Task Dedup_keeps_latest_nonnull_data()
    {
        var ctx = NewContext(out _);
        var user = new User("admin@acme.com");
        var suspended = user with { Suspended = true };

        await ctx.Read(user);
        await ctx.Update(suspended);

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(ResourceEffect.Update, effect.Effect);
        // The Update data (suspended copy) replaces the Read data — proven by reference identity.
        Assert.Same(suspended, effect.Data);
    }

    [Fact]
    public async Task Effects_are_recorded_in_call_order()
    {
        var ctx = NewContext(out _);

        await ctx.Create(new User("a@x"));
        await ctx.Read<User>("b@x");

        Assert.Equal(
            new[] { ResourceEffect.Create, ResourceEffect.Read },
            ctx.Effects.Select(e => e.Effect));
    }

    [Fact]
    public async Task Load_instance_overload_records_a_shared_effect_with_resolved_identity_and_data()
    {
        var ctx = NewContext(out _);
        var user = new User("jane@acme.com");

        await ctx.Read(user);

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(ResourceEffect.Read, effect.Effect);
        Assert.Equal(LockMode.Shared, effect.Mode);
        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), effect.Identity);
        Assert.NotNull(effect.Data);
    }

    [Fact]
    public async Task Links_do_not_record_effects_or_access_claims()
    {
        var ctx = NewContext(out _);
        var source = new User("booking@acme.com");
        var target = new User("jane@acme.com");
        await ctx.Link("Patient", source, target);
        Assert.Empty(ctx.Effects);
        var link = Assert.Single(ctx.Links);
        Assert.Equal(new ResourceIdentity(typeof(User), "booking@acme.com"), link.Source);
        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), link.Target);
        Assert.Equal("Patient", link.Role);
    }

    [Fact]
    public async Task Collection_sources_record_multiple_links_and_dedup_exact_repeats()
    {
        var ctx = NewContext(out _);
        User[] sources = [new("a@x"), new("b@x")];
        var target = new User("slot@x");
        await ctx.Link("Slot", sources, target);
        await ctx.Link("Slot", sources, target);
        Assert.Equal(2, ctx.Links.Count);
        Assert.All(ctx.Links, link => Assert.Equal("Slot", link.Role));
        Assert.Equal(["a@x", "b@x"], ctx.Links.Select(link => link.Source.Key.Value));
        Assert.Empty(ctx.Effects);
    }

    [Fact]
    public async Task Same_endpoints_can_have_different_roles()
    {
        var ctx = NewContext(out _);
        var source = new User("booking@x"); var target = new User("jane@x");
        await ctx.Link("Patient", source, target);
        await ctx.Link("Payer", source, target);
        Assert.Equal(["Patient", "Payer"], ctx.Links.Select(link => link.Role));
    }

    [Fact]
    public async Task Update_outranks_read_in_dedup()
    {
        var ctx = NewContext(out _); var user = new User("jane@x");
        await ctx.Read(user); await ctx.Update(user);
        Assert.Equal(ResourceEffect.Update, Assert.Single(ctx.Effects).Effect);
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
