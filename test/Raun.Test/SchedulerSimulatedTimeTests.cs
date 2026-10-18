using Raun;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The scheduler's opt-in simulated-time mode drives realistic report timings from inside step
/// bodies (via <see cref="ScenarioContext.SimulateElapsed"/>) without any real waiting. Because a
/// single shared clock cannot represent a DAG (concurrent siblings would corrupt one another and a
/// join would start at the SUM of its parallel deps rather than the MAX), each step gets its own
/// <see cref="SimulatedClock"/> and the scheduler computes start offsets from dependency finishes.
/// </summary>
public class SchedulerSimulatedTimeTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Base = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    private static ScenarioNode Node(
        int index,
        Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[]? dependsOn = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"op {index}",
        DependsOn = dependsOn ?? [],
        Invoke = invoke,
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    // A fixed base clock: GetUtcNow always returns Base (the scheduler captures it once for sim mode).
    private static ScenarioScheduler SimScheduler()
        => new(simulatedTime: true, timeProvider: new SimulatedClock(Base));

    private static Func<IStepInputs, ScenarioContext, Task<object?>> Elapse(TimeSpan delta)
        => (_, ctx) => { ctx.SimulateElapsed(delta); return Task.FromResult<object?>(null); };

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous));
        Assert.True(done == task, "operation did not complete within the test timeout");
        return await task;
    }

    [Fact]
    public async Task Linear_chain_offsets_are_cumulative_and_durations_match_declared()
    {
        var d0 = TimeSpan.FromMilliseconds(30);
        var d1 = TimeSpan.FromMilliseconds(20);
        var d2 = TimeSpan.FromMilliseconds(50);

        var def = Def(
            Node(0, Elapse(d0)),
            Node(1, Elapse(d1), [0]),
            Node(2, Elapse(d2), [1]));

        var results = await WithTimeout(SimScheduler().RunAsync(def));

        // Each step's measured duration equals exactly the amount it declared.
        Assert.Equal(d0, results[0].Duration);
        Assert.Equal(d1, results[1].Duration);
        Assert.Equal(d2, results[2].Duration);

        // Start offsets are the cumulative declared durations of the upstream chain.
        Assert.Equal(Base, results[0].StartedAt);
        Assert.Equal(Base + d0, results[1].StartedAt);
        Assert.Equal(Base + d0 + d1, results[2].StartedAt);
    }

    [Fact]
    public async Task Parallel_siblings_overlap_and_their_join_starts_at_the_max_not_the_sum()
    {
        var slow = TimeSpan.FromMilliseconds(70);
        var fast = TimeSpan.FromMilliseconds(50);
        var joinWork = TimeSpan.FromMilliseconds(10);

        var def = Def(
            Node(0, Elapse(TimeSpan.Zero)),
            Node(1, Elapse(slow), [0]),   // 70ms sibling
            Node(2, Elapse(fast), [0]),   // 50ms sibling
            Node(3, Elapse(joinWork), [1, 2]));

        var results = await WithTimeout(SimScheduler().RunAsync(def));

        // Both siblings share the same start offset (the root finished at offset 0) and overlap.
        Assert.Equal(Base, results[1].StartedAt);
        Assert.Equal(Base, results[2].StartedAt);
        Assert.Equal(slow, results[1].Duration);
        Assert.Equal(fast, results[2].Duration);

        // Their intervals genuinely overlap on the one shared timeline.
        var s1 = results[1].StartedAt; var e1 = s1 + results[1].Duration;
        var s2 = results[2].StartedAt; var e2 = s2 + results[2].Duration;
        Assert.True(s1 < e2 && s2 < e1, "parallel siblings must overlap");

        // The join starts at MAX(dep finishes) = 70ms, NOT the sum (120ms).
        Assert.Equal(Base + slow, results[3].StartedAt);
        Assert.NotEqual(Base + slow + fast, results[3].StartedAt);
    }

    [Fact]
    public async Task Resource_effects_carry_timestamps_on_the_same_sim_timeline()
    {
        var rootWork = TimeSpan.FromMilliseconds(100);
        var beforeEffect = TimeSpan.FromMilliseconds(40);

        var def = Def(
            Node(0, Elapse(rootWork)),
            Node(1, async (_, ctx) =>
            {
                ctx.SimulateElapsed(beforeEffect);
                await ctx.Resources.Read(new Widget("k"));
                return null;
            }, [0]));

        var results = await WithTimeout(SimScheduler().RunAsync(def));

        // Step 1 starts after the root's 100ms; its effect is stamped 40ms further along.
        Assert.Equal(Base + rootWork, results[1].StartedAt);
        var effect = Assert.Single(results[1].Effects);
        Assert.Equal(Base + rootWork + beforeEffect, effect.Timestamp);
    }

    [Fact]
    public async Task Real_mode_ignores_SimulateElapsed_and_stamps_from_the_injected_provider()
    {
        // simulatedTime:false (the default) — SimulateElapsed is an inert no-op and timing is real.
        var clock = new TestTimeProvider(Base);
        var def = Def(Node(0, Elapse(TimeSpan.FromSeconds(5))));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(Base, results[0].StartedAt);
        Assert.True(results[0].Duration < TimeSpan.FromSeconds(1),
            "real mode must not absorb the simulated 5s");
    }

    private sealed record Widget(string Name);
}
