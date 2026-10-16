using System.ComponentModel;
using Xunit;

namespace Raun.Test.Resources;

public class ResourceDefinitionTests
{
    [ResourceActionDefinition(ResourceActionKind.Create)]
    private sealed class BookedAttribute : ResourceAttribute
    {
        [Relationship<Patient>(ResourceRelation.Reference)]
        public string Patient { get; set; } = "ignored initializer";
        [Relationship<Slot>(ResourceRelation.Consume)]
        public string Slot { get; set; } = "";
    }
    public sealed class Patient;
    public sealed class Slot;

    [Fact]
    public void Direct_actions_expose_one_immutable_typed_relationship_schema()
    {
        var action = ResourceAction.For<BookedAttribute>();
        Assert.Equal(LifecycleVerb.Create, action.Effect);
        Assert.Equal("Booked", action.Name);
        Assert.Equal(2, action.Relationships.Count);
        var patient = action.GetRelationship("Patient");
        Assert.Equal(ResourceRelation.Reference, patient.Relation);
        Assert.Equal(typeof(Patient), patient.ResourceType);
        Assert.True(patient.Required);
        Assert.False(patient.IsCollection);
        Assert.Equal(ResourceRelation.Consume, action.GetRelationship("Slot").Relation);
        Assert.Same(action.Relationships, new BookedAttribute().Relationships);
    }

    [Fact]
    public void Built_in_arrays_use_the_same_schema_and_remain_optional()
    {
        var action = ResourceAction.For<CreatedAttribute>();
        var references = action.GetRelationship("References");
        Assert.Equal(ResourceRelation.Reference, references.Relation);
        Assert.Null(references.ResourceType);
        Assert.True(references.IsCollection);
        Assert.False(references.Required);
        Assert.Equal(ResourceRelation.Consume, action.GetRelationship("Consumes").Relation);
    }

    [Theory]
    [InlineData(typeof(MissingAttribute))]
    [InlineData(typeof(RepeatedAttribute))]
    [InlineData(typeof(InvalidKindAttribute))]
    [InlineData(typeof(InvalidPropertyAttribute))]
    [InlineData(typeof(HidingAttribute))]
    public void Malformed_definitions_are_rejected_without_running_user_code(Type type)
        => Assert.Throws<ArgumentException>(() => ResourceAction.For(type));

    private sealed class MissingAttribute : ResourceAttribute;
    [ResourceActionDefinition(ResourceActionKind.Create)]
    private sealed class RepeatedAttribute : CreatedAttribute;
    [ResourceActionDefinition((ResourceActionKind)99)]
    private sealed class InvalidKindAttribute : ResourceAttribute;
    private sealed class InvalidPropertyAttribute : CreatedAttribute
    {
        [Relationship(ResourceRelation.Reference)]
        public int Wrong { get; set; }
    }
    private sealed class HidingAttribute : CreatedAttribute
    {
        public new string[] References { get; set; } = [];
    }
}
