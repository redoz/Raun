using Raun.Model;
using Raun.Reporting.Html;
using Xunit;
using Patient = Raun.Test.Resources.ResourceRepresentationMappingTests.Patient;
using Row = Raun.Test.Resources.ResourceRepresentationMappingTests.Row;

namespace Raun.Test.Resources;

public sealed class ResourceRepresentationIntegrationTests
{
    [Fact]
    public async Task Key_only_claims_lineage_and_messages_normalize_to_the_same_identity()
    {
        var resolver = Resolver();
        var context = Context(resolver);
        var symbolic = new ResourceIdentity(typeof(Row), "42");
        await context.Read(new Patient("42"));
        await context.Load<Row>((ResourceKey)"42");
        await context.Edit(symbolic);
        await context.Reference(symbolic, new Patient("42"), symbolic);
        Assert.Equal(new ResourceIdentity(typeof(Patient), "42"), Assert.Single(context.Effects).Identity);
        Assert.All(context.Lineage, relation => Assert.Equal(relation.Subject, relation.Target));
        var title = new StepMessageBuilder(new MessageFormattingContext(resolver)).AppendFormatted(symbolic).Build();
        Assert.Equal(Assert.Single(context.Effects).Identity, Assert.Single(title.Parts).Resource);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alternate_and_symbolic_claims_conflict_in_both_orders(bool symbolicFirst)
    {
        var resolver = Resolver();
        var first = Context(resolver);
        var second = Context(resolver);
        var ledger = new ResourceLedger([Node(0), Node(1)]);
        first.AttachLedger(ledger, 0);
        second.AttachLedger(ledger, 1);
        var symbolic = new ResourceIdentity(typeof(Row), "42");
        if (symbolicFirst)
        {
            await first.Edit(symbolic);
            await Assert.ThrowsAsync<ResourceConflictException>(() => second.Read(new Patient("42")).AsTask());
        }
        else
        {
            await first.Read(new Patient("42"));
            await Assert.ThrowsAsync<ResourceConflictException>(() => second.Edit(symbolic).AsTask());
        }
        Assert.Empty(second.Effects);
    }

    [Fact]
    public async Task Declared_source_labels_and_runtime_snapshots_survive_effect_aggregation()
    {
        var resolver = Resolver();
        resolver.Label<Row>(row => "base:" + row.Name);
        resolver.Label<DerivedRow>(_ => throw new InvalidOperationException("Wrong declared type"));
        Row row = new DerivedRow("42", "before", "evidence");
        var context = Context(resolver);
        var title = new StepMessageBuilder(new MessageFormattingContext(resolver)).AppendFormatted(row).Build();
        await context.Read(row);
        await context.Edit(new Row("42", "after"));
        await context.Load<Row>((ResourceKey)"42");
        var effect = Assert.Single(context.Effects);
        Assert.Equal(LifecycleVerb.Edit, effect.Verb);
        Assert.Equal("base:after", effect.Label);
        Assert.Contains("before", Assert.Single(title.Parts).Snapshot, StringComparison.Ordinal);
        Assert.Contains("evidence", Assert.Single(title.Parts).Snapshot, StringComparison.Ordinal);
        Assert.Contains("after", effect.DataSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("before", effect.DataSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Named_kind_reports_share_ids_and_distinguish_qualified_metadata()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify("patients").With<Patient>().With<Row>(row => row.Id);
        var context = Context(resolver);
        await context.Read(new Patient("42"));
        await context.Edit(new ResourceIdentity(typeof(Row), "42"));
        var node = Node(0);
        var definition = new ScenarioDefinition { ScenarioId = "s", DisplayName = "s", MethodName = "s", Nodes = [node] };
        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([definition]);
        builder.OnScenarioStarted(definition);
        builder.OnStepFinished(definition, new StepResult { Node = node, DisplayName = "s", Status = StepStatus.Passed,
            StartedAt = DateTimeOffset.UtcNow, Effects = context.Effects });
        var report = Assert.Single(builder.Build("now").Scenarios);
        var resource = Assert.Single(report.Resources);
        Assert.Equal("patients", resource.Type);
        Assert.Equal("named:patients", resource.QualifiedType);
        Assert.Equal(resource.Id, Assert.Single(Assert.Single(report.Steps).Effects).ResourceId);
    }

    private static ResourceIdentityResolver Resolver()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings([ResourceMapping.Alternate<Patient, Row>()]);
        return resolver;
    }
    private static ResourceContext Context(ResourceIdentityResolver resolver) => new("s", "s", resolver, TimeProvider.System);
    private static ScenarioNode Node(int index) => new() { Index = index, StepId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Phase = "Given", OperationName = "op", DisplayNameTemplate = "op", DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(null) };
    public sealed record DerivedRow(string Id, string Name, string Extra) : Row(Id, Name);
}
