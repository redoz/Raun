namespace Raun;

/// <summary>A CLR resource owner or an ordinal, named resource kind.</summary>
public readonly record struct ResourceKind
{
    public ResourceKind(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    public ResourceKind(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public Type? Type { get; }
    public string? Name { get; }
    internal bool IsValid => Type is not null || Name is not null;
    internal string DisplayName => Type is { } type ? ResourcePresentation.Name(type) : Name ?? string.Empty;
    internal string QualifiedName => Type is { } type ? type.FullName ?? type.Name : "named:" + Name;
    public override string ToString() => DisplayName;
}
