using Raun.Running;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Preflight as the platform sees it: discovered as a node when a delegate is supplied, and never
/// invoked by a discovery request. The loop-level behaviour lives in <c>Raun.Test</c>.
/// </summary>
public class PreflightDiscoveryTests
{
    [Fact]
    public void A_preflight_node_is_discovered_when_a_delegate_is_supplied()
    {
        var nodes = RaunDiscoverer.BuildNodes(Preflight.Definition(_ => Task.CompletedTask));

        var node = Assert.Single(nodes);
        Assert.Equal("raun:preflight", node.Uid.Value);
        Assert.Contains("Preflight", node.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_never_invokes_the_preflight_delegate()
    {
        // A --list-tests request must not start containers or migrate a database.
        var invoked = false;
        var framework = new RaunTestFramework(
            new StubProvider(), simulateTime: false, userServices: null,
            preflight: _ => { invoked = true; return Task.CompletedTask; });

        var uid = new Microsoft.Testing.Platform.TestHost.SessionUid("preflight-discover");
        await framework.CreateTestSession(uid);
        await framework.OnDiscover(uid, filter: null, new DiscardBus(), () => { }, CancellationToken.None);

        Assert.False(invoked);
    }

    private sealed class DiscardBus : Microsoft.Testing.Platform.Messages.IMessageBus
    {
        public Task PublishAsync(
            Microsoft.Testing.Platform.Extensions.Messages.IDataProducer dataProducer,
            Microsoft.Testing.Platform.Extensions.Messages.IData data) => Task.CompletedTask;
    }

    private sealed class StubProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(Microsoft.Testing.Platform.CommandLine.ICommandLineOptions)
                ? new NoOptions()
                : null;

        private sealed class NoOptions : Microsoft.Testing.Platform.CommandLine.ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => false;

            public bool TryGetOptionArgumentList(string optionName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments)
            {
                arguments = null;
                return false;
            }
        }
    }
}
