namespace Raun.Reporting;

/// <summary>A subscriber to the run-event stream. The bus awaits each call serially.</summary>
/// <remarks>
/// A sink sees one event at a time, in publication order, so it needs no locking. It must not
/// publish to the bus that is delivering to it from inside <see cref="PublishAsync"/>: the bus's
/// turn queue is not reentrant, and such a call waits on itself.
/// </remarks>
public interface IRunEventSink
{
    /// <summary>Handle one event. May be async; the bus awaits it before the next sink/event.</summary>
    ValueTask PublishAsync(RunEvent evt);
}
