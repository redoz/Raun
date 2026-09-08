using System.Collections.Concurrent;

namespace Raun;

/// <summary>One registered cleanup, tagged with the step that registered it.</summary>
/// <param name="OwningStepIndex">The step whose execution registered this cleanup; the scheduler
/// orders teardown by the reverse topological position of this step.</param>
/// <param name="Sequence">Global registration order, used to break ties within one step.</param>
/// <param name="Kind">Whether the scenario's policy may skip this cleanup.</param>
/// <param name="Cleanup">The work to run, handed the teardown node's <see cref="ScenarioContext"/>.</param>
internal readonly record struct TeardownRegistration(
    int OwningStepIndex, int Sequence, Cleanup Kind, Func<ScenarioContext, Task> Cleanup);

/// <summary>
/// Scenario-scoped collector for cleanups registered by steps. Owned by the scheduler and shared by
/// every step's <see cref="ScenarioContext"/> — steps register concurrently, so this is the
/// synchronized object while the context itself stays per-step.
/// </summary>
internal sealed class TeardownLog
{
    private readonly ConcurrentQueue<TeardownRegistration> _entries = new();
    private int _sequence = -1;

    /// <summary>Records a cleanup for <paramref name="owningStepIndex"/>.</summary>
    public void Add(int owningStepIndex, Cleanup kind, Func<ScenarioContext, Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _entries.Enqueue(new TeardownRegistration(
            owningStepIndex, Interlocked.Increment(ref _sequence), kind, cleanup));
    }

    /// <summary>Registrations in registration order.</summary>
    public IReadOnlyList<TeardownRegistration> Entries => [.. _entries];
}
