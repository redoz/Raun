namespace Raun;

/// <summary>The operation a step performs on a resource.</summary>
public enum ResourceEffect
{
    /// <summary>Create a resource. This access is exclusive.</summary>
    Create,
    /// <summary>Observe a resource without a change. This access is shared.</summary>
    Read,
    /// <summary>Change a resource. This access is exclusive.</summary>
    Update,
    /// <summary>Remove a resource. This access is exclusive.</summary>
    Delete,
}

/// <summary>Maps an effect to its access mode and same-step precedence.</summary>
public static class ResourceEffectExtensions
{
    /// <summary>Read uses shared access. All other effects use exclusive access.</summary>
    public static LockMode ToLockMode(this ResourceEffect effect) => effect switch
    {
        ResourceEffect.Read => LockMode.Shared,
        ResourceEffect.Create or ResourceEffect.Update or ResourceEffect.Delete => LockMode.Exclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };

    /// <summary>Delete takes precedence over Update, Create, and Read, in that order.</summary>
    public static int Precedence(this ResourceEffect effect) => effect switch
    {
        ResourceEffect.Delete => 4,
        ResourceEffect.Update => 3,
        ResourceEffect.Create => 2,
        ResourceEffect.Read => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(effect)),
    };
}
