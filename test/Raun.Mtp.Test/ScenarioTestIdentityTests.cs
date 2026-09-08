using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The <see cref="Microsoft.Testing.Platform.Extensions.Messages.TestMethodIdentifierProperty"/> the
/// discoverer and reporter attach for runner grouping, built from the definition's identity
/// (see <c>Raun.Model.ScenarioIdentity</c> for where namespace and type come from).
/// </summary>
public class ScenarioTestIdentityTests
{
    private static ScenarioDefinition Definition(string methodName, string ns, string typeName, string? classDisplayName = null) => new()
    {
        ScenarioId = "scn",
        DisplayName = "customer books",
        MethodName = methodName,
        Namespace = ns,
        TypeName = typeName,
        ClassDisplayName = classDisplayName,
        Nodes = [],
    };

    [Fact]
    public void Create_from_a_definition_prefers_the_namespace_and_type_the_generator_recorded()
    {
        // Dotted MethodName would split into ("MyApp.Outer", "Inner"); the model knows better.
        var id = ScenarioTestIdentity.Create(Definition("MyApp.Outer.Inner.Book", "MyApp", "Outer+Inner"));

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Outer+Inner", id.TypeName);
        Assert.Equal("customer books", id.MethodName);
        Assert.Equal("System.Void", id.ReturnTypeFullName);
        Assert.Empty(id.ParameterTypeFullNames);
    }

    [Fact]
    public void Create_from_a_definition_without_recorded_identity_splits_the_method_name()
    {
        // An older generator's output: Namespace/TypeName left at their defaults.
        var id = ScenarioTestIdentity.Create(Definition("MyApp.Bookings.Book", "", ""));

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Bookings", id.TypeName);
    }

    [Fact]
    public void Create_from_a_definition_still_lets_the_class_display_name_override_the_type()
    {
        var id = ScenarioTestIdentity.Create(Definition("MyApp.Bookings.Book", "MyApp", "Bookings", "Appointment booking"));

        Assert.Equal("Appointment booking", id.TypeName);
    }
}
