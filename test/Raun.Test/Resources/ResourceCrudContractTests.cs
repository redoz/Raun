using Xunit;

namespace Raun.Test.Resources;

public class ResourceCrudContractTests
{
    [Fact]
    public void Resource_effects_are_exactly_CRUD_and_actions_use_that_enum()
    {
        var effect = typeof(ReadAttribute).Assembly.GetType("Raun.ResourceEffect");
        Assert.NotNull(effect);
        Assert.True(effect.IsEnum);
        Assert.Equal(["Create", "Read", "Update", "Delete"], Enum.GetNames(effect));
        Assert.Equal(effect, typeof(ResourceAction).GetProperty(nameof(ResourceAction.Effect))!.PropertyType);
        Assert.NotNull(typeof(ReadAttribute).Assembly.GetType("Raun.SeedAttribute"));
        Assert.NotNull(typeof(ReadAttribute).Assembly.GetType("Raun.UpdatedAttribute"));
    }
}
