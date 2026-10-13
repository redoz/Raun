using Xunit;

namespace Raun.Test.Resources;

/// <summary>A type with no KeyFor/selector/IResourceIdentity — exercises the value-equality fallback.</summary>
public sealed record Plain(string Name);

/// <summary>A type whose key is computed at runtime (chain link 3).</summary>
public sealed class Session : IResourceIdentity
{
    public Session(string token) => Token = token;

    public string Token { get; }

    public ResourceKey GetResourceKey() => $"session:{Token}";
}

/// <summary>Covers the resolver's layered key chain and the with-edit identity invariant.</summary>
public class ResourceIdentityResolverTests
{
    [Fact]
    public void TryResolve_excludes_unregistered_fallback_values()
    {
        var resolver = new ResourceIdentityResolver();
        Assert.False(resolver.TryResolve(typeof(int), 42, out _));
        Assert.False(resolver.TryResolve(typeof(Plain), new Plain("widget"), out _));
        Assert.False(resolver.TryResolve(typeof(object), new Session("abc"), out _));
    }

    [Fact]
    public void TryResolve_uses_declared_contract_and_selector_identity()
    {
        var resolver = new ResourceIdentityResolver();
        IResourceIdentity value = new Session("abc");
        Assert.True(resolver.TryResolve(typeof(IResourceIdentity), value, out var identity));
        Assert.Equal(resolver.Resolve(value), identity);
        resolver.Identify<IResourceIdentity>(_ => "selected");
        Assert.True(resolver.TryResolve(typeof(IResourceIdentity), value, out identity));
        Assert.Equal(new ResourceIdentity(typeof(IResourceIdentity), "selected"), identity);
        Assert.True(resolver.TryResolve(typeof(User), new User("admin@acme.com"), out identity));
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), identity);
    }

    [Fact]
    public void TryResolve_accepts_registered_plain_records_and_identity_values()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Plain>(p => p.Name);
        Assert.True(resolver.TryResolve(typeof(Plain), new Plain("widget"), out var identity));
        Assert.Equal(new ResourceIdentity(typeof(Plain), "widget"), identity);
        Assert.True(resolver.TryResolve(typeof(ResourceIdentity), identity, out var resolved));
        Assert.Equal(identity, resolved);
    }

    [Fact]
    public void TryResolve_preserves_a_nullable_declared_selector_type()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<int?>(_ => "nullable-key");
        Assert.True(resolver.TryResolve(typeof(int?), 42, out var identity));
        Assert.Equal(new ResourceIdentity(typeof(int?), "nullable-key"), identity);
        Assert.Equal(resolver.Resolve(typeof(int?), 42), identity);
    }

    [Fact]
    public void Link1_uses_IResource_KeyFor()
    {
        var resolver = new ResourceIdentityResolver();
        var id = resolver.Resolve(new User("admin@acme.com"));
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), id);
    }

    [Fact]
    public void With_edited_record_keeps_its_key_because_KeyFor_projects_a_stable_member()
    {
        var resolver = new ResourceIdentityResolver();
        var user = new User("admin@acme.com");
        var suspended = user with { Suspended = true };

        Assert.Equal(resolver.Resolve(user), resolver.Resolve(suspended));
    }

    [Fact]
    public void Link2_uses_a_registered_selector()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Plain>(p => p.Name);

        var id = resolver.Resolve(new Plain("widget"));
        Assert.Equal(new ResourceIdentity(typeof(Plain), "widget"), id);
    }

    [Fact]
    public void Link3_uses_IResourceIdentity()
    {
        var resolver = new ResourceIdentityResolver();
        var id = resolver.Resolve(new Session("abc"));
        Assert.Equal(new ResourceIdentity(typeof(Session), "session:abc"), id);
    }

    [Fact]
    public void Link4_falls_back_to_value_equality()
    {
        var resolver = new ResourceIdentityResolver();
        var a = resolver.Resolve(new Plain("same"));
        var b = resolver.Resolve(new Plain("same"));

        Assert.Equal(a, b);
        Assert.Equal(new Plain("same").ToString(), a.Key.Value);
    }

    [Fact]
    public void KeyFor_wins_over_a_registered_selector()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<User>(_ => "ignored");

        var id = resolver.Resolve(new User("admin@acme.com"));
        Assert.Equal(new ResourceKey("admin@acme.com"), id.Key);
    }
}
