using System.ComponentModel;
using System.Runtime.Loader;
using Xunit;

namespace Raun.Test.Resources;

public sealed class ResourceActionTests
{
    [Fact]
    public void Actions_use_one_declared_base_effect_and_optional_display_name()
    {
        var action = ResourceAction.For<ApprovedAttribute>();
        Assert.Equal(LifecycleVerb.Edit, action.Effect);
        Assert.Equal("Approve", action.Name);
        Assert.Equal(typeof(ApprovedAttribute), action.AttributeType);
        Assert.Equal("Validated", ResourceAction.For<ValidatedAttribute>().Name);
        Assert.Equal("Approve", ResourceAction.For<IndirectAttribute>().Name);
        Assert.Equal("Generic", ResourceAction.For<GenericAttribute<string>>().Name);
        Assert.Equal("Create", ResourceAction.For<CreatedAttribute>().Name);
        Assert.Null(ResourceAction.For<CreatedAttribute>().AttributeType);
        Assert.Same(action, ResourceAction.For(action.AttributeType!));
    }

    [Fact]
    public void Names_do_not_establish_identity_or_effects()
    {
        var left = ResourceAction.For<ApprovedAttribute>();
        var right = ResourceAction.For<CheckedAttribute>();
        Assert.Equal(left.Name, right.Name);
        Assert.NotEqual(left.AttributeType, right.AttributeType);
        Assert.Equal(LifecycleVerb.Read, right.Effect);
        Assert.Throws<ArgumentException>(() => ResourceAction.For<BlankAttribute>());
        Assert.Throws<ArgumentException>(() => ResourceAction.For<ObsoleteAttribute>());
    }

    [Fact]
    public void Metadata_does_not_run_user_initializers_or_constructors()
    {
        Assert.Equal("Throwing", ResourceAction.For<ThrowingAttribute>().Name);
        Assert.Equal("Throwing", ResourceAction.FromGenerated(typeof(ResourceActionTests), typeof(ThrowingAttribute).AssemblyQualifiedName!).Name);
    }

    [Fact]
    [Hidden]
    public void Generated_metadata_resolves_private_generic_types_in_the_scopes_load_context()
    {
        Assert.Equal(typeof(HiddenAttribute), ResourceAction.FromGenerated(typeof(ResourceActionTests), typeof(HiddenAttribute).AssemblyQualifiedName!).AttributeType);
        var type = typeof(GenericAttribute<PrivateValue>);
        Assert.Equal(type, ResourceAction.FromGenerated(typeof(ResourceActionTests), type.AssemblyQualifiedName!).AttributeType);
        var context = new AssemblyLoadContext("custom-actions", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(ResourceActionTests).Assembly.Location);
            var scope = assembly.GetType(typeof(ResourceActionTests).FullName!)!;
            var action = ResourceAction.FromGenerated(scope, typeof(ApprovedAttribute).AssemblyQualifiedName!);
            Assert.Equal(assembly, action.AttributeType!.Assembly);
            Assert.NotEqual(typeof(ApprovedAttribute), action.AttributeType);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void Default_action_names_must_contain_text()
    {
        Assert.Throws<ArgumentException>(() => ResourceAction.For<Attribute>());
        Assert.Equal("Explicit", ResourceAction.For<Named.Attribute>().Name);
    }

    [Fact]
    public void Cached_metadata_does_not_prevent_a_collectible_context_from_unloading()
    {
        var reference = LoadAndUnload();
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnload()
    {
        var context = new AssemblyLoadContext("unload-actions", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(ResourceActionTests).Assembly.Location);
        var scope = assembly.GetType(typeof(ResourceActionTests).FullName!)!;
        var action = ResourceAction.FromGenerated(scope, typeof(ApprovedAttribute).AssemblyQualifiedName!);
        Assert.Equal(assembly, action.AttributeType!.Assembly);
        var reference = new WeakReference(context);
        context.Unload();
        return reference;
    }

    private sealed class Attribute : EditedAttribute;
    private static class Named
    {
        [DisplayName("Explicit")]
        internal sealed class Attribute : EditedAttribute;
    }

    [DisplayName("Approve")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Exercises indirect action inheritance.")]
    public class ApprovedAttribute : EditedAttribute;
    public sealed class IndirectAttribute : ApprovedAttribute;
    public sealed class ValidatedAttribute : ReadAttribute;
    public sealed class GenericAttribute<T> : EditedAttribute;
    [DisplayName("Approve")]
    public sealed class CheckedAttribute : ReadAttribute;
    [DisplayName(" ")]
    public sealed class BlankAttribute : EditedAttribute;
    public sealed class ThrowingAttribute : EditedAttribute
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1065:Do not raise exceptions in unexpected locations", Justification = "Proves metadata lookup does not initialize the attribute.")]
        static ThrowingAttribute() => throw new InvalidOperationException("initialized");
        public ThrowingAttribute() => throw new InvalidOperationException("constructed");
    }
    private sealed class HiddenAttribute : EditedAttribute;
    public sealed record PrivateValue;
}
