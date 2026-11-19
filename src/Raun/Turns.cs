namespace Raun;

/// <summary>
/// Work handed in from several async flows at once runs one piece at a time, in the order it was
/// handed in. A piece that throws gives up its turn like any other.
/// </summary>
internal sealed class Turns
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public async Task TakeAsync(Func<Task> work)
    {
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tail;
            _tail = turn.Task;
        }

        await previous.ConfigureAwait(false); // a turn's task never faults
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            turn.SetResult();
        }
    }
}
