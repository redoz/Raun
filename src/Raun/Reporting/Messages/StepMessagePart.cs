namespace Raun;

/// <summary>An immutable literal or formatted value in a recorded message.</summary>
public sealed record StepMessagePart(string Text, ResourceIdentity? Resource = null, string? Data = null, bool DataTruncated = false)
{
    /// <summary>Full raw JSON captured when this object reference was materialized; null when unavailable or identity-only.</summary>
    public string? Snapshot { get; init; }
}
