using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;
using Xunit;
using Xunit.Sdk;

namespace Raun.Mtp.Test;

/// <summary>
/// A scenario reported as a single test: one node in progress when it starts, one terminal node when
/// it finishes, with the state folded from its steps. What a tool that runs every test on its own
/// (Stryker.NET) sees instead of one node per step.
/// </summary>
public class ScenarioMtpReportSinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static ScenarioNode Node(int index, string stepId, string template, bool synthetic = false) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        DependsOn = [],
        IsSynthetic = synthetic,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(string id = "scn", string display = "my scenario", params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = "Ns.Scenarios.Scn",
        Nodes = nodes.Length == 0 ? [Node(0, "a", "step a"), Node(1, "b", "step b"), Node(2, "c", "step c")] : nodes,
    };

    /// <summary>A result for step <paramref name="index"/>, starting <paramref name="index"/> seconds
    /// into the scenario and running for half a second.</summary>
    private static StepResult Result(
        ScenarioDefinition definition,
        int index,
        StepStatus status,
        Exception? exception = null,
        string? skipReason = null,
        string[]? logs = null) => new()
        {
            Node = definition.Nodes[index],
            DisplayName = definition.Nodes[index].DisplayNameTemplate,
            Status = status,
            StartedAt = Start + TimeSpan.FromSeconds(index),
            Duration = status is StepStatus.Passed or StepStatus.Failed ? TimeSpan.FromMilliseconds(500) : TimeSpan.Zero,
            Exception = exception,
            SkipReason = skipReason,
            Logs = logs ?? [],
        };

    private static string AttachmentRoot { get; } =
        Path.Combine(Path.GetTempPath(), "raun-attach-tests", Guid.NewGuid().ToString("N"));

    private static (ScenarioMtpReportSink Sink, RecordingMessageBus Bus) NewSink()
    {
        var bus = new RecordingMessageBus();
        return (new ScenarioMtpReportSink(new SessionUid("sess"), bus, new StubProducer(), AttachmentRoot), bus);
    }

    /// <summary>Runs a whole scenario through a fresh sink and returns its terminal node.</summary>
    private static async Task<TestNode> Finish(ScenarioDefinition definition, params StepResult[] results)
    {
        var (sink, bus) = NewSink();
        await sink.PublishAsync(new ScenarioStarted(definition));
        await sink.PublishAsync(new ScenarioFinished(definition, results));
        return bus.Nodes[^1];
    }

    private static T State<T>(TestNode node)
        where T : TestNodeStateProperty
        => Assert.IsType<T>(Assert.Single(node.Properties.OfType<TestNodeStateProperty>()));

    [Fact]
    public async Task A_scenario_is_announced_in_progress_under_its_own_uid_and_name()
    {
        var (sink, bus) = NewSink();
        var def = Definition();

        await sink.PublishAsync(new ScenarioStarted(def));

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("scn", node.Uid.Value);
        Assert.Equal("my scenario", node.DisplayName);
        State<InProgressTestNodeStateProperty>(node);
        Assert.Equal("Scenarios", Assert.Single(node.Properties.OfType<TestMethodIdentifierProperty>()).TypeName);
    }

    [Fact]
    public async Task Steps_publish_nothing_of_their_own()
    {
        // The whole point: a runner that lists tests must see the scenario and only the scenario.
        var (sink, bus) = NewSink();
        var def = Definition();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Result(def, 0, StepStatus.Passed)));
        await sink.PublishAsync(new StepFinished(def, Result(def, 1, StepStatus.Failed, new InvalidOperationException("x"))));

        Assert.Single(bus.Nodes);
    }

    [Fact]
    public async Task All_steps_passing_passes_the_scenario()
    {
        var def = Definition();

        var node = await Finish(def,
            Result(def, 0, StepStatus.Passed), Result(def, 1, StepStatus.Passed), Result(def, 2, StepStatus.Passed));

        Assert.Equal("scn", node.Uid.Value);
        State<PassedTestNodeStateProperty>(node);
    }

    [Fact]
    public async Task A_failed_assertion_in_one_step_fails_the_scenario_with_that_exception()
    {
        var def = Definition();
        var assertion = new XunitException("expected 2, got 3");

        var node = await Finish(def,
            Result(def, 0, StepStatus.Passed),
            Result(def, 1, StepStatus.Failed, assertion),
            Result(def, 2, StepStatus.Skipped, skipReason: "dependency failed: step b"));

        Assert.Same(assertion, State<FailedTestNodeStateProperty>(node).Exception);
    }

    [Fact]
    public async Task An_unexpected_exception_in_a_step_is_an_error_on_the_scenario()
    {
        var def = Definition();
        var crash = new InvalidOperationException("boom");

        var node = await Finish(def, Result(def, 0, StepStatus.Passed), Result(def, 1, StepStatus.Failed, crash));

        Assert.Same(crash, State<ErrorTestNodeStateProperty>(node).Exception);
    }

    [Fact]
    public async Task The_first_failing_step_decides_when_several_fail()
    {
        var def = Definition();
        var first = new XunitException("first");

        // Handed over out of order: the fold goes by when a step started, not by list position.
        var node = await Finish(def,
            Result(def, 2, StepStatus.Failed, new InvalidOperationException("second")),
            Result(def, 1, StepStatus.Failed, first),
            Result(def, 0, StepStatus.Passed));

        Assert.Same(first, State<FailedTestNodeStateProperty>(node).Exception);
    }

    [Fact]
    public async Task A_timeout_outranks_an_earlier_failure()
    {
        // A mutation tester treats "timed out" and "failed" differently, and a timeout says more
        // about the run than the assertion that happened to fail before it.
        var def = Definition();
        var timeout = new TimeoutException("too slow");

        var node = await Finish(def,
            Result(def, 0, StepStatus.Failed, new XunitException("x")),
            Result(def, 1, StepStatus.Failed, timeout));

        Assert.Same(timeout, State<TimeoutTestNodeStateProperty>(node).Exception);
    }

    [Fact]
    public async Task A_branch_that_was_not_taken_does_not_count_against_the_scenario()
    {
        var def = Definition();

        var node = await Finish(def,
            Result(def, 0, StepStatus.Passed),
            Result(def, 1, StepStatus.NotTaken, skipReason: "not taken: else"),
            Result(def, 2, StepStatus.Passed));

        State<PassedTestNodeStateProperty>(node);
    }

    [Fact]
    public async Task A_skipped_step_beside_passing_ones_leaves_the_scenario_passed()
    {
        var def = Definition();

        var node = await Finish(def,
            Result(def, 0, StepStatus.Passed), Result(def, 1, StepStatus.Skipped, skipReason: "not selected"));

        State<PassedTestNodeStateProperty>(node);
    }

    [Fact]
    public async Task A_scenario_in_which_nothing_ran_is_skipped_with_the_reason()
    {
        var def = Definition();

        var node = await Finish(def,
            Result(def, 0, StepStatus.Skipped, skipReason: "preflight failed"),
            Result(def, 1, StepStatus.Skipped, skipReason: "preflight failed"));

        Assert.Equal("preflight failed", State<SkippedTestNodeStateProperty>(node).Explanation);
    }

    [Fact]
    public async Task A_scenario_that_finishes_with_no_results_is_skipped_not_passed()
    {
        // "Passed" would let a mutant survive on a scenario that never ran.
        var def = Definition();

        var node = await Finish(def);

        State<SkippedTestNodeStateProperty>(node);
        Assert.Empty(node.Properties.OfType<TimingProperty>());
    }

    [Fact]
    public async Task Plumbing_results_do_not_take_part_in_the_fold()
    {
        var def = Definition(nodes: [Node(0, "m", "«merge»", synthetic: true)]);

        var node = await Finish(def, Result(def, 0, StepStatus.Passed));

        State<SkippedTestNodeStateProperty>(node);
    }

    [Fact]
    public async Task The_output_lists_every_step_with_its_outcome_and_its_own_lines()
    {
        var def = Definition();

        var node = await Finish(def,
            Result(def, 0, StepStatus.Passed, logs: ["created Jane"]),
            Result(def, 1, StepStatus.Failed, new XunitException("x")),
            Result(def, 2, StepStatus.Skipped, skipReason: "dependency failed: step b"));

        var output = Assert.Single(node.Properties.OfType<StandardOutputProperty>()).StandardOutput;
        Assert.Equal(
            [
                "1. step a [passed]",
                "  created Jane",
                "2. step b [failed]",
                "3. step c [skipped: dependency failed: step b]",
            ],
            output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Timing_spans_the_first_start_to_the_last_end()
    {
        var def = Definition();

        var node = await Finish(def, Result(def, 0, StepStatus.Passed), Result(def, 2, StepStatus.Passed));

        var timing = Assert.Single(node.Properties.OfType<TimingProperty>()).GlobalTiming;
        Assert.Equal(Start, timing.StartTime);
        Assert.Equal(Start + TimeSpan.FromSeconds(2.5), timing.EndTime);
        Assert.Equal(TimeSpan.FromSeconds(2.5), timing.Duration);
    }

    [Fact]
    public async Task Attachments_of_every_step_land_on_the_scenario_node()
    {
        var root = Path.Combine(Path.GetTempPath(), "raun-attach-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bus = new RecordingMessageBus();
            var sink = new ScenarioMtpReportSink(new SessionUid("sess"), bus, new StubProducer(), root);
            var def = Definition();

            StepResult WithAttachment(int index, string name, string value) => new()
            {
                Node = def.Nodes[index],
                DisplayName = def.Nodes[index].DisplayNameTemplate,
                Status = StepStatus.Passed,
                StartedAt = Start + TimeSpan.FromSeconds(index),
                Attachments = new Dictionary<string, string> { [name] = value },
            };

            await sink.PublishAsync(new ScenarioFinished(def,
                [WithAttachment(0, "request", "GET /a"), WithAttachment(1, "request", "GET /b")]));

            var artifacts = bus.Nodes[^1].Properties.OfType<FileArtifactProperty>();
            Assert.Equal(
                ["GET /a", "GET /b"],
                artifacts.Select(a => File.ReadAllText(a.FileInfo.FullName)).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Scenarios_running_side_by_side_each_get_one_start_and_one_finish()
    {
        var (sink, bus) = NewSink();
        var one = Definition(id: "one", display: "one");
        var two = Definition(id: "two", display: "two");

        await sink.PublishAsync(new ScenarioStarted(one));
        await sink.PublishAsync(new ScenarioStarted(two));
        await sink.PublishAsync(new ScenarioFinished(two, [Result(two, 0, StepStatus.Failed, new XunitException("x"))]));
        await sink.PublishAsync(new ScenarioFinished(one, [Result(one, 0, StepStatus.Passed)]));

        Assert.Equal(["one", "two", "two", "one"], bus.Nodes.Select(n => n.Uid.Value));
        State<FailedTestNodeStateProperty>(bus.Nodes[2]);
        State<PassedTestNodeStateProperty>(bus.Nodes[3]);
    }

    private sealed class StubProducer : IDataProducer
    {
        public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage)];
        public string Uid => "stub.producer";
        public string Version => "1.0.0";
        public string DisplayName => "stub";
        public string Description => "stub data producer for sink tests";
        public Task<bool> IsEnabledAsync() => Task.FromResult(true);
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
