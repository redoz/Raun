using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Test;

public class RunLoopSinkTests
{
    [Fact]
    public async Task A_sink_handed_straight_to_the_run_loop_sees_one_event_at_a_time()
    {
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
        private readonly TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int inside;
        private bool held;

        public bool Overlapped => second.Task.IsCompleted;

        public int Finished { get; private set; }

        public async ValueTask PublishAsync(RunEvent evt)
        {
            if (Interlocked.Increment(ref inside) > 1) second.TrySetResult();

            // The four steps finish together. The first of those events stays inside until a second
            // caller comes in beside it. If none can, only time can say so: Turns' own tests say it
            // without a clock; this one says the run loop uses it.
            if (evt is StepFinished && !held)
            {
                held = true;
                await Task.WhenAny(second.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
            }

            if (evt is ScenarioFinished) Finished++;
            Interlocked.Decrement(ref inside);
        }
    }
}
