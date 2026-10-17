using Xunit;

namespace Raun.Test.Resources;

public class ResourceRelationshipTests
{
    public sealed record Patient(string Id);
    public sealed record Row(string Id);
    public sealed record Response(string Id, string? State = null);
    public sealed record Appointment(string Id);

    [ResourceActionDefinition(ResourceActionKind.Consume)]
    [System.ComponentModel.DisplayName("Reserve")]
    private sealed class ReservedAttribute : ResourceAttribute;

    [Fact]
    public async Task Named_consume_preserves_canonical_keys_lineage_and_rejects_invalid_relations()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Row>(row => row.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        var target = new ResourceIdentity(typeof(Row), "p");
        var action = ResourceAction.For<ReservedAttribute>();
        var observations = new List<string?>();
        resources.AttachActionObserver((_, _, _, snapshot, _, _) => observations.Add(snapshot));
        await resources.Apply(action, target);
        await resources.Relate(action, source, target, typeof(Patient));
        Assert.Single(observations);
        var effect = Assert.Single(resources.Effects);
        Assert.Equal("Reserve", effect.Action!.Name);
        Assert.Equal(LifecycleVerb.Consume, effect.Verb);
        Assert.Equal(LockMode.Shared, effect.Verb.ToLockMode());
        Assert.Null(effect.DataSnapshot);
        Assert.Equal(typeof(Patient), effect.Identity.Type);
        Assert.Equal(effect.Identity, Assert.Single(resources.Lineage).Target);
        Assert.Equal(LifecycleVerb.Consume, Assert.Single(resources.Lineage).Kind);
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Relate(ResourceAction.For<EditedAttribute>(), source, target));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Relate(action, source, target, typeof(Appointment)));
        Assert.Single(resources.Effects);
        Assert.Single(resources.Lineage);
        await resources.Apply(action, target);
        Assert.Equal(2, observations.Count);
    }

    [Fact]
    public async Task Named_relations_keep_changed_snapshots_but_do_not_repeat_identical_observations()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Response>(row => row.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var observations = new List<string?>();
        resources.AttachActionObserver((_, _, _, snapshot, _, _) => observations.Add(snapshot));
        var source = new ResourceIdentity(typeof(Appointment), "a");
        var action = ResourceAction.For<ReservedAttribute>();
        await resources.Apply(action, new Response("p", "before"));
        await resources.Relate(action, source, new Response("p", "after"));
        await resources.Relate(action, source, new Response("p", "after"));
        Assert.Equal(2, observations.Count);
        Assert.Contains("after", observations[1], StringComparison.Ordinal);
        Assert.Equal(observations[1], Assert.Single(resources.Effects).DataSnapshot);
        Assert.Single(resources.Lineage);
    }

    [Fact]
    public async Task Canonical_expectations_accept_all_mapped_representations_and_data_free_keys()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Patient>(p => p.Id).With<Row>(r => r.Id).With<Response>(r => r.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        await resources.Relate(ResourceRelation.Reference, source, new Row("p"), typeof(Patient));
        await resources.Relate(ResourceRelation.Reference, source, new Response("q"), typeof(Patient));
        await resources.Relate(ResourceRelation.Reference, source, new ResourceIdentity(typeof(Row), "r"), typeof(Patient));
        Assert.Equal(3, resources.Lineage.Count);
        Assert.All(resources.Lineage, relation => Assert.Equal(typeof(Patient), relation.Target.Type));
        var key = Assert.Single(resources.Effects, effect => effect.Identity.Key.Value == "r");
        Assert.Null(key.Data); Assert.Null(key.DataSnapshot);
    }

    [Fact]
    public async Task Mismatched_kinds_self_aliases_and_conflicting_kinds_record_no_new_evidence()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Row>(r => r.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Relate(ResourceRelation.Reference, source, new ResourceIdentity("other", "p"), typeof(Patient)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Relate(ResourceRelation.Reference, new ResourceIdentity(typeof(Patient), "p"), new ResourceIdentity(typeof(Row), "p")));
        Assert.Empty(resources.Effects); Assert.Empty(resources.Lineage);
        await resources.Relate(ResourceRelation.Reference, source, new Row("p"), typeof(Patient));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Relate(ResourceRelation.Consume, source, new Row("p"), typeof(Patient)));
        Assert.Single(resources.Effects); Assert.Single(resources.Lineage);
    }
    [Fact]
    public async Task Imperative_relations_share_the_same_guard_and_rejected_claims_add_no_edges()
    {
        var resolver = new ResourceIdentityResolver();
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        var target = new ResourceIdentity(typeof(Patient), "p");
        await resources.Reference(target, source);
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Consume(target, source));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Reference(source, source));
        Assert.Single(resources.Lineage); Assert.Single(resources.Effects);
    }
}
