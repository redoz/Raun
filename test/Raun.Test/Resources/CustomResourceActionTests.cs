using System.ComponentModel;
using Raun.Model;
using Xunit;
using Patient = Raun.Test.Resources.ResourceRepresentationMappingTests.Patient;
using Row = Raun.Test.Resources.ResourceRepresentationMappingTests.Row;

namespace Raun.Test.Resources;

public sealed class CustomResourceActionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Custom_edit_uses_existing_exclusive_conflict_rules(bool customFirst)
    {
        var resolver = new ResourceIdentityResolver();
        var first = Context(resolver);
        var second = Context(resolver);
        var ledger = new ResourceLedger([Node(0), Node(1)]);
        first.AttachLedger(ledger, 0);
        second.AttachLedger(ledger, 1);
        var patient = new Patient("42");
        if (customFirst)
        {
            await first.Apply(ResourceAction.For<ResourceActionTests.ApprovedAttribute>(), patient);
            var error = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Read(patient).AsTask());
            Assert.Equal(LifecycleVerb.Edit, error.OtherVerb);
        }
        else
        {
            await first.Read(patient);
            var error = await Assert.ThrowsAsync<ResourceConflictException>(() => second.Apply(ResourceAction.For<ResourceActionTests.ApprovedAttribute>(), patient).AsTask());
            Assert.Equal(LifecycleVerb.Edit, error.Verb);
        }
        Assert.Empty(second.Effects);
    }

    [Fact]
    public async Task Custom_reads_are_shared_and_distinct_action_types_keep_their_own_effects()
    {
        var resolver = new ResourceIdentityResolver();
        var first = Context(resolver);
        var second = Context(resolver);
        var ledger = new ResourceLedger([Node(0), Node(1)]);
        first.AttachLedger(ledger, 0);
        second.AttachLedger(ledger, 1);
        await first.Apply(ResourceAction.For<ResourceActionTests.ValidatedAttribute>(), new Patient("42"));
        await second.Read(new Patient("42"));
        Assert.Equal(LockMode.Shared, Assert.Single(first.Effects).Mode);
    }

    [Fact]
    public async Task Aggregation_preserves_builtin_rollup_and_distinct_domain_actions()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings([ResourceMapping.Alternate<Patient, Row>()]);
        var context = Context(resolver);
        await context.Read(new Patient("42"));
        await context.Edit(new Patient("42"));
        Assert.Equal(LifecycleVerb.Edit, Assert.Single(context.Effects).Verb);
        var action = ResourceAction.For<ResourceActionTests.ApprovedAttribute>();
        await context.Apply(action, new Row("42", "before"));
        var original = context.Effects[1];
        await context.Apply(action, new Row("42", "after"));
        await context.Apply(ResourceAction.For<ConfirmedAttribute>(), new Row("42", "confirmed"));
        await context.Apply(ResourceAction.For<ResourceActionTests.CheckedAttribute>(), new Row("42", "checked"));
        Assert.Equal(4, context.Effects.Count);
        Assert.Equal(original.Timestamp, context.Effects[1].Timestamp);
        Assert.Contains("after", context.Effects[1].DataSnapshot, StringComparison.Ordinal);
        Assert.Equal("Approve", context.Effects[1].Action!.Name);
        Assert.Equal("Approve", context.Effects[2].Action!.Name);
        Assert.NotEqual(context.Effects[1].Action!.AttributeType, context.Effects[2].Action!.AttributeType);
        Assert.All(context.Effects, effect => Assert.Equal(new ResourceIdentity(typeof(Patient), "42"), effect.Identity));
    }

    [Fact]
    public async Task Key_only_effects_have_no_wrapper_payload_or_snapshot()
    {
        var context = Context(new ResourceIdentityResolver());
        var identity = new ResourceIdentity(typeof(Patient), "42");
        await context.Create(identity);
        await context.Load(identity);
        await context.Apply(ResourceAction.For<ResourceActionTests.ApprovedAttribute>(), identity);
        Assert.All(context.Effects, effect => { Assert.Null(effect.Data); Assert.Null(effect.DataSnapshot); });
        Assert.Equal(2, context.Effects.Count);
    }

    [Fact]
    public async Task Apply_uses_declared_source_type_and_logs_each_frozen_observation()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings([ResourceMapping.Alternate<Patient, Row>()]);
        resolver.Label<Row>(row => "base:" + row.Name);
        resolver.Label<ResourceRepresentationIntegrationTests.DerivedRow>(_ => throw new InvalidOperationException("wrong type"));
        var context = new ScenarioContext("s", "s", null, resolver, TimeProvider.System, CancellationToken.None);
        Row row = new ResourceRepresentationIntegrationTests.DerivedRow("42", "before", "extra");
        await context.Resources.Apply(ResourceAction.For<ResourceActionTests.ApprovedAttribute>(), row);
        await context.Resources.Apply(ResourceAction.For<ResourceActionTests.ApprovedAttribute>(), new Row("42", "after"));
        Assert.Equal(2, context.LogEntries.Count);
        Assert.Contains("[resource] Approve base:before", context.Logs[0], StringComparison.Ordinal);
        Assert.Contains("[resource] Approve base:after", context.Logs[1], StringComparison.Ordinal);
        Assert.Contains("before", context.LogEntries[0].Content!.Parts.Single(part => part.Resource.HasValue).Snapshot, StringComparison.Ordinal);
        Assert.Equal("base:after", Assert.Single(context.Resources.Effects).Label);
    }

    private static ResourceContext Context(ResourceIdentityResolver resolver) => new("s", "s", resolver, TimeProvider.System);
    private static ScenarioNode Node(int index) => new() { Index = index, StepId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Phase = "Given", OperationName = "op", DisplayNameTemplate = "op", DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(null) };
    [DisplayName("Approve")]
    public sealed class ConfirmedAttribute : EditedAttribute;
}
