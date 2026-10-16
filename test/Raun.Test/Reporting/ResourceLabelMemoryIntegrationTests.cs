using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test.Reporting;

public class ResourceLabelMemoryIntegrationTests
{
    private sealed record Patient(string Id, string Name) : IResource<Patient>
    {
        public static ResourceKey KeyFor(Patient p) => p.Id;
        public static string? LabelFor(Patient p) => p.Name;
    }
    private static readonly ResourceIdentity Key = new(typeof(Patient), "42");
    private static ScenarioNode Node(int index, Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        int[]? dependencies = null, bool teardown = false, bool setup = false,
        Func<StepMessage, object?, MessageFormattingContext, StepMessage>? completed = null) => new()
    {
        Index = index, StepId = $"s{index}", Phase = "Given", OperationName = "Test",
        DisplayNameTemplate = "test", DependsOn = dependencies ?? [], IsTeardown = teardown, IsSetup = setup,
        Invoke = invoke, FormatTitleMessage = (_, f) => StepMessage.Create(f, $"Use {Key}"),
        FormatCompletedTitleMessage = completed,
        FormatNoteMessages = [(_, f) => StepMessage.Create(f, $"Note {Key}")],
    };
    private static ScenarioDefinition Definition(params ScenarioNode[] nodes) => new()
    { ScenarioId = "test", DisplayName = "test", MethodName = "Test", Nodes = nodes };

    [Fact]
    public async Task Setup_titles_notes_logs_effects_and_cleanup_share_the_same_execution_memory()
    {
        var definition = Definition(
            Node(0, async (_, ctx) => { await ctx.Resources.Load(new Patient("42", "Jane")); return null; }, setup: true),
            Node(1, async (_, ctx) =>
            {
                ctx.Log($"Log {Key}");
                Assert.Equal("Argument Jane", StepMessage.Create(ctx.ArgumentTitleFormatting, $"Argument {Key}").Text);
                await ctx.Resources.Read(Key);
                ctx.OnTeardown(cleanup => { cleanup.Log($"Cleanup {Key}"); return Task.CompletedTask; });
                return null;
            }, [0]),
            Node(2, (_, _) => Task.FromResult<object?>(null), teardown: true));
        var result = await new ScenarioScheduler().RunAsync(definition);
        Assert.All(result, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal("Use Jane", result[1].DisplayName);
        Assert.Contains("Note Jane", result[1].Notes);
        Assert.Contains("Log Jane", result[1].Logs);
        Assert.Equal("Jane", Assert.Single(result[1].Effects).Label);
        Assert.Null(Assert.Single(result[1].Effects).DataSnapshot);
        Assert.Contains("Cleanup Jane", result[2].Logs);
    }

    [Fact]
    public async Task Completed_titles_are_observations_and_skipped_titles_keep_predecessor_names()
    {
        var result = await new ScenarioScheduler().RunAsync(Definition(
            Node(0, (_, _) => Task.FromResult<object?>(new Patient("42", "Jane")),
                completed: (_, output, f) => StepMessage.Create(f, $"Made {(Patient)output!}")),
            Node(1, (_, _) => throw new InvalidOperationException("failure"), [0]),
            Node(2, (_, _) => Task.FromResult<object?>(null), [1])));
        Assert.Equal("Made Jane", result[0].DisplayName);
        Assert.Equal(StepStatus.Skipped, result[2].Status);
        Assert.Equal("Use Jane", result[2].DisplayName);
        Assert.Contains("Note Jane", result[2].Notes);
    }

    [Fact]
    public async Task Repeated_and_concurrent_runs_do_not_share_names_through_the_DI_resolver()
    {
        using var services = new ServiceCollection().AddSingleton<ResourceIdentityResolver>().BuildServiceProvider();
        async Task<string> Run(string name)
        {
            var result = await new ScenarioScheduler().RunAsync(Definition(
                Node(0, async (_, ctx) => { await Task.Yield(); await ctx.Resources.Read(new Patient("42", name)); return null; }),
                Node(1, (_, _) => Task.FromResult<object?>(null), [0])), services);
            return result[1].DisplayName;
        }
        Assert.Equal(["Use Jane", "Use John"], await Task.WhenAll(Run("Jane"), Run("John")));
        var fresh = await new ScenarioScheduler().RunAsync(Definition(Node(0, (_, _) => Task.FromResult<object?>(null))), services);
        Assert.Equal("Use Patient:42", fresh[0].DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_captures_pending_evidence_after_producer_drain_once(bool observeBeforeCancellation)
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new CaptureObserver();
        var run = new ScenarioScheduler().RunAsync(Definition(
            Node(0, async (_, ctx) =>
            {
                using var registration = ctx.CancellationToken.Register(() => cancelled.SetResult());
                if (observeBeforeCancellation) { await ctx.Resources.Read(new Patient("42", "Jane")); }
                started.SetResult();
                await release.Task;
                if (!observeBeforeCancellation) { await ctx.Resources.Read(new Patient("42", "Jane")); }
                ctx.CancellationToken.ThrowIfCancellationRequested();
                return null;
            }),
            // An independent ready step makes the scheduler process cancellation while the producer drains.
            Node(1, async (_, _) => { await cancelled.Task; return null; }),
            Node(2, (_, _) => Task.FromResult<object?>(null), [0])), observer: observer, cancellationToken: stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StepStatus.Skipped, result[2].Status);
        Assert.Equal("Use Jane", result[2].DisplayName);
        Assert.Equal("Use Jane", Assert.Single(observer.Finished, r => r.Node.Index == 2).DisplayName);
    }
    private sealed class CaptureObserver : IStepObserver
    {
        internal List<StepResult> Finished { get; } = [];
        public Task OnStepStartingAsync(StepContext context) => Task.CompletedTask;
        public Task OnStepFinishedAsync(StepResult result) { Finished.Add(result); return Task.CompletedTask; }
    }
}
