namespace Raun;

/// <summary>Declare the single base effect of a resource action attribute.</summary>
/// <remarks>The kind determines shared or exclusive access. A derived action cannot replace an inherited definition.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ResourceActionDefinitionAttribute(ResourceActionKind kind) : Attribute
{
    /// <summary>The operation recorded by the action. The step code must perform this operation.</summary>
    public ResourceActionKind Kind { get; } = kind;
}
