using Xunit;

namespace Raun.Test.Resources;

public class ResourceRelationshipTests
{
    public sealed record Patient(string Id);
    public sealed record Row(string Id);
    public sealed record Response(string Id, string? State = null);
    public sealed record Appointment(string Id);

    [ResourceActionDefinition(ResourceEffect.Update)]
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
        await resources.Link("Patient", source, target, typeof(Patient));
        Assert.Single(observations);
        var effect = Assert.Single(resources.Effects);
        Assert.Equal("Reserve", effect.Action!.Name);
        Assert.Equal(ResourceEffect.Update, effect.Effect);
        Assert.Equal(LockMode.Exclusive, effect.Effect.ToLockMode());
        Assert.Null(effect.DataSnapshot);
        Assert.Equal(typeof(Patient), effect.Identity.Type);
        Assert.Equal(effect.Identity, Assert.Single(resources.Links).Target);
        Assert.Equal("Patient", Assert.Single(resources.Links).Role);
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Link("", source, target));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Link("Patient", source, target, typeof(Appointment)));
        Assert.Single(resources.Effects);
        Assert.Single(resources.Links);
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
        await resources.Apply(action, new Response("p", "after"));
        await resources.Link("Patient", source, new Response("p", "after"));
        await resources.Link("Patient", source, new Response("p", "after"));
        Assert.Equal(2, observations.Count);
        Assert.Contains("after", observations[1], StringComparison.Ordinal);
        Assert.Equal(observations[1], Assert.Single(resources.Effects).DataSnapshot);
        Assert.Single(resources.Links);
    }

    [Fact]
    public async Task Canonical_expectations_accept_all_mapped_representations_and_data_free_keys()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Patient>(p => p.Id).With<Row>(r => r.Id).With<Response>(r => r.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        await resources.Link("Patient", source, new Row("p"), typeof(Patient));
        await resources.Link("Patient", source, new Response("q"), typeof(Patient));
        await resources.Link("Patient", source, new ResourceIdentity(typeof(Row), "r"), typeof(Patient));
        Assert.Equal(3, resources.Links.Count);
        Assert.All(resources.Links, relation => Assert.Equal(typeof(Patient), relation.Target.Type));
        Assert.Empty(resources.Effects);
    }

    [Fact]
    public async Task Mismatched_kinds_self_aliases_and_conflicting_kinds_record_no_new_evidence()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>().With<Row>(r => r.Id);
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Link("Patient", source, new ResourceIdentity("other", "p"), typeof(Patient)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Link("Patient", new ResourceIdentity(typeof(Patient), "p"), new ResourceIdentity(typeof(Row), "p")));
        Assert.Empty(resources.Effects); Assert.Empty(resources.Links);
        await resources.Link("Patient", source, new Row("p"), typeof(Patient));
        await resources.Link("OtherRole", source, new Row("p"), typeof(Patient));
        Assert.Empty(resources.Effects); Assert.Equal(2, resources.Links.Count);
    }
    [Fact]
    public async Task Imperative_relations_share_the_same_guard_and_rejected_claims_add_no_edges()
    {
        var resolver = new ResourceIdentityResolver();
        var resources = new ResourceContext("step", "book", resolver, TimeProvider.System);
        var source = new ResourceIdentity(typeof(Appointment), "a");
        var target = new ResourceIdentity(typeof(Patient), "p");
        await resources.Link("Patient", source, target);
        await resources.Link("Patient", source, target);
        await Assert.ThrowsAsync<ArgumentException>(async () => await resources.Link("Patient", source, source));
        Assert.Single(resources.Links); Assert.Empty(resources.Effects);
    }
}
