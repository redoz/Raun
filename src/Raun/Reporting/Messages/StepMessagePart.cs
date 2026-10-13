namespace Raun;

/// <summary>An immutable literal or formatted value in a recorded message.</summary>
public sealed record StepMessagePart(string Text, ResourceIdentity? Resource = null, string? Data = null, bool DataTruncated = false);
