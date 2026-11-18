using Raun.Running;

namespace Raun.Mtp;

/// <summary>
/// Carries the platform's graceful stop (<c>--maximum-failed-tests</c>) to the runs in progress, and
/// only to those. The platform holds one capability for the whole host, while a host can serve many
/// sessions one after another; a stop asked for in one of them must not keep every later session
/// from admitting scenarios. Each run therefore gets a <see cref="RunStopSignal"/> of its own for as
/// long as it lasts.
/// </summary>
internal sealed class RunStopRelay
{
    private readonly object gate = new();
    private readonly List<RunStopSignal> running = [];

    /// <summary>Starts a run: its signal receives stop requests until the returned handle is disposed.</summary>
    public Run Begin()
    {
        var signal = new RunStopSignal();
        lock (gate)
        {
            running.Add(signal);
        }

        return new Run(this, signal);
    }

    /// <summary>Asks every run in progress to stop admitting scenarios. A later run is not affected.</summary>
    public void Request()
    {
        lock (gate)
        {
            foreach (var signal in running)
            {
                signal.Request();
            }
        }
    }

    internal sealed class Run(RunStopRelay owner, RunStopSignal signal) : IDisposable
    {
        public RunStopSignal Signal { get; } = signal;

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.running.Remove(Signal);
            }
        }
    }
}
