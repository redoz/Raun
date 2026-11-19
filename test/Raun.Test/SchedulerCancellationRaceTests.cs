using System.Collections;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

public class SchedulerCancellationRaceTests
{
    [Fact]
    public async Task Cancellation_that_lands_in_the_middle_of_a_scheduling_pass_skips_the_steps_still_waiting()
    {
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

    /// <summary>The second step's dependencies. The scheduler reads them in the middle of a pass;
    /// once armed, the next read cancels the host.</summary>
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
