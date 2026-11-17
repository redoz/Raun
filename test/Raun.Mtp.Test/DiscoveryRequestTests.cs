using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
using Raun.Mtp;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Behavioral tests for the discovery request path: <c>OnDiscover</c> enumerates the
/// <see cref="ScenarioRegistry"/>, builds the per-step nodes, and publishes one
/// <see cref="TestNodeUpdateMessage"/> per step on the MTP message bus. Each scenario is registered
/// under a test-unique method name so the global registry stays isolated across tests.
/// </summary>
public class DiscoveryRequestTests
{
    private static ScenarioNode Node(int index, string stepId, string template) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        SourceFile = @"C:\src\S.cs",
        SourceLine = index + 1,
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    /// <summary>Registers a scenario under a unique method key and returns that key for cleanup.</summary>
    private static string RegisterScenario(string scenarioId, string display, params ScenarioNode[] nodes)
    {
        var method = $"Raun.Mtp.Test.Generated.{scenarioId}_{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => new ScenarioDefinition
        {
            ScenarioId = scenarioId,
            DisplayName = display,
            MethodName = method,
            Nodes = nodes,
        });
        return method;
    }

    [Fact]
    public async Task Discovery_publishes_one_node_update_per_step_of_registered_scenario()
    {
        RegisterScenario("disc-scn", "discovery scenario",
            Node(0, "a", "step a"),
            Node(1, "b", "step b"));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("disc-1");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnDiscover(uid, filter: null, bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);

        var updates = bus.NodesFor("disc-scn");
        Assert.Equal(2, updates.Count);
        Assert.Equal(["disc-scn:a", "disc-scn:b"], updates.Select(n => n.Uid.Value).Order());
    }

    [Fact]
    public async Task Discovered_nodes_carry_display_name_state_and_location()
    {
        RegisterScenario("meta-scn", "meta scenario", Node(0, "only", "the only step"));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("disc-2");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        await framework.OnDiscover(uid, filter: null, bus, () => { }, CancellationToken.None);

        var node = Assert.Single(bus.NodesFor("meta-scn"));
        Assert.Equal("meta-scn:only", node.Uid.Value);
        Assert.Equal("1. the only step", node.DisplayName);
        Assert.NotEmpty(node.Properties.OfType<DiscoveredTestNodeStateProperty>());
        var location = Assert.Single(node.Properties.OfType<TestFileLocationProperty>());
        Assert.Equal(@"C:\src\S.cs", location.FilePath);
        Assert.Equal(1, location.LineSpan.Start.Line);
    }

    [Fact]
    public async Task Discovery_honors_a_uid_filter_and_emits_only_matching_nodes()
    {
        // An IDE can issue a *filtered* discovery (e.g. to refresh specific nodes). When a
        // TestNodeUidListFilter is present, discovery must emit only the named nodes — matching how
        // xUnit's MTP discovery sink applies its filter — not the whole scenario.
        RegisterScenario("filt-scn", "filtered scenario",
            Node(0, "a", "step a"),
            Node(1, "b", "step b"),
            Node(2, "c", "step c"));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("disc-filter");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var filter = new TestNodeUidListFilter([new TestNodeUid("filt-scn:b")]);
        await framework.OnDiscover(uid, filter, bus, () => { }, CancellationToken.None);

        var node = Assert.Single(bus.NodesFor("filt-scn"));
        Assert.Equal("filt-scn:b", node.Uid.Value);
    }

    [Fact]
    public async Task Each_published_update_carries_the_sessions_uid()
    {
        RegisterScenario("sess-scn", "session scenario", Node(0, "a", "step a"));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("disc-session-uid");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        await framework.OnDiscover(uid, filter: null, bus, () => { }, CancellationToken.None);

        var update = Assert.Single(
            bus.Updates,
            u => u.TestNode.Uid.Value.StartsWith("sess-scn", StringComparison.Ordinal));
        Assert.Equal("disc-session-uid", update.SessionUid.Value);
    }

    /// <summary>
    /// Collects every <see cref="TestNodeUpdateMessage"/> published during discovery. The registry is
    /// process-global (other tests register their own scenarios), so helpers filter by scenario id.
    /// </summary>
    private sealed class RecordingMessageBus : IMessageBus
    {
        private readonly List<TestNodeUpdateMessage> updates = [];

        public IReadOnlyList<TestNodeUpdateMessage> Updates
        {
            get { lock (updates) { return [.. updates]; } }
        }

        public List<TestNode> NodesFor(string scenarioId)
            => Updates
                .Select(u => u.TestNode)
                .Where(n => n.Uid.Value.StartsWith(scenarioId + ":", StringComparison.Ordinal))
                .ToList();

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            if (data is TestNodeUpdateMessage update)
            {
                lock (updates)
                {
                    updates.Add(update);
                }
            }

            return Task.CompletedTask;
        }
    }
}
