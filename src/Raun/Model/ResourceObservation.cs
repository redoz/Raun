namespace Raun.Model;

/// <summary>
/// One symbolic effect a step declared on a resource: the trace event powering the report's resource
/// lane and debugging ("which step deleted User:123?").
/// </summary>
public sealed record ResourceObservation
{
    /// <summary>The resource operation declared by the step: Create, Read, Update, or Delete.</summary>
    public required ResourceEffect Effect { get; init; }

    /// <summary>The optional domain action. The base Effect still controls access.</summary>
    public ResourceAction? Action { get; init; }

    /// <summary>The resource identity the effect targets.</summary>
    public required ResourceIdentity Identity { get; init; }

    /// <summary>Optional data snapshot shown in the trace (e.g. the resource instance).</summary>
    public object? Data { get; init; }

    /// <summary>Human-readable label frozen when the effect was recorded.</summary>
    public string? Label { get; init; }

    /// <summary>Full raw JSON frozen when the resource was recorded, rather than when a report is generated.</summary>
    public string? DataSnapshot { get; init; }

    /// <summary>Whether recording-time capture was attempted; an unavailable snapshot must not be replaced with live data.</summary>
    public bool DataSnapshotCaptured { get; init; }

    /// <summary>Stable id of the step that produced the effect.</summary>
    public string? StepId { get; init; }

    /// <summary>Display name of the step that produced the effect.</summary>
    public string? StepDisplayName { get; init; }

    /// <summary>When the effect was recorded.</summary>
    public System.DateTimeOffset Timestamp { get; init; }

    /// <summary>The access mode required by this effect.</summary>
    public LockMode Mode => Effect.ToLockMode();
}
