namespace Raun;

/// <summary>
/// The conflict class a <see cref="LifecycleVerb"/> implies: <c>Read</c>/<c>Load</c>/<c>Reference</c>/
/// <c>Consume</c> are <see cref="Shared"/>; <c>Create</c>/<c>Edit</c>/<c>Delete</c> are
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
