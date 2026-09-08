using Raun.Model;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Where a scenario's namespace and declaring type come from: the members the generator recorded
/// when present, otherwise the dotted split of the method's full name (output of a generator older
/// than those members). Runtime-neutral, so every adapter and the tree-filter path agree.
/// </summary>
public class ScenarioIdentityTests
{
    private static ScenarioDefinition Definition(string methodName, string ns = "", string typeName = "") => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = methodName,
        Namespace = ns,
        TypeName = typeName,
        Nodes = [],
    };

    [Theory]
    [InlineData("MyApp.Bookings.Book", "MyApp", "Bookings", "Book")]
    [InlineData("My.Deep.Ns.Bookings.Book", "My.Deep.Ns", "Bookings", "Book")]
    [InlineData("Bookings.Book", "", "Bookings", "Book")]   // top-level type, no namespace
    [InlineData("Book", "", "", "Book")]                    // bare name (no declaring type)
    public void Split_separates_namespace_type_and_method(string fqn, string ns, string type, string method)
    {
        ScenarioIdentity.Split(fqn, out var actualNamespace, out var actualType, out var actualMethod);

        Assert.Equal(ns, actualNamespace);
        Assert.Equal(type, actualType);
        Assert.Equal(method, actualMethod);
    }

    [Fact]
    public void Resolve_prefers_the_recorded_namespace_and_type()
    {
        ScenarioIdentity.Resolve(Definition("MyApp.Outer.Inner.Book", "MyApp", "Outer+Inner"), out var ns, out var type);

        Assert.Equal("MyApp", ns);
        Assert.Equal("Outer+Inner", type);
    }

    [Fact]
    public void Resolve_falls_back_to_the_split_when_nothing_was_recorded()
    {
        ScenarioIdentity.Resolve(Definition("MyApp.Bookings.Book"), out var ns, out var type);

        Assert.Equal("MyApp", ns);
        Assert.Equal("Bookings", type);
    }
}
