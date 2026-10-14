namespace Raun;

/// <summary>A symbolic resource identity: canonical kind and string key, compared by value.</summary>
public readonly record struct ResourceIdentity
{
    public ResourceIdentity(ResourceKind kind, ResourceKey key) { Kind = kind; Key = key; }
    public ResourceKind Kind { get; init; }
    public ResourceKey Key { get; init; }
    public ResourceIdentity(Type type, ResourceKey key) : this(new ResourceKind(type), key) { }
    public ResourceIdentity(string kind, ResourceKey key) : this(new ResourceKind(kind), key) { }
    public Type? Type => Kind.Type;
    public void Deconstruct(out Type? type, out ResourceKey key) { type = Type; key = Key; }
    public override string ToString() => $"{Kind}:{Key}";
}
