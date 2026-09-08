using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The capabilities Raun declares to the platform: a graceful stop (which is what unlocks
/// --maximum-failed-tests) and the banner.
/// </summary>
public class CapabilitiesTests
{
    [Fact]
    public void A_fresh_stop_signal_is_not_requested()
        => Assert.False(new RunStopSignal().IsStopRequested);

    [Fact]
    public async Task The_graceful_stop_capability_requests_the_stop()
    {
        var signal = new RunStopSignal();
        var capability = new RaunGracefulStopCapability(signal);

        await capability.StopTestExecutionAsync(CancellationToken.None);

        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public void Requesting_a_stop_twice_is_harmless()
    {
        var signal = new RunStopSignal();
        signal.Request();
        signal.Request();

        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public async Task The_banner_names_Raun()
    {
        var banner = await new RaunBannerCapability().GetBannerMessageAsync();

        Assert.NotNull(banner);
        Assert.Contains("Raun", banner, StringComparison.Ordinal);
    }
}
