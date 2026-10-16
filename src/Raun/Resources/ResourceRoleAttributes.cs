using System.Diagnostics.CodeAnalysis;

namespace Raun;

/// <summary>The step creates a new resource.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>Raun records parameter effects before the call and return effects after the call.</para>
/// <para>This effect requires exclusive access to the resource.</para>
/// <para>Raun reports a conflict if an unordered step uses the same resource.</para>
/// <para>The step code must do the operation. Raun does not lock the external system.</para>
/// <para>Derive a custom attribute to give the effect a different report label.</para>
/// <para>The default label is the custom type name without the final Attribute suffix.</para>
/// <para>Use <see cref="System.ComponentModel.DisplayNameAttribute"/> on the custom type to set its label.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Consumers derive domain action attributes from this role.")]
public class CreatedAttribute : Attribute
{
    /// <summary>The resource keeps a reference to each specified resource.</summary>
    /// <remarks>
    /// <para>Each target has a separate identity and uses shared access.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] References { get; set; } = [];

    /// <summary>Each specified resource is an input to the resource that this step produces.</summary>
    /// <remarks>
    /// <para>This relation uses shared access. It does not delete the input resource.</para>
    /// <para>Declare a Delete effect if the step removes the input resource.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] Consumes { get; set; } = [];
}

/// <summary>The step gets an existing resource.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>Raun records parameter effects before the call and return effects after the call.</para>
/// <para>This effect requires shared access to the resource.</para>
/// <para>Unordered steps can share this access. An exclusive use causes a conflict.</para>
/// <para>The step code must do the operation. Raun does not lock the external system.</para>
/// <para>Derive a custom attribute to give the effect a different report label.</para>
/// <para>The default label is the custom type name without the final Attribute suffix.</para>
/// <para>Use <see cref="System.ComponentModel.DisplayNameAttribute"/> on the custom type to set its label.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Consumers derive domain action attributes from this role.")]
public class LoadedAttribute : Attribute
{
    /// <summary>The resource keeps a reference to each specified resource.</summary>
    /// <remarks>
    /// <para>Each target has a separate identity and uses shared access.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] References { get; set; } = [];

    /// <summary>Each specified resource is an input to the resource that this step produces.</summary>
    /// <remarks>
    /// <para>This relation uses shared access. It does not delete the input resource.</para>
    /// <para>Declare a Delete effect if the step removes the input resource.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] Consumes { get; set; } = [];
}

/// <summary>The step uses an existing resource without a change.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>Raun records parameter effects before the call and return effects after the call.</para>
/// <para>This effect requires shared access to the resource.</para>
/// <para>Unordered steps can share this access. An exclusive use causes a conflict.</para>
/// <para>The step code must do the operation. Raun does not lock the external system.</para>
/// <para>Derive a custom attribute to give the effect a different report label.</para>
/// <para>The default label is the custom type name without the final Attribute suffix.</para>
/// <para>Use <see cref="System.ComponentModel.DisplayNameAttribute"/> on the custom type to set its label.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Consumers derive domain action attributes from this role.")]
public class ReadAttribute : Attribute;

/// <summary>The step changes an existing resource.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>Raun records parameter effects before the call and return effects after the call.</para>
/// <para>This effect requires exclusive access to the resource.</para>
/// <para>Raun reports a conflict if an unordered step uses the same resource.</para>
/// <para>The step code must do the operation. Raun does not lock the external system.</para>
/// <para>Derive a custom attribute to give the effect a different report label.</para>
/// <para>The default label is the custom type name without the final Attribute suffix.</para>
/// <para>Use <see cref="System.ComponentModel.DisplayNameAttribute"/> on the custom type to set its label.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Consumers derive domain action attributes from this role.")]
public class EditedAttribute : Attribute
{
    /// <summary>The resource keeps a reference to each specified resource.</summary>
    /// <remarks>
    /// <para>Each target has a separate identity and uses shared access.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] References { get; set; } = [];

    /// <summary>Each specified resource is an input to the resource that this step produces.</summary>
    /// <remarks>
    /// <para>This relation uses shared access. It does not delete the input resource.</para>
    /// <para>Declare a Delete effect if the step removes the input resource.</para>
    /// <para>Set each value to a parameter name or <see cref="Subject.Return"/>.</para>
    /// <para>Use nameof(parameter) for a parameter name.</para>
    /// </remarks>
    public string[] Consumes { get; set; } = [];
}

/// <summary>The step removes an existing resource.</summary>
/// <remarks>
/// <para>Use this attribute on a parameter, a return value, or a method.</para>
/// <para>An attribute on a method applies to its return value.</para>
/// <para>Raun records parameter effects before the call and return effects after the call.</para>
/// <para>This effect requires exclusive access to the resource.</para>
/// <para>Raun reports a conflict if an unordered step uses the same resource.</para>
/// <para>The step code must do the operation. Raun does not lock the external system.</para>
/// <para>Derive a custom attribute to give the effect a different report label.</para>
/// <para>The default label is the custom type name without the final Attribute suffix.</para>
/// <para>Use <see cref="System.ComponentModel.DisplayNameAttribute"/> on the custom type to set its label.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Consumers derive domain action attributes from this role.")]
public class DeletedAttribute : Attribute;
