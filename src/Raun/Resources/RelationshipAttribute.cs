using System.Diagnostics.CodeAnalysis;

namespace Raun;

/// <summary>Declare the relationship held by an attribute's string or string-array property.</summary>
/// <remarks>The supplied property values name step parameters, or Subject.Return. The attribute location identifies the source.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "The generic marker adds a canonical resource expectation.")]
public class RelationshipAttribute(ResourceRelation relation) : Attribute
{
    /// <summary>The source-to-target relationship. Both kinds use shared target access.</summary>
    public ResourceRelation Relation { get; } = relation;
    /// <summary>Require an explicit, nonempty binding at each attribute usage. Default is true.</summary>
    public bool Required { get; set; } = true;
}

/// <summary>Declare a relationship whose target must belong to the canonical group of TResource.</summary>
/// <remarks>Mapped rows, DTOs, domain objects, and key-only references can satisfy the same expectation. CLR assignability is not required.</remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RelationshipAttribute<TResource>(ResourceRelation relation) : RelationshipAttribute(relation);
