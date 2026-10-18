namespace Raun;

/// <summary>Declare the single base effect of a resource action attribute.</summary>
/// <remarks>The effect determines shared or exclusive access. A derived action cannot replace an inherited definition.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ResourceActionDefinitionAttribute(ResourceEffect effect) : Attribute
{
    /// <summary>The operation recorded by the action. The step code must perform this operation.</summary>
    public ResourceEffect Effect { get; } = effect;
}
