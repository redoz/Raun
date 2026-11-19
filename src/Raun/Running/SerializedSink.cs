using Raun.Reporting;

namespace Raun.Running;

/// <summary>
/// Gives a sink its events one at a time, in call order. Scenarios run concurrently and publish
/// from several async flows at once; a sink is told it needs no locking of its own, and this is
/// what makes that true for a sink handed to the run loop directly. Unlike
/// <see cref="RunEventBus"/> it isolates nothing: what the sink throws reaches the publisher.
/// </summary>
internal sealed class SerializedSink(IRunEventSink inner) : IRunEventSink
{
    private readonly object _turns = new();
    private Task _tail = Task.CompletedTask;

    public async ValueTask PublishAsync(RunEvent evt)
    {
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_turns)
        {
            previous = _tail;
            _tail = turn.Task;
        }

        await previous.ConfigureAwait(false); // never faults: every turn completes in the finally below
        try
        {
            await inner.PublishAsync(evt).ConfigureAwait(false);
        }
        finally
        {
            turn.SetResult();
        }
    }
}
