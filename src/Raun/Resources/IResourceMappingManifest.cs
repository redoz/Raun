namespace Raun;

/// <summary>The generated mapping-library protocol; consumers do not implement this interface.</summary>
public interface IResourceMappingManifest
{
    static abstract IReadOnlyList<ResourceMapping> GetMappings();
}
