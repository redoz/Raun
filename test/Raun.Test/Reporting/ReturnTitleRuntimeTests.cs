using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Scheduling;
using Xunit;

namespace Raun.Test.Reporting;

public sealed class ReturnTitleRuntimeTests
{
    [Fact]
    public async Task Successful_return_updates_terminal_title_and_keeps_start_context_and_span_names()
    {
        string? contextName = null;
        string? spanName = null;
        var output = new Seed("returned", "original");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RaunTelemetry.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var node = Node(0, (_, context) =>
        {
            contextName = context.StepDisplayName;
            spanName = Activity.Current?.DisplayName;
            return Task.FromResult<object?>(output);
        }, FormatReturn);
        var observer = new Observer();
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node), observer: observer));
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal("fallback", observer.StartName);
        Assert.Equal("fallback", contextName);
        Assert.Equal("fallback", spanName);
        Assert.Equal("returned", observer.FinishedName);
        Assert.Equal("returned", result.DisplayName);
        Assert.Equal(new ResourceIdentity(typeof(Seed), "returned"), Assert.Single(result.TitleMessage.Parts).Resource);
        Assert.Equal("original", SnapshotData(Assert.Single(result.TitleMessage.Parts)));
        Assert.Empty(result.Effects);
    }

    [Theory]
    [InlineData(null, "fallback")]
    [InlineData("", "")]
    [InlineData(0, "0")]
    [InlineData(false, "False")]
    public async Task Only_null_return_keeps_fallback(object? output, string expected)
    {
        var node = Node(0, (_, _) => Task.FromResult(output), (_, value, formatting) => StepMessage.Create(formatting, $"{value}"));
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal(expected, result.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Formatter_exception_or_null_message_preserves_fallback_without_failing(bool nullMessage)
    {
        var node = Node(0, (_, _) => Task.FromResult<object?>(new Seed("returned", "original")), (_, _, _) =>
            nullMessage ? null! : throw new InvalidOperationException("formatter failed"));
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.Equal(StepStatus.Passed, result.Status);
        Assert.Equal("fallback", result.DisplayName);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task Completion_preserves_input_parts_captured_before_start_observer_and_body_mutate_inputs()
    {
        var input = new Seed("input", "before");
        var producer = Node(0, (_, _) => Task.FromResult<object?>(input));
        var calls = 0;
        var node = new ScenarioNode
        {
            Index = 1, StepId = "return", Phase = "When", OperationName = "return", DisplayNameTemplate = "fallback", DependsOn = [0],
            FormatTitleMessage = (inputs, formatting) =>
            {
                calls++;
                return StepMessage.Create(formatting, $"Using {inputs.Get<Seed>(0):data} produced fallback");
            },
            Invoke = (inputs, _) =>
            {
                inputs.Get<Seed>(0).Data = "body";
                return Task.FromResult<object?>(new Seed("returned", "output snapshot"));
            },
            FormatCompletedTitleMessage = (initial, value, formatting) =>
            {
                var parts = initial.Parts.ToArray();
                parts[2] = new(" produced ");
                return new StepMessage(parts.Concat(StepMessage.Create(formatting, $"{(Seed)value!:id}").Parts));
            },
        };
        var observer = new Observer(() => input.Data = "observer", "return");
        var results = await new ScenarioScheduler().RunAsync(Definition(producer, node), observer: observer);
        Assert.Equal(1, calls);
        Assert.Equal("Using before produced returned", results[1].DisplayName);
        var part = Assert.Single(results[1].TitleMessage.Parts, p => p.Resource?.Key.Value == "input");
        Assert.Equal("before", part.Data);
        Assert.Equal("before", SnapshotData(part));
    }

    [Fact]
    public async Task Returned_resource_snapshot_is_frozen_before_a_dependent_mutates_output()
    {
        var output = new Seed("returned", "before");
        var producer = Node(0, (_, _) => Task.FromResult<object?>(output), FormatReturn);
        var dependent = Node(1, (inputs, _) => { inputs.Get<Seed>(0).Data = "after"; return Task.FromResult<object?>(null); }, dependencies: [0]);
        var results = await new ScenarioScheduler().RunAsync(Definition(producer, dependent));
        var part = Assert.Single(results[0].TitleMessage.Parts);
        Assert.Equal("returned", part.Text);
        Assert.Equal("before", SnapshotData(part));
        Assert.Equal("after", output.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_failure_or_cancellation_never_invokes_completion_formatter(bool canceled)
    {
        using var source = new CancellationTokenSource();
        var calls = 0;
        var node = Node(0, (_, context) =>
        {
            if (canceled)
            {
                source.Cancel();
                throw new OperationCanceledException(context.CancellationToken);
            }
            throw new InvalidOperationException("body failed");
        }, (_, _, _) => { calls++; return StepMessage.FromText("completed"); });
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node), cancellationToken: source.Token));
        Assert.Equal(canceled ? StepStatus.Skipped : StepStatus.Failed, result.Status);
        Assert.Equal("fallback", result.DisplayName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Dependency_skip_never_invokes_completion_formatter()
    {
        var calls = 0;
        var failed = Node(0, (_, _) => throw new InvalidOperationException("body failed"));
        var skipped = Node(1, (_, _) => throw new InvalidOperationException("must not run"),
            (_, _, _) => { calls++; return StepMessage.FromText("completed"); }, [0]);
        var results = await new ScenarioScheduler().RunAsync(Definition(failed, skipped));
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal("fallback", results[1].DisplayName);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Step_or_scenario_timeout_keeps_start_title(bool scenarioTimeout)
    {
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        var node = new ScenarioNode
        {
            Index = 0, StepId = "timed", Phase = "Given", OperationName = "timed", DisplayNameTemplate = "fallback", DependsOn = [],
            Timeout = scenarioTimeout ? null : TimeSpan.FromMilliseconds(100),
            Invoke = async (_, ctx) =>
            {
                using var registration = ctx.CancellationToken.Register(() => pending.TrySetCanceled(ctx.CancellationToken));
                return await pending.Task;
            },
            FormatCompletedTitleMessage = (_, _, _) => { completed = true; return StepMessage.FromText("completed"); },
        };
        var definition = new ScenarioDefinition
        {
            ScenarioId = "timeout", DisplayName = "timeout", MethodName = "Returns.Timeout", Nodes = [node],
            Timeout = scenarioTimeout ? TimeSpan.FromMilliseconds(100) : null,
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(definition));
        pending.TrySetResult(null);
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.IsType<TimeoutException>(result.Exception);
        Assert.Equal("fallback", result.DisplayName);
        Assert.Equal("fallback", result.TitleMessage.Text);
        Assert.False(completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_branch_selection_failure_restores_frozen_start_title(bool invalidArm)
    {
        var input = new Seed("input", "before");
        var completed = false;
        var node = new ScenarioNode
        {
            Index = 0, StepId = "condition", Phase = "Given", OperationName = "condition", DisplayNameTemplate = "template", DependsOn = [],
            FormatTitleMessage = (_, formatting) => StepMessage.Create(formatting, $"{input:data}"),
            Invoke = (_, _) => { input.Data = "body"; return Task.FromResult<object?>(new Seed("returned", "original")); },
            FormatCompletedTitleMessage = (_, value, formatting) => { completed = true; return FormatReturn(StepMessage.FromText("unused"), value, formatting); },
            SelectArm = _ => invalidArm ? 1 : throw new InvalidOperationException("choice failed"), Arms = ["yes"],
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.True(completed);
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("before", result.DisplayName);
        Assert.Equal("before", Assert.Single(result.TitleMessage.Parts).Data);
        Assert.Equal("before", SnapshotData(Assert.Single(result.TitleMessage.Parts)));
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task Completed_title_uses_invariant_culture_and_configured_resolver()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var resolver = new ResourceIdentityResolver();
            resolver.Identify<Seed>(_ => "configured");
            using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
            MessageFormattingContext? captured = null;
            var node = Node(0, (_, _) => Task.FromResult<object?>(new Seed("returned", "data")), (_, value, formatting) =>
            {
                captured = formatting;
                return StepMessage.Create(formatting, $"{12.5m:F2} {(Seed)value!:id}");
            });
            var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node), services));
            Assert.Equal("12.50 configured", result.DisplayName);
            Assert.NotNull(captured);
            Assert.Same(resolver, captured.Resolver);
            Assert.Same(CultureInfo.InvariantCulture, captured.FormatProvider);
            Assert.Equal("configured", Assert.Single(result.TitleMessage.Parts, p => p.Resource.HasValue).Resource?.Key.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static StepMessage FormatReturn(StepMessage initial, object? output, MessageFormattingContext formatting)
        => output is Seed seed ? StepMessage.Create(formatting, $"{seed:id}") : initial;

    private static string? SnapshotData(StepMessagePart part)
    {
        using var snapshot = JsonDocument.Parse(Assert.IsType<string>(part.Snapshot));
        return snapshot.RootElement.GetProperty("Data").GetString();
    }

    private static ScenarioNode Node(int index, Func<IStepInputs, ScenarioContext, Task<object?>> invoke,
        Func<StepMessage, object?, MessageFormattingContext, StepMessage>? completed = null, int[]? dependencies = null) => new()
    {
        Index = index, StepId = "step-" + index, Phase = "Given", OperationName = "Seed", DisplayNameTemplate = "fallback",
        DependsOn = dependencies ?? [], Invoke = invoke, FormatCompletedTitleMessage = completed,
    };

    private static ScenarioDefinition Definition(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "returns", DisplayName = "returns", MethodName = "Returns.Run", Nodes = nodes,
    };

    private sealed class Observer(Action? action = null, string? stepId = null) : IStepObserver
    {
        public string? StartName { get; private set; }
        public string? FinishedName { get; private set; }
        public Task OnStepStartingAsync(StepContext context)
        {
            StartName = context.DisplayName;
            if (stepId is null || context.Node.StepId == stepId) { action?.Invoke(); }
            return Task.CompletedTask;
        }
        public Task OnStepFinishedAsync(StepResult result) { FinishedName = result.DisplayName; return Task.CompletedTask; }
    }

    public sealed class Seed(string key, string data) : IResourceIdentity
    {
        public ResourceKey GetResourceKey() => key;
        public string Data { get; set; } = data;
        public override string ToString() => Data;
    }
}
