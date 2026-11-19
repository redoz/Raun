using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Test;

/// <summary>What the run loop promises the sink it is handed, whatever that sink is.</summary>
public class RunLoopSinkTests
{
    [Fact]
    public async Task A_sink_handed_straight_to_the_run_loop_sees_one_event_at_a_time()
    {
        // Four scenarios finish their step at the same moment. A sink is told it needs no locking
        // of its own; that has to hold for one given to the run loop directly, not only for those
        // behind a RunEventBus.
        const int Scenarios = 4;
        var entered = 0;
        var together = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ScenarioDefinition Definition(int n) => new()
        {
            ScenarioId = "s" + n, DisplayName = "s" + n, MethodName = "s" + n, Namespace = "", TypeName = "",
            Nodes =
            [
                new ScenarioNode
                {
                    Index = 0, StepId = "s" + n + "-body", Phase = "Given", OperationName = "Body",
                    DisplayNameTemplate = "body", DependsOn = [],
                    Invoke = async (_, _) =>
                    {
                        if (Interlocked.Increment(ref entered) == Scenarios) together.TrySetResult();
                        await together.Task;
                        return null;
                    },
                },
            ],
        };

        var sink = new OneAtATime();
        await new RunLoop(() => Enumerable.Range(0, Scenarios).Select(Definition), maxParallelScenarios: Scenarios)
            .RunAsync(null, sink, TestContext.Current.CancellationToken);

        Assert.False(sink.Overlapped, "two events were inside the sink at once");
        Assert.Equal(Scenarios, sink.Finished);
    }

    private sealed class OneAtATime : IRunEventSink
    {
        private int inside;
        private volatile bool overlapped;

        public bool Overlapped => overlapped;

        public int Finished { get; private set; }

        public async ValueTask PublishAsync(RunEvent evt)
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            await Task.Delay(20); // long enough for a second caller to arrive if nothing holds it back
            if (evt is ScenarioFinished) Finished++;
            Interlocked.Decrement(ref inside);
        }
    }
}
