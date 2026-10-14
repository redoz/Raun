using System.Text.Json;
using Raun.Model;
using Raun.Reporting.Html;
using Xunit;

namespace Raun.Test.Resources;

public sealed class ExplicitResourceKeyTests
{
    public sealed record Resource(string Id) : IResource<Resource>
    {
        static ResourceKey IResource<Resource>.KeyFor(Resource value) => value.Id;
        // A public method with the same shape is not necessarily the contract implementation.
        public static ResourceKey KeyFor(Resource value) => "wrong-public-key";
    }

    public readonly record struct ValueResource(string Id) : IResource<ValueResource>
    {
        static ResourceKey IResource<ValueResource>.KeyFor(ValueResource value) => value.Id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_contract_wins_over_public_lookalikes_and_registered_selectors(bool registered)
    {
        var resolver = new ResourceIdentityResolver();
        if (registered) resolver.Identify<Resource>(_ => "wrong-selector-key");
        var resource = new Resource("key");
        var expected = new ResourceIdentity(typeof(Resource), "key");
        Assert.Equal(expected, resolver.Resolve(resource));
        Assert.Equal(expected, resolver.Resolve(typeof(Resource), resource));
        Assert.True(resolver.TryResolve(typeof(Resource), resource, out var identity));
        Assert.Equal(expected, identity);
    }

    [Fact]
    public void Explicit_value_type_contract_does_not_fall_back_to_ToString()
    {
        var resolver = new ResourceIdentityResolver();
        var resource = new ValueResource("key");
        Assert.Equal(new ResourceIdentity(typeof(ValueResource), "key"), resolver.Resolve(resource));
        Assert.True(resolver.TryResolve(typeof(ValueResource), resource, out var identity));
        Assert.Equal(new ResourceIdentity(typeof(ValueResource), "key"), identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Object_and_key_claims_conflict_for_the_same_explicit_identity(bool keyFirst)
    {
        var resolver = new ResourceIdentityResolver();
        var first = new ResourceContext("first", "first", resolver, TimeProvider.System);
        var second = new ResourceContext("second", "second", resolver, TimeProvider.System);
        var nodes = new[] { Node(0, "first"), Node(1, "second") };
        var ledger = new ResourceLedger(nodes);
        first.AttachLedger(ledger, 0);
        second.AttachLedger(ledger, 1);
        var expected = new ResourceIdentity(typeof(Resource), "key");
        if (keyFirst) await first.Edit(expected);
        else await first.Edit(new Resource("key"));
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => keyFirst
            ? second.Read(new Resource("key")).AsTask() : second.Load<Resource>("key").AsTask());
        Assert.Equal(expected, error.Identity);
        Assert.Empty(second.Effects);
    }

    [Fact]
    public async Task Explicit_keys_unify_effects_lineage_and_report_resources()
    {
        var resources = new ResourceContext("step", "step", new ResourceIdentityResolver(), TimeProvider.System);
        var target = new Resource("target");
        await resources.Load<Resource>("target");
        await resources.Reference(target, new Resource("subject"));
        Assert.Equal(new ResourceIdentity(typeof(Resource), "target"), Assert.Single(resources.Effects).Identity);
        var relation = Assert.Single(resources.Lineage);
        Assert.Equal(new ResourceIdentity(typeof(Resource), "subject"), relation.Subject);
        Assert.Equal(new ResourceIdentity(typeof(Resource), "target"), relation.Target);
        var node = Node(0, "step");
        var definition = new ScenarioDefinition
        {
            ScenarioId = "keys", DisplayName = "keys", MethodName = "Keys.Run", Nodes = [node],
        };
        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(definition);
        builder.OnStepFinished(definition, new StepResult
        {
            Node = node, DisplayName = "step", Status = StepStatus.Passed, StartedAt = DateTimeOffset.UtcNow,
            Effects = resources.Effects, Lineage = resources.Lineage,
        });
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(builder.Build("now")));
        var reportResources = json.RootElement.GetProperty("Scenarios")[0].GetProperty("Resources");
        Assert.Equal(2, reportResources.GetArrayLength());
        Assert.Equal(new[] { "subject", "target" }, reportResources.EnumerateArray().Select(r => r.GetProperty("Key").GetString()).Order());
    }

    private static ScenarioNode Node(int index, string id) => new()
    {
        Index = index, StepId = id, Phase = "Given", OperationName = "Claim", DisplayNameTemplate = id,
        DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(null),
    };
}
