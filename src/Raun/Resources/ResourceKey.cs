namespace Raun;

/// <summary>
/// A resource's symbolic key within its type. Compared by value, so two keys with the same
/// <see cref="Value"/> are equal. Implicitly convertible from a string for ergonomic call sites
/// (<c>ctx.Resources.Read&lt;User&gt;("admin@x")</c>).
/// </summary>
public readonly record struct ResourceKey(string Value)
{
    /// <summary>Wraps a raw string as a <see cref="ResourceKey"/>.</summary>
    public static implicit operator ResourceKey(string value) => new(value);

    /// <summary>Creates a key from a string (the named alternative to the implicit conversion).</summary>
    public static ResourceKey FromString(string value) => new(value);

    /// <inheritdoc/>
    public override string ToString() => Value;
}
