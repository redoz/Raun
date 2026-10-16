using System;
using System.Linq;
using Raun;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>The role attributes exist with the right usage targets (return/parameter/method shorthand).</summary>
public class ResourceRoleAttributeTests
{
    static AttributeTargets Targets<T>() where T : Attribute =>
        typeof(T).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single().ValidOn;

    [Fact]
    public void All_primary_roles_support_the_same_locations()
    {
        var expected = AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method;
        Assert.Equal(expected, Targets<CreatedAttribute>());
        Assert.Equal(expected, Targets<LoadedAttribute>());
        Assert.Equal(expected, Targets<ReadAttribute>());
        Assert.Equal(expected, Targets<EditedAttribute>());
        Assert.Equal(expected, Targets<DeletedAttribute>());
    }

    [Fact]
    public void Return_roles_allow_return_and_method()
    {
        Assert.True(Targets<CreatedAttribute>().HasFlag(AttributeTargets.ReturnValue));
        Assert.True(Targets<CreatedAttribute>().HasFlag(AttributeTargets.Method));
        Assert.True(Targets<LoadedAttribute>().HasFlag(AttributeTargets.ReturnValue));
        Assert.True(Targets<LoadedAttribute>().HasFlag(AttributeTargets.Method));
    }

    [Fact]
    public void Parameter_roles_allow_parameters()
    {
        Assert.True(Targets<ReadAttribute>().HasFlag(AttributeTargets.Parameter));
        Assert.True(Targets<DeletedAttribute>().HasFlag(AttributeTargets.Parameter));
    }

    [Fact]
    public void Edited_is_valid_on_both_parameter_and_return()
    {
        var t = Targets<EditedAttribute>();
        Assert.True(t.HasFlag(AttributeTargets.Parameter));
        Assert.True(t.HasFlag(AttributeTargets.ReturnValue));
        Assert.True(t.HasFlag(AttributeTargets.Method));
    }
}
