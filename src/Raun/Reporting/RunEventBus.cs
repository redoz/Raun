namespace Raun.Reporting;

/// <summary>
/// Fans a <see cref="RunEvent"/> out to child sinks serially, in registration order, awaiting each.
/// A throwing sink is isolated: the bus records its first error in <see cref="Failures"/> and keeps
/// delivering to the remaining sinks and to that sink on later events. A broken report sink must
/// never fail the run or starve the MTP reporter (design §3.A "Failure isolation").
/// </summary>
public sealed class RunEventBus : IRunEventSink
{
    private readonly IReadOnlyList<IRunEventSink> _sinks;
    private readonly Exception?[] _firstError;
    private readonly List<Exception> _failures = [];
    private readonly object _turns = new();
    private Task _tail = Task.CompletedTask;

    public RunEventBus(IReadOnlyList<IRunEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
        _firstError = new Exception?[sinks.Count];
    }

    /// <summary>The first error each failed sink raised, in sink order; empty when all sinks held.</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    // THREADING: scenarios run concurrently, so PublishAsync is called from several async flows at
    // once. Publications take turns in call order: each waits for the previous one to finish before
    // delivering, so every sink still sees one event at a time and the accumulators behind them
    // (HtmlReportModelBuilder, _failures) need no locking. Within one scenario the scheduler raises
    // callbacks serially, so a scenario's own events stay in order; different scenarios interleave.
    // A sink must not call back into the bus from inside its own PublishAsync before yielding — the
    // turn queue is not reentrant and such a call would wait on itself.
    public async ValueTask PublishAsync(RunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

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
            await DeliverAsync(evt).ConfigureAwait(false);
        }
        finally
        {
            turn.SetResult();
        }
    }

    private async Task DeliverAsync(RunEvent evt)
    {
        for (var i = 0; i < _sinks.Count; i++)
        {
            try
            {
                await _sinks[i].PublishAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_firstError[i] is null)
                {
                    _firstError[i] = ex;
                    _failures.Add(ex);
                }
            }
        }
    }
}
