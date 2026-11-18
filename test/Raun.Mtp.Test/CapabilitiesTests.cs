using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The capabilities Raun declares to the platform: a graceful stop (which is what unlocks
/// --maximum-failed-tests) and the banner.
/// </summary>
public class CapabilitiesTests
{
    [Fact]
    public async Task The_graceful_stop_capability_stops_the_run_in_progress()
    {
        var relay = new RunStopRelay();
        var capability = new RaunGracefulStopCapability(relay);
        using var run = relay.Begin();

        await capability.StopTestExecutionAsync(CancellationToken.None);

        Assert.True(run.Signal.IsStopRequested);
    }

    [Fact]
    public async Task A_stop_asked_for_in_one_run_does_not_reach_the_next_run_of_the_same_host()
    {
        var relay = new RunStopRelay();
        var capability = new RaunGracefulStopCapability(relay);
        using (relay.Begin())
        {
            await capability.StopTestExecutionAsync(CancellationToken.None);
        }

        using var next = relay.Begin();

        Assert.False(next.Signal.IsStopRequested);
    }

    [Fact]
    public async Task A_stop_reaches_every_run_in_progress()
    {
        var relay = new RunStopRelay();
        using var one = relay.Begin();
        using var two = relay.Begin();

        await new RaunGracefulStopCapability(relay).StopTestExecutionAsync(CancellationToken.None);

        Assert.True(one.Signal.IsStopRequested);
        Assert.True(two.Signal.IsStopRequested);
    }

    [Fact]
    public async Task The_banner_names_Raun()
    {
        var banner = await new RaunBannerCapability().GetBannerMessageAsync();

        Assert.NotNull(banner);
        Assert.Contains("Raun", banner, StringComparison.Ordinal);
    }
}
