using System.Diagnostics;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Raun emits one span per executed step (and per teardown) from <see cref="RaunTelemetry.Source"/>,
/// parented to whatever is ambient, with the step's identity as tags, its outcome as status, and its
/// log lines and resource events as span events. Nothing is emitted without a listener.
/// </summary>
// Both classes register a process-wide ActivityListener; sharing one collection keeps them from
// running at the same time, since a listener in one would make the other's spans sampled.
[Collection("ActivityListeners")]
public class TracingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static ScenarioNode Node(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[]? dependsOn = null,
        bool teardown = false) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = teardown ? "Then" : "When",
        OperationName = teardown ? "Teardown" : $"Op{index}",
        DisplayNameTemplate = teardown ? "Teardown" : $"op {index}",
        SourceFile = @"C:\src\Scenarios.cs",
        SourceLine = 10 + index,
        DependsOn = dependsOn ?? [],
        IsTeardown = teardown,
        Invoke = invoke,
    };

    // Each test gets its own scenario id so a listener (which is process-global) can pick out its
    // own spans even when other test classes run concurrently.
    private static ScenarioDefinition Def(string scenarioId, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = scenarioId,
        DisplayName = "tracing scenario " + scenarioId,
        MethodName = "Ns.Tracing." + scenarioId,
        Nodes = nodes,
    };

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous));
        Assert.True(done == task, "operation did not complete within the test timeout");
        return await task;
    }

    /// <summary>Captures every Raun span tagged with the scenario id it was built for.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly List<Activity> _all = [];
        private readonly ActivityListener _listener;
        private readonly string _scenarioId;

        public Capture(string scenarioId)
        {
            _scenarioId = scenarioId;
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RaunTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_all)
                    {
                        _all.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> Spans
        {
            get
            {
                lock (_all)
                {
                    return _all
                        .Where(a => (string?)a.GetTagItem(RaunTelemetry.Attributes.Scenario) == _scenarioId)
                        .OrderBy(a => a.StartTimeUtc)
                        .ToList();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Each_executed_step_gets_a_span_with_its_identity_tags()
    {
        var id = NewId();
        using var capture = new Capture(id);
        var def = Def(id,
            Node(0, (_, _) => Task.FromResult<object?>(null)),
            Node(1, (_, _) => Task.FromResult<object?>(null), [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(2, capture.Spans.Count);
        var first = capture.Spans[0];
        Assert.Equal("op 0", first.DisplayName);
        Assert.Equal("tracing scenario " + id, first.GetTagItem(RaunTelemetry.Attributes.TestSuiteName));
        Assert.Equal("op 0", first.GetTagItem(RaunTelemetry.Attributes.TestCaseName));
        Assert.Equal("step-0", first.GetTagItem(RaunTelemetry.Attributes.Step));
        Assert.Equal("When", first.GetTagItem(RaunTelemetry.Attributes.StepPhase));
        Assert.Equal("Op0", first.GetTagItem(RaunTelemetry.Attributes.StepOperation));
        Assert.Equal(@"C:\src\Scenarios.cs", first.GetTagItem(RaunTelemetry.Attributes.CodeFilePath));
        Assert.Equal(10, first.GetTagItem(RaunTelemetry.Attributes.CodeLineNumber));
        Assert.Equal("pass", first.GetTagItem(RaunTelemetry.Attributes.TestCaseResultStatus));

        // The result carries the ids so a report can point at the trace.
        Assert.Equal(first.TraceId.ToString(), results[0].TraceId);
        Assert.Equal(first.SpanId.ToString(), results[0].SpanId);
    }

    [Fact]
    public async Task Step_spans_parent_to_the_ambient_activity()
    {
        var id = NewId();
        using var capture = new Capture(id);
        using var ambientSource = new ActivitySource("TracingTests.Ambient");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TracingTests.Ambient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(ambientListener);

        var def = Def(id,
            Node(0, (_, _) => Task.FromResult<object?>(null)),
            Node(1, (_, _) => Task.FromResult<object?>(null)));

        using (var scenario = ambientSource.StartActivity("scenario"))
        {
            Assert.NotNull(scenario);
            await WithTimeout(new ScenarioScheduler().RunAsync(def));

            Assert.All(capture.Spans, span =>
            {
                Assert.Equal(scenario.SpanId, span.ParentSpanId);
                Assert.Equal(scenario.TraceId, span.TraceId);
            });
        }
    }

    [Fact]
    public async Task A_failing_step_marks_its_span_as_an_error_with_the_exception()
    {
        var id = NewId();
        using var capture = new Capture(id);
        var def = Def(id, Node(0, (_, _) => throw new InvalidOperationException("boom")));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        var span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("fail", span.GetTagItem(RaunTelemetry.Attributes.TestCaseResultStatus));
        var exception = Assert.Single(span.Events, e => e.Name == "exception");
        Assert.Contains(exception.Tags, t => t.Key == "exception.message" && (string?)t.Value == "boom");
    }

    [Fact]
    public async Task Log_lines_and_resource_effects_become_span_events_in_order()
    {
        var id = NewId();
        using var capture = new Capture(id);
        var def = Def(id, Node(0, async (_, ctx) =>
        {
            ctx.Log("before");
            await ctx.Resources.Create(new Resources.User("jane@x"));
            ctx.Log("after");
            return null;
        }));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        var span = Assert.Single(capture.Spans);
        var events = span.Events.ToList();
        Assert.Equal([RaunTelemetry.Events.Log, RaunTelemetry.Events.Resource, RaunTelemetry.Events.Log], events.Select(e => e.Name));
        Assert.Contains(events[0].Tags, t => t.Key == "message" && (string?)t.Value == "before");
        Assert.Contains(events[1].Tags, t => t.Key == "verb" && (string?)t.Value == "Create");
        Assert.Contains(events[1].Tags, t => t.Key == "identity" && (string?)t.Value == "User:jane@x");
        Assert.Contains(events[2].Tags, t => t.Key == "message" && (string?)t.Value == "after");
    }

    [Fact]
    public async Task Skipped_steps_get_no_span_but_an_event_on_the_ambient_scenario_span()
    {
        var id = NewId();
        using var capture = new Capture(id);
        using var ambientSource = new ActivitySource("TracingTests.Ambient2");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TracingTests.Ambient2",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(ambientListener);

        var def = Def(id,
            Node(0, (_, _) => throw new InvalidOperationException("boom")),
            Node(1, (_, _) => Task.FromResult<object?>(null), [0]));

        using var scenario = ambientSource.StartActivity("scenario");
        Assert.NotNull(scenario);
        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Single(capture.Spans); // only the failed step ran
        var skipped = Assert.Single(scenario.Events, e => e.Name == RaunTelemetry.Events.StepSkipped);
        Assert.Contains(skipped.Tags, t => t.Key == "step" && (string?)t.Value == "op 1");
        Assert.Contains(skipped.Tags, t => t.Key == "status" && (string?)t.Value == "Skipped");
    }

    [Fact]
    public async Task Teardown_gets_its_own_span_and_cleanups_log_onto_it()
    {
        var id = NewId();
        using var capture = new Capture(id);
        var def = Def(id,
            Node(0, (_, ctx) =>
            {
                ctx.OnTeardown(t => { t.Log("released"); return Task.CompletedTask; });
                return Task.FromResult<object?>(null);
            }),
            Node(1, (_, _) => Task.FromResult<object?>(null), teardown: true));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        var teardown = Assert.Single(capture.Spans, s => s.DisplayName == "Teardown");
        Assert.Equal("pass", teardown.GetTagItem(RaunTelemetry.Attributes.TestCaseResultStatus));
        Assert.Contains(teardown.Events, e => e.Name == RaunTelemetry.Events.Log
            && e.Tags.Any(t => t.Key == "message" && (string?)t.Value == "released"));
        Assert.Equal(teardown.TraceId.ToString(), results[1].TraceId);
    }

    [Fact]
    public async Task Without_a_listener_nothing_is_recorded_and_results_carry_no_trace_ids()
    {
        // No Capture here. Tests in this class run one at a time and each disposes its listener, and
        // no other class in this project subscribes to the Raun source, so nothing is listening now.
        var def = Def(NewId(), Node(0, (_, ctx) => { ctx.Log("x"); return Task.FromResult<object?>(null); }));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Null(results[0].TraceId);
        Assert.Null(results[0].SpanId);
        Assert.Equal(["x"], results[0].Logs); // logging works exactly the same without a span
    }
}
