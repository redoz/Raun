namespace Raun;

/// <summary>Declare a relationship from the decorated resource to a named step subject.</summary>
/// <remarks>The source needs a Create, Load, or Edit action. Method placement describes the return value. Consume does not delete the target.</remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RelatesToAttribute(ResourceRelation relation, string target) : Attribute
{
    /// <summary>The relationship kind. Target access is shared.</summary>
    public ResourceRelation Relation { get; } = relation;
    /// <summary>The target parameter name or Subject.Return. Use nameof for a parameter.</summary>
    public string Target { get; } = target;
}
