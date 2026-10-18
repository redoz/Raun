using Xunit;

namespace Raun.Test.Resources;

public class ResourceDefinitionTests
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1813:Avoid unsealed attributes", Justification = "Exercises inherited role validation.")]
    private class Booked : CreatedAttribute
    {
        public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
        public sealed class SlotAttribute : ResourceLinkAttribute<Slot, UpdatedAttribute>;
    }
    public sealed class Patient;
    public sealed class Slot;

    [Fact]
    public void Actions_expose_one_immutable_typed_role_schema()
    {
        var action = ResourceAction.For<Booked>();
        Assert.Equal(ResourceEffect.Create, action.Effect);
        Assert.Equal("Booked", action.Name);
        Assert.Equal(2, action.Relationships.Count);
        var patient = action.GetRelationship("Patient");
        Assert.Equal(typeof(Booked), patient.OwnerType);
        Assert.Equal(typeof(Booked.PatientAttribute), patient.MarkerType);
        Assert.Equal(typeof(Patient), patient.ResourceType);
        Assert.Equal(typeof(ReadAttribute), patient.ActionType);
        Assert.Equal(typeof(UpdatedAttribute), action.GetRelationship("Slot").ActionType);
        Assert.Same(action.Relationships, new Booked().Relationships);
    }

    [Fact]
    public void Builtins_have_no_implicit_roles()
        => Assert.Empty(ResourceAction.For<CreatedAttribute>().Relationships);

    [Theory]
    [InlineData(typeof(MissingAttribute))]
    [InlineData(typeof(RepeatedAttribute))]
    [InlineData(typeof(InvalidKindAttribute))]
    [InlineData(typeof(PrivateRole))]
    [InlineData(typeof(Hiding))]
    [InlineData(typeof(HidingField))]
    [InlineData(typeof(HidingType))]
    public void Malformed_definitions_are_rejected_without_running_user_code(Type type)
        => Assert.Throws<ArgumentException>(() => ResourceAction.For(type));

    private sealed class MissingAttribute : ResourceAttribute;
    [ResourceActionDefinition(ResourceEffect.Create)] private sealed class RepeatedAttribute : CreatedAttribute;
    [ResourceActionDefinition((ResourceEffect)99)] private sealed class InvalidKindAttribute : ResourceAttribute;
    private sealed class PrivateRole : CreatedAttribute
    {
        private sealed class SecretAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
    }
    private sealed class Hiding : Booked
    {
        public new string PatientAttribute { get; set; } = "";
    }
    private sealed class HidingType : Booked
    {
        [AttributeUsage(AttributeTargets.Parameter)]
        public new sealed class PatientAttribute : Attribute;
    }
    private sealed class HidingField : Booked
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "Exercises a field hiding an inherited resource role.")]
        public new string PatientAttribute = "";
    }
}
