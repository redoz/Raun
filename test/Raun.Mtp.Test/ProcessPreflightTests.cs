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
    {
        var sink = new Recorder();
        await new RunLoop(() => [Scenario], preflight: preflight.RunAsync)
            .RunAsync(selector: null, sink, CancellationToken.None);
        return sink;
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
}
