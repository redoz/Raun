using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Xunit;
using Xunit.Sdk;

namespace Raun.Mtp.Test;

/// <summary>
/// A scenario as one test, end to end through <see cref="RaunTestFramework"/>: what a runner that
/// executes every test on its own (Stryker.NET) discovers, asks for and gets back. With one test per
/// step such a runner re-runs a scenario's earlier steps for every later one; here each step runs once.
/// </summary>
public class ScenarioGranularityFrameworkTests
{
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    private static Func<string, string?> UnderStryker { get; } =
        name => name == "STRYKER_MUTANT_FILE" ? "stryker-mutant-1.txt" : null;

    private sealed class Scenario
    {
        private readonly int[] _runs = new int[3];

        public Scenario(Exception? failInB = null)
        {
            Id = "sg-" + Guid.NewGuid().ToString("N");
            ScenarioRegistry.Register($"Raun.Mtp.Test.Granularity.{Id}", () => new ScenarioDefinition
            {
                ScenarioId = Id,
                DisplayName = "three steps",
                MethodName = $"Ns.Scenarios.{Id}",
                Nodes =
                [
                    Step(0, "a", [], null),
                    Step(1, "b", [0], failInB),
                    Step(2, "c", [1], null),
                ],
            });
        }

        public string Id { get; }

        public IReadOnlyList<int> Runs => _runs;

        private ScenarioNode Step(int index, string stepId, int[] dependsOn, Exception? fail) => new()
        {
            Index = index,
            StepId = stepId,
            Phase = "Given",
            OperationName = $"Op{index}",
            DisplayNameTemplate = stepId,
            DependsOn = dependsOn,
            Invoke = (_, _) =>
            {
                Interlocked.Increment(ref _runs[index]);
                return fail is null ? Task.FromResult<object?>(null) : Task.FromException<object?>(fail);
            },
        };
    }

    private static TestNodeUidListFilter Uids(params string[] uids)
        => new([.. uids.Select(u => new TestNodeUid(u))]);

    private static async Task<List<TestNode>> Discover(RaunTestFramework framework, ITestExecutionFilter? filter = null)
    {
        var uid = new SessionUid(Guid.NewGuid().ToString("N"));
        await framework.CreateTestSession(uid);
        var bus = new RecordingMessageBus();
        await framework.OnDiscover(uid, filter, bus, () => { }, CancellationToken.None);
        return bus.Nodes;
    }

    private static async Task<List<TestNode>> Run(RaunTestFramework framework, ITestExecutionFilter? filter = null)
    {
        var uid = new SessionUid(Guid.NewGuid().ToString("N"));
        await framework.CreateTestSession(uid);
        var bus = new RecordingMessageBus();
        await framework.OnExecute(uid, filter, bus, () => { }, CancellationToken.None);
        return bus.Nodes;
    }

    private static List<TestNode> About(Scenario scenario, IEnumerable<TestNode> nodes)
        => [.. nodes.Where(n => n.Uid.Value.StartsWith(scenario.Id, StringComparison.Ordinal))];

    private static bool Is<T>(TestNode node)
        where T : TestNodeStateProperty
        => node.Properties.OfType<T>().Length != 0;

    [Fact]
    public async Task Under_stryker_a_scenario_is_discovered_as_one_test()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(testGranularity: null, UnderStryker);

        var node = Assert.Single(About(scenario, await Discover(framework)));

