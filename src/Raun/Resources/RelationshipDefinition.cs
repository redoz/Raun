namespace Raun;

/// <summary>Immutable metadata for one role declared by a nested resource link attribute.</summary>
public sealed class RelationshipDefinition
{
    internal RelationshipDefinition(Type ownerType, Type markerType, string name, Type resourceType, Type actionType)
    { OwnerType = ownerType; MarkerType = markerType; Name = name; ResourceType = resourceType; ActionType = actionType; }
    /// <summary>The action type that declares this role.</summary>
    public Type OwnerType { get; }
    /// <summary>The concrete nested parameter attribute.</summary>
    public Type MarkerType { get; }
    /// <summary>The exact role name, without an Attribute suffix.</summary>
    public string Name { get; }
    /// <summary>The expected canonical resource group.</summary>
    public Type ResourceType { get; }
    /// <summary>The action applied by the packaged marker.</summary>
    public Type ActionType { get; }
}
