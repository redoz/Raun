using Xunit;

namespace Raun.Test.Resources;

public class SubjectAttributeTests
{
    private sealed class Booked : CreatedAttribute;

    [Fact]
    public void Raw_link_captures_role_and_explicit_source()
    {
        var link = new LinkAttribute<Booked>("Patient") { Source = Subject.Return };
        Assert.Equal("Patient", link.Role);
        Assert.Equal("<return>", link.Source);
    }

    [Fact]
    public void Raw_link_uses_unambiguous_owner_by_default()
        => Assert.Null(new LinkAttribute<Booked>("Patient").Source);

    [Fact]
    public void Subject_Return_is_the_reserved_token() => Assert.Equal("<return>", Subject.Return);
}
