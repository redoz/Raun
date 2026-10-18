using Xunit;

namespace Raun.Test.Resources;

/// <summary>A resource type that carries its own type-level key projection via the CRTP
/// <see cref="IResource{TSelf}"/> contract (static-abstract <c>KeyFor</c>).</summary>
public sealed record User(string Email, bool Suspended = false) : IResource<User>
{
    public static ResourceKey KeyFor(User u) => u.Email;
}

/// <summary>Covers the resource value types: the verb to lock-mode map, value-equal identity and key,
/// and that the static-abstract <c>KeyFor</c> is reachable through the interface contract.</summary>
public class ResourceModelTests
{
    [Theory]
    [InlineData(ResourceEffect.Read, LockMode.Shared)]
    [InlineData(ResourceEffect.Create, LockMode.Exclusive)]
    [InlineData(ResourceEffect.Update, LockMode.Exclusive)]
    [InlineData(ResourceEffect.Delete, LockMode.Exclusive)]
    public void Verb_maps_to_lock_mode(ResourceEffect verb, LockMode expected)
        => Assert.Equal(expected, verb.ToLockMode());

    [Fact]
    public void ResourceKey_is_value_equal_and_string_convertible()
    {
        ResourceKey a = "jane@x";          // implicit from string
        Assert.Equal(new ResourceKey("jane@x"), a);
        Assert.Equal("jane@x", a.ToString());
    }

    [Fact]
    public void Identity_pairs_type_and_key_with_value_equality()
    {
        var a = new ResourceIdentity(typeof(User), "jane@x");
        var b = new ResourceIdentity(typeof(User), "jane@x");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Static_abstract_KeyFor_is_callable_through_the_interface()
        => Assert.Equal(new ResourceKey("jane@x"), User.KeyFor(new User("jane@x")));

    [Fact]
    public void ResourceIdentity_ToString_renders_as_TypeName_colon_Key()
        => Assert.Equal("User:jane@x", new ResourceIdentity(typeof(User), "jane@x").ToString());
}
