using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The <see cref="Microsoft.Testing.Platform.Extensions.Messages.TestMethodIdentifierProperty"/> the
/// discoverer and reporter attach for runner grouping, built from the namespace and type the
/// definition states.
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
    public void Create_from_a_definition_uses_the_namespace_and_type_it_states()
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
    public void Create_from_a_definition_never_reads_the_namespace_or_type_out_of_the_method_name()
    {
        var id = ScenarioTestIdentity.Create(Definition("MyApp.Bookings.Book", "", "Scenarios"));

        Assert.Equal("", id.Namespace);
        Assert.Equal("Scenarios", id.TypeName);
    }

    [Fact]
    public void Create_from_a_definition_still_lets_the_class_display_name_override_the_type()
    {
        var id = ScenarioTestIdentity.Create(Definition("MyApp.Bookings.Book", "MyApp", "Bookings", "Appointment booking"));

        Assert.Equal("Appointment booking", id.TypeName);
    }
}
