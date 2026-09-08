using Raun.Running;
using Xunit;

namespace Raun.Test;

/// <summary>The stop flag an adapter hands the run loop: off until requested, idempotent after.</summary>
public class RunStopSignalTests
{
    [Fact]
    public void A_fresh_stop_signal_is_not_requested()
        => Assert.False(new RunStopSignal().IsStopRequested);

    [Fact]
    public void Requesting_a_stop_twice_is_harmless()
    {
        var signal = new RunStopSignal();
        signal.Request();
        signal.Request();

        Assert.True(signal.IsStopRequested);
    }
}
