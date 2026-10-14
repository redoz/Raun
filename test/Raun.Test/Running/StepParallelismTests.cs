using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Test;

public sealed class StepParallelismTests
{
    [Theory]
    [InlineData(0, 6)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    public async Task Step_limits_apply_independently_to_each_scenario(int limit, int expectedWave)
    {
        var wave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var completed = 0;
        var active = 0;
        var maximum = 0;
        async Task<object?> Invoke(IStepInputs inputs, ScenarioContext context)
        {
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximum); }
            while (current > previous && Interlocked.CompareExchange(ref maximum, current, previous) != previous);
            if (Interlocked.Increment(ref started) == expectedWave) wave.TrySetResult();
            try { await release.Task; }
            finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref completed); }
            return null;
        }
        ScenarioDefinition Definition(string id) => new()
        {
            ScenarioId = id, DisplayName = id, MethodName = "Limit." + id,
            Nodes = Enumerable.Range(0, 3).Select(i => new ScenarioNode
            {
                Index = i, StepId = id + i, Phase = "Given", OperationName = "Work", DisplayNameTemplate = "work",
                DependsOn = [], Invoke = Invoke,
            }).ToArray(),
        };
        var run = new RunLoop(() => [Definition("a"), Definition("b")], maxParallelScenarios: 2, maxParallelSteps: limit)
            .RunAsync(null, new Sink(), CancellationToken.None).AsTask();
        try
        {
            await wave.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(6, completed);
        Assert.Equal(expectedWave, maximum);
    }

    [Fact]
    public void Negative_step_limits_are_rejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new RunLoop(() => [], maxParallelSteps: -1));

    private sealed class Sink : IRunEventSink
    {
        public ValueTask PublishAsync(RunEvent evt) => ValueTask.CompletedTask;
    }
}
