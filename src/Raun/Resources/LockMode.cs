namespace Raun;

/// <summary>
/// The access mode required by a <see cref="ResourceEffect"/>. Read uses <see cref="Shared"/> access.
/// Create, Update, and Delete use
/// <see cref="Exclusive"/>. Two effects on one identity conflict when at least one is exclusive. The
/// <see cref="ResourceLedger"/> uses this to <em>detect</em> conflicting access between steps that
/// nothing orders; no lock is ever taken and nothing waits. The <see cref="Raun.Scheduling.ContentionGate"/>
/// reuses the same two modes for admission across scenarios, where a refused scenario is retried by
/// the run loop rather than blocked.
/// </summary>
public enum LockMode
{
    /// <summary>Coexists with other shared access to the same identity.</summary>
    Shared,

    /// <summary>Conflicts with every other access to the same identity.</summary>
    Exclusive,
}
