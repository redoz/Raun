using System.Collections;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>Cancellation arrives from another thread, at any point of the scheduler's pass.</summary>
public class SchedulerCancellationRaceTests
{
    [Fact]
    public async Task Cancellation_between_judging_a_step_ready_and_launching_it_skips_the_step()
    {
        // The scheduler first decides what each waiting step is (to be skipped, or ready), then
        // launches the ready ones unless the scenario is cancelled. Cancellation landing between the
        // two leaves a ready step that is neither skipped nor launched, with nothing running. That
        // is a cancelled scenario, not a defect in its graph.
        using var host = new CancellationTokenSource();
        var dependencies = new CancelWhenRead(host, [0]);
        var definition = new ScenarioDefinition
        {
            ScenarioId = "race", DisplayName = "race", MethodName = "race", Namespace = "", TypeName = "",
            Nodes =
            [
                new ScenarioNode
                {
                    Index = 0, StepId = "first", Phase = "Given", OperationName = "First",
                    DisplayNameTemplate = "first", DependsOn = [],
                    Invoke = (_, _) => Task.FromResult<object?>(null),
                },
                new ScenarioNode
                {
                    Index = 1, StepId = "second", Phase = "When", OperationName = "Second",
                    DisplayNameTemplate = "second", DependsOn = dependencies,
                    Invoke = (_, _) => Task.FromResult<object?>(null),
                },
            ],
        };

        var results = await new ScenarioScheduler()
            .RunAsync(definition, observer: new ArmWhenFirstFinishes(dependencies), cancellationToken: host.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(ScenarioScheduler.CanceledSkipReason, results[1].SkipReason);
    }

    /// <summary>The second step's dependencies: once armed, the next read of them cancels the host.
    /// The scheduler reads them while judging the step, after it has checked for cancellation.</summary>
    private sealed class CancelWhenRead(CancellationTokenSource host, int[] items) : IReadOnlyList<int>
    {
        private int armed;

        public void Arm() => Volatile.Write(ref armed, 1);

        public int Count => items.Length;

        public int this[int index] => items[index];

        public IEnumerator<int> GetEnumerator()
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                host.Cancel();
            }

            return ((IEnumerable<int>)items).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ArmWhenFirstFinishes(CancelWhenRead dependencies) : IStepObserver
    {
        public Task OnStepStartingAsync(StepContext context) => Task.CompletedTask;

        public Task OnStepFinishedAsync(StepResult result)
        {
            if (result.Node.Index == 0)
            {
                dependencies.Arm();
            }

            return Task.CompletedTask;
        }
    }
}
