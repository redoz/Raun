using Raun.Model;
using Raun.Scheduling;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Raun.Test;

// CA1848/CA1873 push callers toward cached LoggerMessage delegates for hot paths. These tests call
// the extension methods deliberately, because that is what user code and a system under test will
// do — the point is to prove those calls reach the right step.
#pragma warning disable CA1848, CA1873

/// <summary>
/// Bridging <see cref="ILogger"/> onto the running step. The ambient <see cref="ScenarioContext"/>
/// is what makes this work for a logger captured once and used many times — a service resolved at
/// SUT startup logs against whichever step is executing, not the step that built it.
/// </summary>
public class ScenarioLoggingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

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
        MethodName = "Ns.Scn", Namespace = "", TypeName = "Ns",
        Nodes = nodes,
    };

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Generous));
        Assert.True(done == task, "operation did not complete within the test timeout");
        return await task;
    }

    [Fact]
    public async Task Current_context_is_the_running_step()
    {
        string? seen = null;
        var def = Def(Node(0, (_, _) =>
        {
            seen = ScenarioContext.Current?.StepId;
            return Task.FromResult<object?>(null);
        }));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal("step-0", seen);
    }

    [Fact]
    public async Task Current_context_is_cleared_after_the_run()
    {
        var def = Def(Node(0, (_, _) => Task.FromResult<object?>(null)));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Null(ScenarioContext.Current);
    }

    [Fact]
    public async Task Concurrent_steps_each_see_their_own_context()
    {
        // Both steps are roots, so they run concurrently. AsyncLocal must not leak across them.
        var entered = new TaskCompletionSource();
        var released = new TaskCompletionSource();
        string? first = null;
        string? second = null;

        var def = Def(
            Node(0, async (_, _) =>
            {
                entered.TrySetResult();
                await released.Task;
                first = ScenarioContext.Current?.StepId;
                return null;
            }),
            Node(1, async (_, _) =>
            {
                await entered.Task;
                second = ScenarioContext.Current?.StepId;
                released.TrySetResult();
                return null;
            }));

        await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Equal("step-0", first);
        Assert.Equal("step-1", second);
    }

    [Fact]
    public async Task Logger_writes_land_on_the_step_that_was_running()
    {
        var def = Def(Node(0, (_, ctx) =>
        {
            ctx.GetLogger<ScenarioLoggingTests>().LogInformation("hello {Name}", "world");
            return Task.FromResult<object?>(null);
        }));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        var line = Assert.Single(results[0].Logs);
        Assert.Contains("hello world", line, StringComparison.Ordinal);
        Assert.Contains("Information", line, StringComparison.Ordinal);
        Assert.Contains(nameof(ScenarioLoggingTests), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_logger_captured_once_follows_the_current_step()
    {
        // The case that motivates the ambient context: a service resolves its logger at construction
        // and writes from several different steps. Each write belongs to the step that provoked it.
        var provider = new RaunLoggerProvider();
        var logger = provider.CreateLogger("Shared");

        var def = Def(
            Node(0, (_, _) => { logger.LogInformation("from first"); return Task.FromResult<object?>(null); }),
            Node(1, (_, _) => { logger.LogInformation("from second"); return Task.FromResult<object?>(null); }, [0]));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        Assert.Contains("from first", Assert.Single(results[0].Logs), StringComparison.Ordinal);
        Assert.Contains("from second", Assert.Single(results[1].Logs), StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_with_no_current_step_is_dropped_without_throwing()
    {
        var logger = new RaunLoggerProvider().CreateLogger("Orphan");

        logger.LogWarning("nobody is running");   // must not throw
    }

    [Fact]
    public async Task Exceptions_are_included_in_the_logged_line()
    {
        var def = Def(Node(0, (_, ctx) =>
        {
            ctx.GetLogger("Cat").LogError(new InvalidOperationException("boom"), "it failed");
            return Task.FromResult<object?>(null);
        }));

        var results = await WithTimeout(new ScenarioScheduler().RunAsync(def));

        var line = Assert.Single(results[0].Logs);
        Assert.Contains("it failed", line, StringComparison.Ordinal);
        Assert.Contains("boom", line, StringComparison.Ordinal);
    }
}
