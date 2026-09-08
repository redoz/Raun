using Raun.Running;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The capabilities Raun declares to the platform: a graceful stop (which is what unlocks
/// --maximum-failed-tests) and the banner.
/// </summary>
public class CapabilitiesTests
{
    [Fact]
    public async Task The_graceful_stop_capability_requests_the_stop()
    {
        var signal = new RunStopSignal();
        var capability = new RaunGracefulStopCapability(signal);

        await capability.StopTestExecutionAsync(CancellationToken.None);

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
