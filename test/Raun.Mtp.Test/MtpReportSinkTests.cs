using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Phase 3 behavioral tests for the session-scoped MTP sink: a <see cref="MtpReportSink"/> implements
/// <see cref="IRunEventSink"/> and maps each step lifecycle event onto a Microsoft.Testing.Platform
/// <see cref="TestNodeUpdateMessage"/>. start -> <see cref="InProgressTestNodeStateProperty"/>;
/// <see cref="StepStatus.Passed"/> -> <see cref="PassedTestNodeStateProperty"/>;
/// <see cref="StepStatus.Failed"/> splits by exception kind into Failed/Timeout/Error;
/// <see cref="StepStatus.Skipped"/> -> <see cref="SkippedTestNodeStateProperty"/>. Finished updates
/// carry <see cref="TimingProperty"/>, the step's <see cref="TestFileLocationProperty"/>, and the
/// runtime-formatted display name; logs surface as standard output. The observer contract is async:
/// the sink awaits the platform's <see cref="IMessageBus.PublishAsync"/> directly rather than
/// blocking on it.
/// </summary>
public class MtpReportSinkTests
{
    private static readonly DateTimeOffset TestInstant = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    private static ScenarioNode Node(int index, string stepId, string template, string? file = null, int line = 0, string? group = null, bool synthetic = false) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        SourceFile = file,
        SourceLine = line,
        DependsOn = [],
        GroupId = group,
        IsSynthetic = synthetic,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(string id = "scn", string display = "my scenario", params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = "Ns.Scn",
        Nodes = nodes.Length == 0 ? [Node(0, "a", "step a")] : nodes,
    };

    /// <summary>Where the tests that do not care about attachments let them land.</summary>
    private static string AttachmentRoot { get; } =
        Path.Combine(Path.GetTempPath(), "raun-attach-tests", Guid.NewGuid().ToString("N"));

    private static (MtpReportSink Sink, RecordingMessageBus Bus) NewSink()
    {
        var bus = new RecordingMessageBus();
        return (new MtpReportSink(new SessionUid("sess"), bus, new StubProducer(), AttachmentRoot), bus);
    }

