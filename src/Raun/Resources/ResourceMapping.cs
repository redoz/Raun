namespace Raun;

/// <summary>An immutable declaration connecting one representation to its resource owner.</summary>
public sealed class ResourceMapping
{
    private ResourceMapping(Type ownerType, Type sourceType, Func<object, ResourceKey> key, Func<object, string?> label)
    {
        OwnerType = ownerType;
        SourceType = sourceType;
        Key = key;
        Label = label;
    }

    public Type OwnerType { get; }
    public Type SourceType { get; }
    internal Func<object, ResourceKey> Key { get; }
    internal Func<object, string?> Label { get; }

    public static ResourceMapping Alternate<TOwner, TAlternate>() where TOwner : IResourceAlternate<TAlternate>
        => new(typeof(TOwner), typeof(TAlternate), ExtractKey<TOwner, TAlternate>, ExtractLabel<TOwner, TAlternate>);

    private static ResourceKey ExtractKey<TOwner, TAlternate>(object value) where TOwner : IResourceAlternate<TAlternate>
        => TOwner.KeyFor((TAlternate)value);
    private static string? ExtractLabel<TOwner, TAlternate>(object value) where TOwner : IResourceAlternate<TAlternate>
        => TOwner.LabelFor((TAlternate)value);
}
