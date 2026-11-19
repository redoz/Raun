namespace Raun.Reporting;

/// <summary>
/// Fans a <see cref="RunEvent"/> out to child sinks serially, in registration order, awaiting each.
/// A throwing sink is isolated: the bus records its first error in <see cref="Failures"/> and keeps
/// delivering to the remaining sinks and to that sink on later events. A broken report sink must
/// never fail the run or starve the MTP reporter.
/// </summary>
/// <remarks>
/// Scenarios run concurrently, so <see cref="PublishAsync"/> is called from several async flows at
/// once. Publications take turns in call order: each waits for the previous one to finish before
/// delivering, so a sink sees one event at a time and needs no locking of its own. A scenario's own
/// events stay in order; events of different scenarios interleave.
/// </remarks>
public sealed class RunEventBus : IRunEventSink
{
    private readonly IReadOnlyList<IRunEventSink> _sinks;
    private readonly Exception?[] _firstError;
    private readonly List<Exception> _failures = [];
    private readonly Turns _turns = new();

    public RunEventBus(IReadOnlyList<IRunEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
        _firstError = new Exception?[sinks.Count];
    }

    /// <summary>The first error each failed sink raised, in sink order; empty when all sinks held.</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    /// <summary>The first error <paramref name="sink"/> raised, or <see langword="null"/> when it
    /// held or is not one of this bus's sinks.</summary>
    public Exception? FailureOf(IRunEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        for (var i = 0; i < _sinks.Count; i++)
        {
            if (ReferenceEquals(_sinks[i], sink))
            {
                return _firstError[i];
            }
        }

        return null;
    }

    /// <summary>Delivers one event to every sink, after every earlier publication has finished.</summary>
    public ValueTask PublishAsync(RunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return new ValueTask(_turns.TakeAsync(() => DeliverAsync(evt)));
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
