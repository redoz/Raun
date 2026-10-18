namespace Raun.Model;

/// <summary>A role-named connection between canonical resource identities.</summary>
public sealed record ResourceLink
{
    /// <summary>The identity that holds the relationship.</summary>
    public required ResourceIdentity Source { get; init; }
    /// <summary>The owner-defined role name.</summary>
    public required string Role { get; init; }
    /// <summary>The identity named by the relationship.</summary>
    public required ResourceIdentity Target { get; init; }
}
