using Xunit;

namespace Raun.Test;

public class ScenarioCaseAttributeTests
{
    [Fact]
    public void Cases_are_repeatable_method_metadata_with_named_arguments()
    {
        var attribute = new ScenarioCaseAttribute("reader", "/orders", 200);
        Assert.Equal("reader", attribute.CaseName);
        Assert.Equal(new object?[] { "/orders", 200 }, attribute.Arguments);
        var usage = Assert.IsType<AttributeUsageAttribute>(Attribute.GetCustomAttribute(typeof(ScenarioCaseAttribute), typeof(AttributeUsageAttribute)));
        Assert.True(usage.AllowMultiple);
        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
    }
}
