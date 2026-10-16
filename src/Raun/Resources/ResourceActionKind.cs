namespace Raun;

/// <summary>The operation a step performs on one resource.</summary>
public enum ResourceActionKind
{
    /// <summary>Create a new resource. This access is exclusive.</summary>
    Create,
    /// <summary>Get an existing resource. This access is shared.</summary>
    Load,
    /// <summary>Use an existing resource without a change. This access is shared.</summary>
    Read,
    /// <summary>Change an existing resource. This access is exclusive.</summary>
    Edit,
    /// <summary>Remove an existing resource. This access is exclusive.</summary>
    Delete,
}
