using Xunit;

namespace Raun.Test.Resources;

public sealed class ResourceRepresentationMappingTests
{
    [Fact]
    public void Defaults_unify_objects_and_symbolic_keys_with_explicit_dispatch()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings([ResourceMapping.Alternate<Patient, Row>()]);
        Assert.Equal(resolver.Resolve(new Patient("42")), resolver.Resolve(new Row("42", "row")));
        Assert.Equal(new ResourceIdentity(typeof(Patient), "42"), resolver.Normalize(new ResourceIdentity(typeof(Row), "42")));
        Assert.True(resolver.TryResolve(typeof(Row), new Row("42", "row"), out var actual));
        Assert.Equal(new ResourceIdentity(typeof(Patient), "42"), actual);
        Assert.Equal("row", resolver.ResolveLabel(actual, typeof(Row), new Row("42", "row")));
    }

    [Fact]
    public void Fluent_selectors_override_same_group_defaults_but_not_legacy_contracts()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Patient>(_ => "legacy");
        resolver.Identify<Patient>().With<Row>(row => "explicit:" + row.Id);
        resolver.ApplyMappings([ResourceMapping.Alternate<Patient, Row>()]);
        resolver.Label<Row>(_ => "override");
        Assert.Equal((ResourceKey)"42", resolver.Resolve(new Patient("42")).Key);
        var row = new Row("42", "default");
        var identity = resolver.Resolve(row);
        Assert.Equal((ResourceKey)"explicit:42", identity.Key);
        Assert.Equal("override", resolver.ResolveLabel(identity, typeof(Row), row));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_and_named_groups_compose_in_either_order(bool namedFirst)
    {
        var resolver = new ResourceIdentityResolver();
        if (namedFirst) { resolver.Identify("patients").With<Patient>(); }
        resolver.Identify<Patient>().With<Row>(row => row.Id);
        if (!namedFirst) { resolver.Identify("patients").With<Patient>(); }
        resolver.Identify<Row>().With<Patient>(); // reciprocal declaration must not cycle
        Assert.Equal(new ResourceIdentity("patients", "42"), resolver.Resolve(new Row("42", "label")));
        Assert.Equal(resolver.Resolve(new Row("42", "label")), resolver.Resolve(new Patient("42")));
        Assert.NotEqual(resolver.Resolve(new Patient("42")), resolver.Resolve(new Other("42")));
    }

    [Fact]
    public void Named_conflicts_and_default_batches_are_atomic()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify("first").With<Row>(row => row.Id);
        Assert.Throws<InvalidOperationException>(() => resolver.Identify("second").With<Row>(row => row.Id));
        Assert.Equal(new ResourceIdentity("first", "42"), resolver.Resolve(new Row("42", "label")));
        var other = new ResourceIdentityResolver();
        Assert.Throws<InvalidOperationException>(() => other.ApplyMappings([
            ResourceMapping.Alternate<Patient, Row>(), ResourceMapping.Alternate<OtherOwner, Row>()]));
        Assert.False(other.TryResolve(typeof(Row), new Row("42", "label"), out _));
    }

    [Fact]
    public void Successful_resolution_freezes_new_groups_but_repeated_defaults_are_safe()
    {
        var resolver = new ResourceIdentityResolver();
        Assert.False(resolver.TryResolve(typeof(Row), new Row("42", "label"), out _));
        var mappings = new[] { ResourceMapping.Alternate<Patient, Row>() };
        resolver.ApplyMappings(mappings);
        Assert.Equal(new ResourceIdentity(typeof(Patient), "42"), resolver.Resolve(new Row("42", "label")));
        Parallel.For(0, 20, _ => resolver.ApplyMappings(mappings));
        Assert.Throws<InvalidOperationException>(() => resolver.Identify("late").With<Other>(x => x.Id));
        Assert.Throws<InvalidOperationException>(() => resolver.ApplyMappings([ResourceMapping.Alternate<OtherOwner, Other>()]));
    }

    [Fact]
    public void Omitted_alternate_label_falls_back_to_source_contract_then_canonical_display()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.ApplyMappings([ResourceMapping.Alternate<OtherOwner, Other>()]);
        var value = new Other("42");
        Assert.Equal("source", resolver.ResolveLabel(resolver.Resolve(value), typeof(Other), value));
    }

    public sealed record Patient(string Id) : IResource<Patient>, IResourceAlternate<Row>
    {
        public static ResourceKey KeyFor(Patient value) => value.Id;
        static ResourceKey IResourceAlternate<Row>.KeyFor(Row value) => value.Id;
        static string? IResourceAlternate<Row>.LabelFor(Row value) => value.Name;
    }
    public record Row(string Id, string Name);
    public sealed record Other(string Id) : IResource<Other>
    {
        public static ResourceKey KeyFor(Other value) => value.Id;
        public static string? LabelFor(Other value) => "source";
    }
    public sealed class OtherOwner : IResourceAlternate<Row>, IResourceAlternate<Other>
    {
        public static ResourceKey KeyFor(Row value) => value.Id;
        public static ResourceKey KeyFor(Other value) => value.Id;
    }
}
