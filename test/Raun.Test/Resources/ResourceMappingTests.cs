using Xunit;

namespace Raun.Test.Resources;

public sealed class ResourceMappingTests
{
    [Fact]
    public void Metadata_does_not_initialize_owner_or_invoke_extractors()
    {
        var mapping = ResourceMapping.Alternate<ThrowingOwner, Row>();
        Assert.Equal(typeof(ThrowingOwner), mapping.OwnerType);
        Assert.Equal(typeof(Row), mapping.SourceType);
    }

    public sealed record Row(string Id, string Name);

    public sealed class ThrowingOwner : IResourceAlternate<Row>
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065:Do not raise exceptions in unexpected locations", Justification = "Proves descriptor discovery never initializes the owner.")]
        static ThrowingOwner() => throw new InvalidOperationException("owner initialized");
        public static ResourceKey KeyFor(Row value) => throw new InvalidOperationException(value.Id);
    }
}
