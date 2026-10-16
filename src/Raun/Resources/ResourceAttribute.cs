namespace Raun;

/// <summary>Declare an action on a step parameter or return value.</summary>
/// <remarks>Declare one ResourceActionDefinition in the class hierarchy. Raun reads metadata without running attribute code.</remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
public abstract class ResourceAttribute : Attribute
{
    /// <summary>The relationship properties declared by this attribute type. These are schema entries, not bound step arguments.</summary>
    public IReadOnlyList<RelationshipDefinition> Relationships => ResourceAction.For(GetType()).Relationships;
}
