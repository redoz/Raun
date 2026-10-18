using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

public class ResourceIdentityRoleTests
{
    static ResourceContext NewContext(string stepId = "step-0")
        => new(stepId, stepId, new ResourceIdentityResolver(), TimeProvider.System);

    [Theory]
    [InlineData(ResourceEffect.Read, LockMode.Shared)]
    [InlineData(ResourceEffect.Update, LockMode.Exclusive)]
    [InlineData(ResourceEffect.Delete, LockMode.Exclusive)]
    [InlineData(ResourceEffect.Create, LockMode.Exclusive)]
    public async Task Identity_claims_record_the_resource_type_and_key_without_data(ResourceEffect verb, LockMode mode)
    {
        var ctx = NewContext();
        var identity = new ResourceIdentity(typeof(User), "jane@x");
        ctx.Label<User>(_ => throw new InvalidOperationException("Key-only claims cannot select DTO labels."));
        ctx.Label<ResourceIdentity>(_ => throw new InvalidOperationException("Identity wrappers cannot select labels."));

        await Claim(ctx, verb, identity);

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(identity, effect.Identity);
        Assert.Equal(verb, effect.Effect);
        Assert.Equal(mode, effect.Mode);
        Assert.Null(effect.Data);
        Assert.Null(effect.DataSnapshot);
        Assert.True(effect.DataSnapshotCaptured);
        Assert.Equal("User:jane@x", effect.Label);
        Assert.Empty(ctx.Links);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_and_instance_claims_dedup_and_preserve_instance_data(bool identityFirst)
    {
        var ctx = NewContext();
        var user = new User("jane@x");
        var identity = new ResourceIdentity(typeof(User), "jane@x");
        ctx.Label<User>(_ => "Jane");

        if (identityFirst)
        {
            await ctx.Delete(identity);
            await ctx.Read(user);
        }
        else
        {
            await ctx.Read(user);
            await ctx.Delete(identity);
        }

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(identity, effect.Identity);
        Assert.Equal(ResourceEffect.Delete, effect.Effect);
        Assert.Same(user, effect.Data);
        Assert.NotNull(effect.DataSnapshot);
        Assert.Equal("Jane", effect.Label);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_and_instance_claims_conflict_for_unordered_steps(bool identityFirst)
    {
        var first = NewContext("step-0");
        var second = NewContext("step-1");
        var ledger = new ResourceLedger([Node(0), Node(1)]);
        first.AttachLedger(ledger, 0);
        second.AttachLedger(ledger, 1);
        var identity = new ResourceIdentity(typeof(User), "jane@x");
        var user = new User("jane@x");

        if (identityFirst)
        {
            await first.Update(identity);
            var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Read(user).AsTask());
            Assert.Equal(identity, conflict.Identity);
        }
        else
        {
            await first.Read(user);
            var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Update(identity).AsTask());
            Assert.Equal(identity, conflict.Identity);
        }

        Assert.Empty(second.Effects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Links_accept_identity_sources_with_identity_or_instance_targets_without_effects(bool keyOnlyTarget)
    {
        var ctx = NewContext();
        var target = new ResourceIdentity(typeof(User), "target@x");
        var identitySource = new ResourceIdentity(typeof(User), "key-source@x");
        var instanceSource = new User("object-source@x");
        if (keyOnlyTarget)
        {
            await ctx.Link("Patient", identitySource, target);
            await ctx.Link("Patient", instanceSource, target);
        }
        else
        {
            await ctx.Link("Patient", identitySource, new User("target@x"));
            await ctx.Link("Patient", instanceSource, new User("target@x"));
        }
        Assert.Equal(2, ctx.Links.Count);
        Assert.Equal(identitySource, ctx.Links[0].Source);
        Assert.Equal(new ResourceIdentity(typeof(User), "object-source@x"), ctx.Links[1].Source);
        Assert.All(ctx.Links, link => { Assert.Equal(target, link.Target); Assert.Equal("Patient", link.Role); });
        Assert.Empty(ctx.Effects);
    }

    static ValueTask Claim(ResourceContext ctx, ResourceEffect verb, ResourceIdentity identity) => verb switch
    {
        ResourceEffect.Read => ctx.Read(identity),
        ResourceEffect.Update => ctx.Update(identity),
        ResourceEffect.Delete => ctx.Delete(identity),
        ResourceEffect.Create => ctx.Create(identity),
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    static ScenarioNode Node(int index) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        DependsOn = [],
        OperationName = "Claim",
        DisplayNameTemplate = "claim",
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };
}
