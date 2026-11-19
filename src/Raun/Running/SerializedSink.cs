using Raun.Reporting;

namespace Raun.Running;

/// <summary>Gives a sink its events in turn. Unlike <see cref="RunEventBus"/> it isolates nothing:
/// what the sink throws reaches the publisher.</summary>
internal sealed class SerializedSink(IRunEventSink inner) : IRunEventSink
{
    private readonly Turns _turns = new();

    public ValueTask PublishAsync(RunEvent evt) => new(_turns.TakeAsync(() => inner.PublishAsync(evt).AsTask()));
}
