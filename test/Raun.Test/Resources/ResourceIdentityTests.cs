using Xunit;

namespace Raun.Test.Resources;

public sealed class ResourceIdentityTests
{
    [Fact]
    public void Typed_and_named_kinds_are_distinct_and_preserve_legacy_views()
    {
        var typed = new ResourceIdentity(typeof(string), "42");
        var named = new ResourceIdentity("String", "42");
        Assert.NotEqual(typed, named);
        Assert.Equal(named, new ResourceIdentity("String", "42"));
        Assert.NotEqual(named, new ResourceIdentity("string", "42"));
        Assert.Null(named.Type);
        Assert.Equal("String", named.Kind.Name);
        var (type, key) = typed;
        Assert.Equal(typeof(string), type);
        Assert.Equal((ResourceKey)"42", key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_named_kinds_are_rejected(string name)
        => Assert.Throws<ArgumentException>(() => new ResourceKind(name));

    [Fact]
    public void Type_identity_does_not_collapse_equal_type_names()
        => Assert.NotEqual(new ResourceIdentity(typeof(First.Owner), "42"),
            new ResourceIdentity(typeof(Second.Owner), "42"));

    public static class First { public sealed class Owner; }
    public static class Second { public sealed class Owner; }
}
