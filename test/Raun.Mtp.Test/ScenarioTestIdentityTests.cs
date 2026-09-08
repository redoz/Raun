using Raun.Model;
using Raun.Mtp;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Unit tests for the FQN → (namespace, type, method) split that backs the
/// <see cref="Microsoft.Testing.Platform.Extensions.Messages.TestMethodIdentifierProperty"/> the
/// discoverer and reporter attach for runner grouping.
/// </summary>
public class ScenarioTestIdentityTests
{
    [Theory]
    [InlineData("MyApp.Bookings.Book", "MyApp", "Bookings", "Book")]
    [InlineData("My.Deep.Ns.Bookings.Book", "My.Deep.Ns", "Bookings", "Book")]
    [InlineData("Bookings.Book", "", "Bookings", "Book")]   // top-level type, no namespace
    [InlineData("Book", "", "", "Book")]                    // bare name (no declaring type)
    public void Split_separates_namespace_type_and_method(string fqn, string ns, string type, string method)
    {
        ScenarioTestIdentity.Split(fqn, out var actualNamespace, out var actualType, out var actualMethod);

        Assert.Equal(ns, actualNamespace);
        Assert.Equal(type, actualType);
        Assert.Equal(method, actualMethod);
    }

    [Fact]
    public void Create_uses_scenario_display_name_as_method_and_derives_namespace_type_from_fqn()
    {
        var id = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "customer books an appointment");

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Bookings", id.TypeName);
        Assert.Equal("customer books an appointment", id.MethodName);
        Assert.Equal("System.Void", id.ReturnTypeFullName);
        Assert.Empty(id.ParameterTypeFullNames);
    }

    [Fact]
    public void Create_uses_the_class_display_name_as_the_type_when_provided()
    {
        var id = ScenarioTestIdentity.Create(
            "MyApp.Bookings.Book", "customer books an appointment", "Appointment booking");

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Appointment booking", id.TypeName);
        Assert.Equal("customer books an appointment", id.MethodName);
    }

    [Fact]
    public void Create_falls_back_to_the_fqn_type_when_class_display_name_is_null_or_empty()
    {
        var nullName = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "scenario", null);
        var emptyName = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "scenario", "");

        Assert.Equal("Bookings", nullName.TypeName);
        Assert.Equal("Bookings", emptyName.TypeName);
    }

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
