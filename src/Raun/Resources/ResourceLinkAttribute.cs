namespace Raun;

/// <summary>Combines a typed relationship role with an action on its target parameter.</summary>
/// <remarks>
/// Declare a concrete nested attribute in the owning resource action. Its name, without Attribute, is the role.
/// TResource identifies the expected canonical resource group. Mapped representations and typed keys are accepted.
/// TAction declares the target operation and its access. The relationship itself grants no access.
/// Each declared role must bind one step parameter. Raun inspects metadata without running attribute code.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public abstract class ResourceLinkAttribute<TResource, TAction> : Attribute
    where TResource : notnull where TAction : ResourceAttribute;

/// <summary>Links this parameter to a declared role on an action applied to the same step.</summary>
/// <remarks>
/// Declare the parameter effect separately. This attribute records a link and grants no access.
/// The role must exist on TOwner. Set Source when more than one owner application could supply the source.
/// Raun records a return-owned link only after the return value exists.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class LinkAttribute<TOwner>(string role) : Attribute where TOwner : ResourceAttribute
{
    /// <summary>The exact role name declared by the owner action.</summary>
    public string Role { get; } = role;
    /// <summary>The source parameter name or Subject.Return. Omit it for one unambiguous owner.</summary>
    public string? Source { get; set; }
}
