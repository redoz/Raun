using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// One test host can serve many sessions (an IDE in server mode, Stryker.NET reusing a host for every
/// mutant). Each session is a separate <see cref="RunLoop"/>, sharing one <see cref="ProcessPreflight"/>.
/// </summary>
public class ProcessPreflightTests
{
    private static readonly ScenarioDefinition Scenario = new()
    {
        ScenarioId = "a",
        DisplayName = "a",
        MethodName = "Ns.a",
        Nodes =
        [
            new ScenarioNode
            {
                Index = 0,
                StepId = "step",
                Phase = "Given",
                OperationName = "Op",
                DisplayNameTemplate = "op",
                DependsOn = [],
                Invoke = (_, _) => Task.FromResult<object?>(null),
            },
        ],
    };

    private sealed class Recorder : IRunEventSink
    {
        public List<StepFinished> Finished { get; } = [];

        public ValueTask PublishAsync(RunEvent evt)
        {
            if (evt is StepFinished finished)
            {
                lock (Finished) { Finished.Add(finished); }
            }

            return default;
        }

        public StepStatus StatusOf(string stepId) => Assert.Single(Finished, e => e.Result.Node.StepId == stepId).Result.Status;
    }

    private static async Task<Recorder> Session(ProcessPreflight preflight)
        => await Session(preflight.RunAsync);

    private static async Task<Recorder> Session(Func<ScenarioContext, Task>? preflight)
    {
        var sink = new Recorder();
        await new RunLoop(() => [Scenario], preflight: preflight)
            .RunAsync(selector: null, sink, CancellationToken.None);
        return sink;
    }

    [Fact]
    public async Task The_session_preflight_runs_in_every_session_after_the_shared_start()
    {
        var calls = new List<string>();
        var preflight = ProcessPreflight.Compose(
            _ => { calls.Add("start"); return Task.CompletedTask; },
            _ => { calls.Add("reset"); return Task.CompletedTask; });

        await Session(preflight);
        var second = await Session(preflight);

        Assert.Equal(["start", "reset", "reset"], calls);
        Assert.Equal(StepStatus.Passed, second.StatusOf("step"));
    }

    [Fact]
    public async Task A_failed_session_preflight_skips_only_its_own_session()
    {
        var starts = 0;
        var resets = 0;
        var preflight = ProcessPreflight.Compose(
            _ => { starts++; return Task.CompletedTask; },
            _ => ++resets == 1
                ? Task.FromException(new InvalidOperationException("database locked"))
                : Task.CompletedTask);

        var first = await Session(preflight);
        var second = await Session(preflight);

        Assert.Equal(1, starts);
        Assert.Equal(StepStatus.Failed, first.StatusOf("preflight"));
        Assert.Equal(StepStatus.Skipped, first.StatusOf("step"));
        Assert.Equal(StepStatus.Passed, second.StatusOf("step"));
    }

    [Fact]
    public async Task The_session_preflight_does_not_run_when_the_start_fails()
    {
        var resets = 0;
        var preflight = ProcessPreflight.Compose(
            _ => Task.FromException(new InvalidOperationException("container runtime not running")),
            _ => { resets++; return Task.CompletedTask; });

        var session = await Session(preflight);

        Assert.Equal(0, resets);
        Assert.Equal(StepStatus.Skipped, session.StatusOf("step"));
    }

    [Fact]
    public async Task A_session_preflight_alone_gets_its_own_preflight_node()
    {
        var resets = 0;
        var preflight = ProcessPreflight.Compose(null, _ => { resets++; return Task.CompletedTask; });

        var session = await Session(preflight);

        Assert.Equal(1, resets);
        Assert.Equal(StepStatus.Passed, session.StatusOf("preflight"));
    }

    [Fact]
    public async Task A_successful_preflight_runs_once_for_every_session_of_the_host()
    {
        var runs = 0;
        var preflight = new ProcessPreflight(_ => { Interlocked.Increment(ref runs); return Task.CompletedTask; });

        var first = await Session(preflight);
        var second = await Session(preflight);

        Assert.Equal(1, runs);
        Assert.Equal(StepStatus.Passed, second.StatusOf("preflight"));
        Assert.Equal(StepStatus.Passed, second.StatusOf("step"));
        Assert.Equal(StepStatus.Passed, first.StatusOf("step"));
    }

    [Fact]
    public async Task Concurrent_sessions_wait_for_the_same_start()
    {
        var runs = 0;
        var release = new TaskCompletionSource();
        var preflight = new ProcessPreflight(async _ => { Interlocked.Increment(ref runs); await release.Task; });

        var first = Session(preflight);
        var second = Session(preflight);
        release.SetResult();
        var sessions = await Task.WhenAll(first, second);

        Assert.Equal(1, runs);
        Assert.All(sessions, s => Assert.Equal(StepStatus.Passed, s.StatusOf("step")));
    }

    [Fact]
    public async Task A_failed_preflight_is_retried_by_the_next_session()
    {
        var runs = 0;
        var preflight = new ProcessPreflight(_ => ++runs == 1
            ? Task.FromException(new InvalidOperationException("container runtime not running"))
            : Task.CompletedTask);

        var first = await Session(preflight);
        var second = await Session(preflight);

        Assert.Equal(2, runs);
        Assert.Equal(StepStatus.Skipped, first.StatusOf("step"));
        Assert.Equal(StepStatus.Passed, second.StatusOf("step"));
    }

    [Fact]
    public async Task A_preflight_that_throws_before_returning_a_task_is_retried_by_the_next_session()
    {
        // Not async: the exception leaves the delegate itself, there is no task to carry it.
        var runs = 0;
        var preflight = new ProcessPreflight(_ => ++runs == 1
            ? throw new InvalidOperationException("container runtime not running")
            : Task.CompletedTask);

        var first = await Session(preflight);
        var second = await Session(preflight);

        Assert.Equal(2, runs);
        Assert.Equal(StepStatus.Failed, first.StatusOf("preflight"));
        Assert.Equal(StepStatus.Skipped, first.StatusOf("step"));
        Assert.Equal(StepStatus.Passed, second.StatusOf("preflight"));
        Assert.Equal(StepStatus.Passed, second.StatusOf("step"));
    }
}
