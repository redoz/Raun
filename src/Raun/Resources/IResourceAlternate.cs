namespace Raun;

/// <summary>Declares an alternate representation of the implementing resource owner.</summary>
public interface IResourceAlternate<TAlternate> : IResource
{
    static abstract ResourceKey KeyFor(TAlternate value);
    static virtual string? LabelFor(TAlternate value) => null;
}
