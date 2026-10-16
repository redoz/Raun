namespace Raun;

/// <summary>How a source resource uses a target resource.</summary>
public enum ResourceRelation
{
    /// <summary>The source keeps a link to the target. Target access is shared.</summary>
    Reference,
    /// <summary>The target is an input to the source. Target access is shared; this does not delete the target.</summary>
    Consume,
}