        Assert.Equal(scenario.Id, node.Uid.Value);
        Assert.Equal("three steps", node.DisplayName);
    }

    [Fact]
    public async Task Without_stryker_discovery_stays_one_test_per_step()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(testGranularity: null, NoEnvironment);

        var uids = About(scenario, await Discover(framework)).Select(n => n.Uid.Value);

        Assert.Equal([$"{scenario.Id}:a", $"{scenario.Id}:b", $"{scenario.Id}:c"], uids);
    }

    [Fact]
    public async Task A_suite_that_asks_for_steps_keeps_them_even_under_stryker()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(TestGranularity.Step, UnderStryker);

        Assert.Equal(3, About(scenario, await Discover(framework)).Count);
    }

    [Fact]
    public async Task A_suite_can_ask_for_scenario_tests_without_stryker()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(TestGranularity.Scenario, NoEnvironment);

        Assert.Single(About(scenario, await Discover(framework)));
    }

    [Fact]
    public async Task Running_the_scenario_by_its_uid_runs_every_step_exactly_once()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(TestGranularity.Scenario, NoEnvironment);

        var nodes = About(scenario, await Run(framework, Uids(scenario.Id)));

        Assert.Equal([1, 1, 1], scenario.Runs);
        Assert.All(nodes, n => Assert.Equal(scenario.Id, n.Uid.Value));
        Assert.True(Is<InProgressTestNodeStateProperty>(nodes[0]));
        Assert.True(Is<PassedTestNodeStateProperty>(nodes[^1]));
        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public async Task What_discovery_names_is_what_a_run_accepts()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(testGranularity: null, UnderStryker);

        var discovered = Assert.Single(About(scenario, await Discover(framework)));
        await Run(framework, Uids(discovered.Uid.Value));

        Assert.Equal([1, 1, 1], scenario.Runs);
    }

    [Fact]
    public async Task A_uid_filter_narrows_discovery_to_the_named_scenario()
    {
        var wanted = new Scenario();
        var other = new Scenario();
        var framework = new RaunTestFramework(TestGranularity.Scenario, NoEnvironment);

        var nodes = await Discover(framework, Uids(wanted.Id));

        Assert.Single(About(wanted, nodes));
        Assert.Empty(About(other, nodes));
    }

    [Fact]
    public async Task A_step_uid_runs_nothing_when_scenarios_are_the_tests()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(TestGranularity.Scenario, NoEnvironment);

        var nodes = About(scenario, await Run(framework, Uids($"{scenario.Id}:b")));

        Assert.Equal([0, 0, 0], scenario.Runs);
        Assert.Empty(nodes);
    }

    [Fact]
    public async Task A_failing_step_fails_the_one_scenario_test()
    {
        var failure = new XunitException("expected 2, got 3");
        var scenario = new Scenario(failInB: failure);
        var framework = new RaunTestFramework(TestGranularity.Scenario, NoEnvironment);

        var nodes = About(scenario, await Run(framework, Uids(scenario.Id)));

        var failed = Assert.Single(nodes[^1].Properties.OfType<FailedTestNodeStateProperty>());
        Assert.Same(failure, failed.Exception);
        Assert.Equal([1, 1, 0], scenario.Runs);
    }

    [Fact]
    public async Task A_failed_preflight_skips_the_scenario_test_and_fails_its_own()
    {
        var scenario = new Scenario();
        var framework = new RaunTestFramework(
            TestGranularity.Scenario, NoEnvironment,
            preflight: _ => throw new InvalidOperationException("no database"));

        var all = await Run(framework, Uids(scenario.Id));

        Assert.Equal([0, 0, 0], scenario.Runs);
        Assert.True(Is<SkippedTestNodeStateProperty>(About(scenario, all)[^1]));
        var preflight = all.Last(n => n.Uid.Value == Raun.Running.Preflight.ScenarioId);
        Assert.True(Is<ErrorTestNodeStateProperty>(preflight));
    }

    [Fact]
    public async Task Preflight_is_discovered_as_one_test_too()
    {
        var framework = new RaunTestFramework(
            TestGranularity.Scenario, NoEnvironment, preflight: _ => Task.CompletedTask);

        var nodes = await Discover(framework);

        Assert.Single(nodes, n => n.Uid.Value == Raun.Running.Preflight.ScenarioId);
        Assert.DoesNotContain(nodes, n => n.Uid.Value.Contains(':', StringComparison.Ordinal));
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        private readonly List<TestNodeUpdateMessage> updates = [];

        public List<TestNode> Nodes
        {
            get { lock (updates) { return updates.Select(u => u.TestNode).ToList(); } }
        }

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            if (data is TestNodeUpdateMessage update)
            {
                lock (updates) { updates.Add(update); }
            }

            return Task.CompletedTask;
        }
    }
}
