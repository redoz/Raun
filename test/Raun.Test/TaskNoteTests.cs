using Raun.Model;
using Raun.Scheduling;
using Raun.Running;
using Raun.Reporting;
using Xunit;

namespace Raun.Test;

public sealed class TaskNoteTests
{
    [Fact]
    public async Task Note_preserves_task_identity_result_and_failure()
    {
        var value = Task.FromResult(42);
        var notedValue = value.Note("why").Note((FormattableString)$"Value {42:000}");
        Assert.Same(value, notedValue);
        Assert.Equal(42, await notedValue);
        var failure = Task.FromException(new InvalidOperationException("expected"));
        var notedFailure = failure.Note("why").Note((FormattableString)$"Failure {42}");
        Assert.Same(failure, notedFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => notedFailure);
        var task = Task.FromCanceled(new CancellationToken(canceled: true));
        var notedCanceled = task.Note("why");
        Assert.Same(task, notedCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => notedCanceled);
    }

    [Fact]
    public async Task Runtime_notes_belong_to_the_active_step_and_are_not_log_lines()
    {
        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "Given", OperationName = "seed", DisplayNameTemplate = "seed", DependsOn = [],
            Invoke = (_, _) => Task.FromResult<object?>(42).Note("Use a known seed"),
        };
        var definition = new ScenarioDefinition { ScenarioId = "notes", DisplayName = "notes", MethodName = "Notes.Run", Nodes = [node] };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(definition));
        Assert.Equal(["Use a known seed"], result.Notes);
        Assert.Empty(result.Logs);
    }

    [Fact]
    public async Task A_filtered_step_formats_notes_after_selected_inputs_finish()
    {
        var producer = NoteNode(0, (_, _) => Task.FromResult<object?>(42));
        var filtered = NoteNode(1, (_, _) => throw new InvalidOperationException("must not execute"),
            [_ => "Always available", inputs => $"Seed {inputs.Get<int>(0)}"]);
        var results = await new ScenarioScheduler().RunAsync(NoteDefinition(producer, filtered), targets: new HashSet<int> { 0 });
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal(["Always available", "Seed 42"], results[1].Notes);
    }

    [Fact]
    public async Task A_preflight_failure_keeps_notes_that_need_no_step_inputs()
    {
        var node = NoteNode(0, (_, _) => throw new InvalidOperationException("must not execute"),
            [_ => "Always available", inputs => $"Seed {inputs.Get<int>(0)}"]);
        var definition = NoteDefinition(node);
        var sink = new NoteSink();
        await new RunLoop(() => [definition], preflight: _ => throw new InvalidOperationException("offline"))
            .RunAsync(null, sink, CancellationToken.None);
        var result = Assert.Single(sink.Results, r => r.Node == node);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal(["Always available"], result.Notes);
    }

    [Fact]
    public async Task Scenario_timeout_keeps_runtime_notes_already_recorded()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = NoteNode(0, async (_, context) =>
        {
            using var registration = context.CancellationToken.Register(() => completion.TrySetCanceled(context.CancellationToken));
            return await completion.Task.Note("Already recorded");
        });
        var definition = new ScenarioDefinition
        {
            ScenarioId = "notes", DisplayName = "notes", MethodName = "Notes.Run", Nodes = [node], Timeout = TimeSpan.FromMilliseconds(100),
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(definition));
        completion.TrySetResult(null);
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal(["Already recorded"], result.Notes);
    }

    [Fact]
    public async Task Cleanup_notes_belong_to_the_teardown_step()
    {
        var node = NoteNode(0, (_, context) =>
        {
            context.OnTeardown(() => Task.CompletedTask.Note("Remove the seed after the checks"));
            return Task.FromResult<object?>(42);
        });
        var teardown = new ScenarioNode
        {
            Index = 1, StepId = "cleanup", Phase = "Teardown", OperationName = "Teardown", DisplayNameTemplate = "Teardown",
            DependsOn = [], IsTeardown = true, Invoke = (_, _) => Task.FromResult<object?>(null),
        };
        var results = await new ScenarioScheduler().RunAsync(NoteDefinition(node, teardown));
        Assert.Empty(results[0].Notes);
        Assert.Equal(["Remove the seed after the checks"], results[1].Notes);
    }

    private static ScenarioNode NoteNode(int index, Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        IReadOnlyList<Func<IStepInputs, string>>? notes = null) => new()
    {
        Index = index, StepId = "note-" + index, Phase = "Given", OperationName = "seed", DisplayNameTemplate = "seed",
        DependsOn = [], Invoke = invoke, FormatNotes = notes ?? [],
    };

    private static ScenarioDefinition NoteDefinition(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "notes", DisplayName = "notes", MethodName = "Notes.Run", Nodes = nodes,
    };

    private sealed class NoteSink : IRunEventSink
    {
        public List<StepResult> Results { get; } = [];
        public ValueTask PublishAsync(RunEvent evt)
        {
            if (evt is StepFinished finished)
            {
                Results.Add(finished.Result);
            }
            return ValueTask.CompletedTask;
        }
    }
}
