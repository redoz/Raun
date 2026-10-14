namespace Raun;

/// <summary>Identifies a string or <see cref="ResourceKey"/> parameter as the key of
/// <typeparamref name="TResource"/>, so its access roles and title references use that resource's identity.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ResourceKeyAttribute<TResource> : Attribute where TResource : notnull;