    [Fact]
    public async Task Attachments_are_written_under_the_run_s_own_results_directory()
    {
        // They used to land in the machine's temp folder and stay there for ever: invisible to the
        // user, never cleaned up, and an attachment carries whatever the step put in it.
        // The run's results directory is where a run's output belongs — visible, per run, and
        // already the user's to keep or delete.
        var root = Path.Combine(Path.GetTempPath(), "raun-attach-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bus = new RecordingMessageBus();
            var sink = new MtpReportSink(new SessionUid("sess"), bus, new StubProducer(), root);
            var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);

            await sink.PublishAsync(new ScenarioStarted(def));
            await sink.PublishAsync(new StepFinished(def, new StepResult
            {
                Node = def.Nodes[0],
                DisplayName = "step a",
                Status = StepStatus.Passed,
                StartedAt = DateTimeOffset.UnixEpoch,
                Attachments = new Dictionary<string, string> { ["reminder.txt"] = "hello" },
            }));

            var artifact = Assert.Single(bus.Nodes[^1].Properties.OfType<FileArtifactProperty>());
            Assert.StartsWith(root, artifact.FileInfo.FullName, StringComparison.Ordinal);
            Assert.Equal("hello", File.ReadAllText(artifact.FileInfo.FullName));
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
    public async Task An_attachment_root_that_stayed_empty_is_removed()
    {
        // Every session creates its own folder; a session that attached nothing must not leave one
        // behind, or a results directory fills up with empty folders one per run.
        var root = Path.Combine(Path.GetTempPath(), "raun-attach-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        RaunAttachments.CleanUp(root);

        Assert.False(Directory.Exists(root), "an empty attachment root was left behind");
    }

    [Fact]
    public async Task An_attachment_root_with_files_in_it_is_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "raun-attach-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "step"));
        await File.WriteAllTextAsync(Path.Combine(root, "step", "a.txt"), "x");
        try
        {
            RaunAttachments.CleanUp(root);

            Assert.True(Directory.Exists(root), "an attachment the run published was deleted");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Start_publishes_in_progress_update_for_the_step_node()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[0], DisplayName = "step a" }));

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("s:a", node.Uid.Value);
        Assert.NotEmpty(node.Properties.OfType<InProgressTestNodeStateProperty>());
    }

    [Fact]
    public async Task Published_node_carries_method_identity_for_grouping()
    {
        // Run updates must carry the same namespace/class/method identity discovery emits, so the
        // runner keeps the step nodes grouped under their scenario method rather than re-bucketing
        // them under "<Empty Namespace>" when results arrive.
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[0], DisplayName = "step a" }));

        var node = Assert.Single(bus.Nodes);
        Assert.NotEmpty(node.Properties.OfType<TestMethodIdentifierProperty>());
    }

    [Fact]
    public async Task Finished_node_carries_method_identity_for_grouping()
    {
        // The finish update is what the runner settles on when a step completes. If it lacked the
        // method identity, the node would collapse back under "<Empty Namespace>" the instant it
        // passed — grouped while running, nameless once done. So the terminal node must carry it too.
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Duration = TimeSpan.FromMilliseconds(1),
        }));

        var node = Assert.Single(bus.Nodes);
        Assert.NotEmpty(node.Properties.OfType<TestMethodIdentifierProperty>());
    }

    [Fact]
    public async Task Start_uses_numbered_runtime_formatted_display_name_without_prefix()
    {
        var def = Definition(id: "s", display: "patient booking", nodes: [Node(0, "a", "patient exists")]);
        var (sink, bus) = NewSink();

        // The scheduler computes the formatted name at run time (placeholders resolved); the sink
        // surfaces that, numbered and without the old scenario prefix.
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[0], DisplayName = "patient Jane exists" }));

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }

    [Fact]
    public async Task Passed_step_publishes_passed_state()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Duration = TimeSpan.FromMilliseconds(5),
        }));

        var node = Assert.Single(bus.Nodes);
        Assert.NotEmpty(node.Properties.OfType<PassedTestNodeStateProperty>());
    }

    [Fact]
    public async Task Failed_assertion_publishes_failed_state_carrying_the_exception()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        // A genuine xunit.v3.assert failure: its base type is Xunit.Sdk.XunitException, which the
        // sink must recognize as an assertion (Failed), not a generic Error.
        var ex = Assert.ThrowsAny<Exception>(() => Assert.Equal(1, 2));

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Failed,
            StartedAt = TestInstant,
            Exception = ex,
        }));

        var node = Assert.Single(bus.Nodes);
        var failed = Assert.Single(node.Properties.OfType<FailedTestNodeStateProperty>());
        Assert.Same(ex, failed.Exception);
        Assert.Empty(node.Properties.OfType<ErrorTestNodeStateProperty>());
        Assert.Empty(node.Properties.OfType<TimeoutTestNodeStateProperty>());
    }

    [Fact]
    public async Task Failed_timeout_publishes_timeout_state()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();
        var ex = new TimeoutException("step timed out");

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Failed,
            StartedAt = TestInstant,
            Exception = ex,
        }));

        var node = Assert.Single(bus.Nodes);
        var timeout = Assert.Single(node.Properties.OfType<TimeoutTestNodeStateProperty>());
        Assert.Same(ex, timeout.Exception);
        Assert.Empty(node.Properties.OfType<FailedTestNodeStateProperty>());
    }

    [Fact]
    public async Task Failed_other_exception_publishes_error_state()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();
        var ex = new InvalidOperationException("boom");

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Failed,
            StartedAt = TestInstant,
            Exception = ex,
        }));

        var node = Assert.Single(bus.Nodes);
        var error = Assert.Single(node.Properties.OfType<ErrorTestNodeStateProperty>());
        Assert.Same(ex, error.Exception);
        Assert.Empty(node.Properties.OfType<FailedTestNodeStateProperty>());
        Assert.Empty(node.Properties.OfType<TimeoutTestNodeStateProperty>());
    }

    [Fact]
    public async Task Skipped_step_publishes_skipped_state_with_reason()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Skipped,
            StartedAt = TestInstant,
            SkipReason = "dependency failed: creating an appointment",
        }));

        var node = Assert.Single(bus.Nodes);
        var skipped = Assert.Single(node.Properties.OfType<SkippedTestNodeStateProperty>());
        Assert.Equal("dependency failed: creating an appointment", skipped.Explanation);
    }

    [Fact]
    public async Task Finished_update_carries_timing_property()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[0], DisplayName = "step a" }));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Duration = TimeSpan.FromMilliseconds(250),
        }));

        var finished = bus.Nodes[^1];
        var timing = Assert.Single(finished.Properties.OfType<TimingProperty>());
        Assert.Equal(TimeSpan.FromMilliseconds(250), timing.GlobalTiming.Duration);
        // The window is StartedAt-anchored (design §3.B bonus): asserts the actual behavioral change
        // so that reverting to the old finish-anchored computation breaks this test.
        Assert.Equal(TestInstant, timing.GlobalTiming.StartTime);
    }

    [Fact]
    public async Task Finished_update_carries_file_location_when_source_is_known()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a", file: @"C:\src\B.cs", line: 12)]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
        }));

        var node = Assert.Single(bus.Nodes);
        var location = Assert.Single(node.Properties.OfType<TestFileLocationProperty>());
        Assert.Equal(@"C:\src\B.cs", location.FilePath);
        Assert.Equal(12, location.LineSpan.Start.Line);
    }

    [Fact]
    public async Task Finished_update_uses_the_numbered_results_formatted_display_name()
    {
        var def = Definition(id: "s", display: "booking", nodes: [Node(0, "a", "patient exists")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "patient Jane exists",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
        }));

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }

    [Fact]
    public async Task Group_member_step_is_numbered_with_sub_index()
    {
        var def = Definition(
            id: "s",
            nodes:
            [
                Node(0, "clean", "the database is clean"),
                Node(1, "p", "patient exists", group: "g1"),
                Node(2, "slot", "slot exists", group: "g1"),
            ]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[1], DisplayName = "patient Jane exists" }));

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("2.1 patient Jane exists", node.DisplayName);
    }

    [Fact]
    public async Task Logs_surface_as_standard_output_on_the_finished_update()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Logs = ["first line", "second line"],
        }));

        var node = Assert.Single(bus.Nodes);
        var output = Assert.Single(node.Properties.OfType<StandardOutputProperty>());
        Assert.Contains("first line", output.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("second line", output.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resource_events_appear_in_standard_output_in_log_order()
    {
        // The scheduler writes resource events into the step's log stream as they happen, so the
        // sink prints them where they occurred — between the lines before and after — not appended.
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Logs = ["looking up jane", "[resource] Create String:jane", "done"],
        }));

        var node = Assert.Single(bus.Nodes);
        var output = Assert.Single(node.Properties.OfType<StandardOutputProperty>()).StandardOutput;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(["looking up jane", "[resource] Create String:jane", "done"], lines);
    }

    [Fact]
    public async Task Log_entries_render_with_the_time_since_scenario_start()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Logs = ["booked", "[resource] Create String:jane"],
            LogEntries =
            [
                new LogEntry(TimeSpan.FromMilliseconds(500), "booked"),
                new LogEntry(TimeSpan.FromMilliseconds(1250), "[resource] Create String:jane"),
            ],
        }));

        var node = Assert.Single(bus.Nodes);
        var output = Assert.Single(node.Properties.OfType<StandardOutputProperty>()).StandardOutput;
        Assert.Contains("+0.500s booked", output, StringComparison.Ordinal);
        Assert.Contains("+1.250s [resource] Create String:jane", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_published_update_carries_the_session_uid()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var bus = new RecordingMessageBus();
        var sink = new MtpReportSink(new SessionUid("the-session"), bus, new StubProducer(), AttachmentRoot);

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepStarted(def, new StepContext { Node = def.Nodes[0], DisplayName = "step a" }));

        var update = Assert.Single(bus.Updates);
        Assert.Equal("the-session", update.SessionUid.Value);
    }

    [Fact]
    public async Task OnStepFinishedAsync_awaits_the_publish_instead_of_blocking()
    {
        // Proves the sink is genuinely async rather than sync-over-async: against a publish that
        // has not completed yet, the returned task must still be pending. A
        // `.GetAwaiter().GetResult()` implementation would block the calling thread at the call and
        // never return this task (hanging the test) — which is exactly the hazard this guards against.
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var gate = new TaskCompletionSource();
        var sink = new MtpReportSink(new SessionUid("sess"), new GatedMessageBus(gate.Task), new StubProducer(), AttachmentRoot);

        await sink.PublishAsync(new ScenarioStarted(def));
        var task = sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
        }));

        Assert.False(task.IsCompleted);
        gate.SetResult();
        await task;
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

        public IReadOnlyList<TestNodeUpdateMessage> Updates
        {
            get { lock (updates) { return [.. updates]; } }
        }

        public IReadOnlyList<TestNode> Nodes => Updates.Select(u => u.TestNode).ToList();

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

    // A bus whose publish completes only when the supplied gate task completes — lets a test observe
    // that the sink awaits the publish rather than blocking on it.
    private sealed class GatedMessageBus(Task gate) : IMessageBus
    {
        public Task PublishAsync(IDataProducer dataProducer, IData data) => gate;
    }

    [Fact]
    public async Task Not_taken_step_is_reported_as_skipped_with_its_reason()
    {
        // Honest tally: a branch that was not chosen did not run, and the runner should say so rather
        // than leave the node in its discovered state and drop it from the count.
        var def = Definition("s", "my scenario", Node(0, "a", "step a"));
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.NotTaken,
            StartedAt = TestInstant,
            SkipReason = "not taken: IsPriority",
        }));

        var node = Assert.Single(bus.Nodes);
        var skipped = Assert.Single(node.Properties.OfType<SkippedTestNodeStateProperty>());
        Assert.Equal("not taken: IsPriority", skipped.Explanation);
        Assert.Empty(node.Properties.OfType<PassedTestNodeStateProperty>());
    }

    [Fact]
    public async Task Synthetic_merge_nodes_publish_no_updates()
    {
        var merge = Node(0, "m", "«merge appt»", synthetic: true);
        var def = Definition("s", "my scenario", merge);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = merge,
            DisplayName = "«merge appt»",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
        }));

        Assert.Empty(bus.Updates);
    }
}
