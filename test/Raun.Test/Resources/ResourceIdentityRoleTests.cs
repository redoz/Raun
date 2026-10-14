using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

public class ResourceIdentityRoleTests
{
    static ResourceContext NewContext(string stepId = "step-0")
        => new(stepId, stepId, new ResourceIdentityResolver(), TimeProvider.System);

    [Theory]
    [InlineData(LifecycleVerb.Read, LockMode.Shared)]
    [InlineData(LifecycleVerb.Edit, LockMode.Exclusive)]
    [InlineData(LifecycleVerb.Delete, LockMode.Exclusive)]
    [InlineData(LifecycleVerb.Reference, LockMode.Shared)]
    [InlineData(LifecycleVerb.Consume, LockMode.Shared)]
    public async Task Identity_claims_record_the_resource_type_and_key_without_data(LifecycleVerb verb, LockMode mode)
    {
        var ctx = NewContext();
        var identity = new ResourceIdentity(typeof(User), "jane@x");
        ctx.Label<User>(_ => throw new InvalidOperationException("Key-only claims cannot select DTO labels."));
        ctx.Label<ResourceIdentity>(_ => throw new InvalidOperationException("Identity wrappers cannot select labels."));

        await Claim(ctx, verb, identity);

        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(identity, effect.Identity);
        Assert.Equal(verb, effect.Verb);
        Assert.Equal(mode, effect.Mode);
        Assert.Null(effect.Data);
        Assert.Null(effect.DataSnapshot);
        Assert.True(effect.DataSnapshotCaptured);
        Assert.Equal("User:jane@x", effect.Label);
        Assert.Empty(ctx.Lineage);
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
        Assert.Equal(LifecycleVerb.Delete, effect.Verb);
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
            await first.Edit(identity);
            var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Read(user).AsTask());
            Assert.Equal(identity, conflict.Identity);
        }
        else
        {
            await first.Read(user);
            var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Edit(identity).AsTask());
            Assert.Equal(identity, conflict.Identity);
        }

        Assert.Empty(second.Effects);
    }

    [Theory]
    [InlineData(LifecycleVerb.Reference, true)]
    [InlineData(LifecycleVerb.Reference, false)]
    [InlineData(LifecycleVerb.Consume, true)]
    [InlineData(LifecycleVerb.Consume, false)]
    public async Task Lineage_accepts_identity_subjects_with_identity_or_instance_targets(LifecycleVerb verb, bool keyOnlyTarget)
    {
        var ctx = NewContext();
        var target = new ResourceIdentity(typeof(User), "target@x");
        var identitySubject = new ResourceIdentity(typeof(User), "key-subject@x");
        var instanceSubject = new User("object-subject@x");
        var instanceTarget = new User("target@x");

        if (keyOnlyTarget)
        {
            if (verb == LifecycleVerb.Reference) { await ctx.Reference(target, identitySubject, instanceSubject); }
            else { await ctx.Consume(target, identitySubject, instanceSubject); }
        }
        else
        {
            if (verb == LifecycleVerb.Reference) { await ctx.Reference(instanceTarget, identitySubject, instanceSubject); }
            else { await ctx.Consume(instanceTarget, identitySubject, instanceSubject); }
        }

        Assert.Equal(2, ctx.Lineage.Count);
        Assert.Equal(identitySubject, ctx.Lineage[0].Subject);
        Assert.Equal(new ResourceIdentity(typeof(User), "object-subject@x"), ctx.Lineage[1].Subject);
        Assert.All(ctx.Lineage, relation =>
        {
            Assert.Equal(target, relation.Target);
            Assert.Equal(verb, relation.Kind);
        });
        var effect = Assert.Single(ctx.Effects);
        Assert.Equal(target, effect.Identity);
        Assert.Equal(verb, effect.Verb);
        if (keyOnlyTarget)
        {
            Assert.Null(effect.Data);
            Assert.Null(effect.DataSnapshot);
        }
        else
        {
            Assert.Same(instanceTarget, effect.Data);
            Assert.NotNull(effect.DataSnapshot);
        }
    }

    static ValueTask Claim(ResourceContext ctx, LifecycleVerb verb, ResourceIdentity identity) => verb switch
    {
        LifecycleVerb.Read => ctx.Read(identity),
        LifecycleVerb.Edit => ctx.Edit(identity),
        LifecycleVerb.Delete => ctx.Delete(identity),
        LifecycleVerb.Reference => ctx.Reference(identity),
        LifecycleVerb.Consume => ctx.Consume(identity),
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
