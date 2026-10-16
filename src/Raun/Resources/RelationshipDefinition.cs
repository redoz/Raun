namespace Raun;

/// <summary>Immutable metadata for one relationship property on a resource attribute.</summary>
public sealed class RelationshipDefinition
{
    internal RelationshipDefinition(string propertyName, ResourceRelation relation, Type? resourceType, bool isCollection, bool required)
    { PropertyName = propertyName; Relation = relation; ResourceType = resourceType; IsCollection = isCollection; Required = required; }
    /// <summary>The attribute property whose supplied values bind step subjects.</summary>
    public string PropertyName { get; }
    /// <summary>The relationship from the decorated source to each bound target.</summary>
    public ResourceRelation Relation { get; }
    /// <summary>The expected canonical resource group, or null for any group.</summary>
    public Type? ResourceType { get; }
    /// <summary>True when the property accepts an array of target names.</summary>
    public bool IsCollection { get; }
    /// <summary>True when every usage must supply at least one target.</summary>
    public bool Required { get; }
}
