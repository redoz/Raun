namespace Raun;

/// <summary>A canonical resource group configured before execution.</summary>
public sealed class ResourceIdentification
{
    private readonly ResourceIdentityResolver _resolver;
    private readonly ResourceKind _kind;
    internal ResourceIdentification(ResourceIdentityResolver resolver, ResourceKind kind) { _resolver = resolver; _kind = kind; }

    public ResourceIdentification With<T>(Func<T, ResourceKey> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _resolver.AddRepresentation(_kind, typeof(T), value => selector((T)value));
        return this;
    }

    public ResourceIdentification With<T>() where T : IResource<T>
        => With<T>(T.KeyFor);
}
