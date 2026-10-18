using System.Diagnostics.CodeAnalysis;

namespace Raun;

/// <summary>The step creates a resource.</summary>
/// <remarks>
/// Use this attribute on a parameter, a return value, or a method. A method attribute applies to its return value.
/// Raun records parameter effects before the call and return effects after a successful call.
/// This effect requires exclusive access. Unordered access to the same resource causes a conflict.
/// The step code must perform the operation. Raun does not lock the external system.
/// Derive a domain attribute to name this operation. Use DisplayNameAttribute to override its report name.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Domain actions derive from their base effect.")]
[ResourceActionDefinition(ResourceEffect.Create)]
public class CreatedAttribute : ResourceAttribute;

/// <summary>The step observes a resource without a change.</summary>
/// <remarks>
/// Use this attribute on a parameter, a return value, or a method. A method attribute applies to its return value.
/// Raun records parameter effects before the call and return effects after a successful call.
/// This effect uses shared access. Unordered reads can share a resource; an exclusive use causes a conflict.
/// Fetching an existing resource is a Read. The step code must perform the operation.
/// Derive a domain attribute to name this operation. Use DisplayNameAttribute to override its report name.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Domain actions derive from their base effect.")]
[ResourceActionDefinition(ResourceEffect.Read)]
public class ReadAttribute : ResourceAttribute;

/// <summary>The step changes a resource.</summary>
/// <remarks>
/// Use this attribute on a parameter, a return value, or a method. A method attribute applies to its return value.
/// Raun records parameter effects before the call and return effects after a successful call.
/// This effect requires exclusive access. Unordered access to the same resource causes a conflict.
/// The step code must perform the change. Raun does not lock the external system.
/// Derive a domain attribute to name this operation. Use DisplayNameAttribute to override its report name.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Domain actions derive from their base effect.")]
[ResourceActionDefinition(ResourceEffect.Update)]
public class UpdatedAttribute : ResourceAttribute;

/// <summary>The step removes a resource.</summary>
/// <remarks>
/// Use this attribute on a parameter, a return value, or a method. A method attribute applies to its return value.
/// Raun records parameter effects before the call and return effects after a successful call.
/// This effect requires exclusive access. Unordered access to the same resource causes a conflict.
/// The step code must perform the removal. A relationship does not remove its target.
/// Derive a domain attribute to name this operation. Use DisplayNameAttribute to override its report name.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Domain actions derive from their base effect.")]
[ResourceActionDefinition(ResourceEffect.Delete)]
public class DeletedAttribute : ResourceAttribute;

/// <summary>The step creates test data directly in the system.</summary>
/// <remarks>
/// Use this action for setup that bypasses the normal application operation.
/// Seed has Create semantics and requires exclusive access. The step code must insert the data.
/// Parameter and return recording times are the same as CreatedAttribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Domain-specific seed actions can derive from this action.")]
public class SeedAttribute : CreatedAttribute;
