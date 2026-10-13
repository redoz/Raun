using System.Globalization;
using Raun.Model;
using Raun.Scheduling;
using Raun.Running;
using Raun.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Raun.Test.Reporting;

public sealed class StructuredMessageRuntimeTests
{
    [Fact]
    public async Task Runtime_interpolation_records_resource_parts_without_lifecycle_effects()
    {
        var resource = new Seed("one", "original");
        var node = Node(0, (_, context) =>
        {
            context.Log($"Using {resource:id}");
            var task = Task.FromResult<object?>(resource);
            Assert.Same(task, task.Note($"Because {resource:data}"));
            resource.Data = "changed";
            return task;
        });
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.Equal("Using one", Assert.Single(result.Logs));
        Assert.Equal("Because original", Assert.Single(result.Notes));
        var content = Assert.IsType<StepMessage>(Assert.Single(result.LogEntries).Content);
        Assert.Equal("original", Assert.Single(Assert.Single(result.NoteMessages).Parts, p => p.Resource.HasValue).Data);
        Assert.Equal(new ResourceIdentity(typeof(Seed), "one"), Assert.Single(content.Parts, p => p.Resource.HasValue).Resource);
        Assert.Empty(result.Effects);
        Assert.Empty(result.Lineage);
    }

    [Fact]
    public async Task Concurrent_runtime_notes_and_logs_keep_their_step_and_task_identity()
    {
        var entered = new TaskCompletionSource();
        var released = new TaskCompletionSource();
        var first = new Seed("first", "first data");
        var second = new Seed("second", "second data");
        var results = await new ScenarioScheduler().RunAsync(Definition(
            Node(0, async (_, context) =>
            {
                entered.SetResult();
                await released.Task;
                context.Log($"{first}");
                Task task = Task.CompletedTask;
                Assert.Same(task, task.Note($"{first:id}"));
                return null;
            }),
            Node(1, async (_, context) =>
            {
                await entered.Task;
                context.Log($"{second}");
                var task = Task.FromResult(42);
                Assert.Same(task, task.Note($"{second:id}"));
                Assert.Equal(42, await task);
                released.SetResult();
                return null;
            })));
        for (var i = 0; i < 2; i++)
        {
            var key = i == 0 ? "first" : "second";
            Assert.Equal(key, Assert.Single(results[i].Notes));
            Assert.Equal(key, Assert.Single(Assert.Single(results[i].NoteMessages).Parts).Resource?.Key.Value);
            Assert.Equal(key, Assert.Single(Assert.Single(results[i].LogEntries).Content!.Parts).Resource?.Key.Value);
        }
    }

    [Fact]
    public async Task Typed_formatters_take_precedence_and_title_is_captured_before_observer_and_body()
    {
        var resource = new Seed("one", "before");
        var titleCalls = 0;
        var oldCalls = 0;
        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "Given", OperationName = "seed", DisplayNameTemplate = "template", DependsOn = [],
            FormatTitleMessage = (_, formatting) => { titleCalls++; return StepMessage.Create(formatting, $"Using {resource:data}"); },
            FormatDisplayName = _ => { oldCalls++; return "old title"; },
            FormatNoteMessages = [(_, formatting) => StepMessage.Create(formatting, $"Because {resource:id}")],
            FormatNotes = [_ => { oldCalls++; return "old note"; }],
            Invoke = (_, context) =>
            {
                Assert.Equal("Using before", context.StepDisplayName);
                resource.Data = "body";
                return Task.FromResult<object?>(null);
            },
        };
        var observer = new StartObserver(() => resource.Data = "observer");
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node), observer: observer));
        Assert.Equal(1, titleCalls);
        Assert.Equal(0, oldCalls);
        Assert.Equal("Using before", observer.Name);
        Assert.Equal("Using before", result.DisplayName);
        Assert.Equal("before", Assert.Single(result.TitleMessage.Parts, p => p.Resource.HasValue).Data);
        Assert.Equal(["Because one"], result.Notes);
    }

    [Fact]
    public async Task Legacy_manual_formatters_and_initializers_project_to_plain_messages()
    {
        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "Given", OperationName = "seed", DisplayNameTemplate = "template", DependsOn = [],
            FormatDisplayName = _ => "old title", FormatNotes = [_ => "old note"],
            Invoke = (_, _) => Task.FromResult<object?>(null),
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.Equal("old title", result.TitleMessage.Text);
        Assert.Equal("old note", Assert.Single(result.NoteMessages).Text);
        var legacy = new StepResult { Node = node, DisplayName = "title", Notes = ["note"], StartedAt = default, Status = StepStatus.Passed };
        Assert.Equal("title", legacy.TitleMessage.Text);
        Assert.Equal("note", Assert.Single(legacy.NoteMessages).Text);
        var line = new LogEntry(TimeSpan.FromSeconds(1), "line");
        Assert.Equal("line", line.Content!.Text);
        var (elapsed, text) = line;
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
        Assert.Equal("line", text);
        Assert.Equal("replacement", (line with { Message = "replacement" }).Content!.Text);
        var typed = StepMessage.FromText("typed");
        var typedFirst = new StepResult
        {
            Node = node, TitleMessage = typed, DisplayName = "ignored", NoteMessages = [typed], Notes = ["ignored"],
            StartedAt = default, Status = StepStatus.Passed,
        };
        Assert.Same(typed, typedFirst.TitleMessage);
        Assert.Equal(["typed"], typedFirst.Notes);
    }

    [Fact]
    public async Task Skipped_notes_are_independent_and_use_the_configured_resolver()
    {
        var resource = new PlainSeed("key");
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<PlainSeed>(_ => "configured");
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var failed = Node(0, (_, _) => throw new InvalidOperationException("failed"));
        var skipped = MetadataNode(1, resource, [0]);
        var results = await new ScenarioScheduler().RunAsync(Definition(failed, skipped), services);
        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.Equal("PlainSeed:configured", results[1].TitleMessage.Text);
        Assert.Equal(["PlainSeed:configured", "last"], results[1].Notes);
        Assert.Equal("configured", Assert.Single(results[1].NoteMessages[0].Parts).Resource?.Key.Value);
    }

    [Fact]
    public async Task Filtered_metadata_uses_available_outputs_and_configured_resolver()
    {
        var resource = new PlainSeed("key");
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<PlainSeed>(_ => "configured");
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var producer = Node(0, (_, _) => Task.FromResult<object?>(resource));
        var filtered = new ScenarioNode
        {
            Index = 1, StepId = "filtered", Phase = "Then", OperationName = "filtered", DisplayNameTemplate = "template", DependsOn = [0],
            FormatTitleMessage = (inputs, formatting) => StepMessage.Create(formatting, $"{inputs.Get<PlainSeed>(0)}"),
            FormatNoteMessages = [(inputs, formatting) => StepMessage.Create(formatting, $"{inputs.Get<PlainSeed>(0):id}")],
            Invoke = (_, _) => throw new InvalidOperationException("must not run"),
        };
        var results = await new ScenarioScheduler().RunAsync(Definition(producer, filtered), services, targets: new HashSet<int> { 0 });
        Assert.Equal(ScenarioScheduler.NotSelectedSkipReason, results[1].SkipReason);
        Assert.Equal("PlainSeed:configured", results[1].DisplayName);
        Assert.Equal(["configured"], results[1].Notes);
        Assert.NotNull(Assert.Single(results[1].TitleMessage.Parts).Resource);
    }

    [Fact]
    public async Task Preflight_failure_keeps_available_rich_metadata_with_configured_resolver()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<PlainSeed>(_ => "configured");
        using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
        var node = MetadataNode(0, new PlainSeed("key"), []);
        var sink = new ResultSink();
        await new RunLoop(() => [Definition(node)], services: services, preflight: context =>
        {
            context.Log($"preflight {new PlainSeed("key")}");
            throw new InvalidOperationException("offline");
        }).RunAsync(null, sink, CancellationToken.None);
        var result = Assert.Single(sink.Results, r => r.Node == node);
        Assert.Equal(StepStatus.Skipped, result.Status);
        Assert.Equal("PlainSeed:configured", result.DisplayName);
        Assert.Equal(["PlainSeed:configured", "last"], result.Notes);
        Assert.NotNull(Assert.Single(result.TitleMessage.Parts).Resource);
        var preflight = Assert.Single(sink.Results, r => r.Status == StepStatus.Failed);
        Assert.Equal("configured", Assert.Single(Assert.Single(preflight.LogEntries).Content!.Parts, p => p.Resource.HasValue).Resource?.Key.Value);
    }

    [Fact]
    public async Task Scenario_timeout_reuses_title_and_keeps_recorded_parts_and_cleanup_output()
    {
        var resource = new Seed("one", "before");
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "Given", OperationName = "seed", DisplayNameTemplate = "template", DependsOn = [],
            FormatTitleMessage = (_, formatting) => { calls++; return StepMessage.Create(formatting, $"{resource:data}"); },
            Invoke = async (_, context) =>
            {
                context.Log($"{resource:id}");
                await context.Resources.Create(resource);
                context.AddAttachment("proof", "value");
                context.OnTeardown(cleanup => { cleanup.Log($"{resource}"); return Task.CompletedTask.Note($"{resource:id}"); });
                var task = completion.Task.Note($"{resource:data}");
                resource.Data = "changed";
                return await task;
            },
        };
        var teardown = new ScenarioNode
        {
            Index = 1, StepId = "cleanup", Phase = "Teardown", OperationName = "Teardown", DisplayNameTemplate = "Teardown",
            DependsOn = [], IsTeardown = true, Invoke = (_, _) => Task.FromResult<object?>(null),
        };
        var definition = new ScenarioDefinition
        {
            ScenarioId = "timeout", DisplayName = "timeout", MethodName = "Messages.Timeout", Nodes = [node, teardown], Timeout = TimeSpan.FromMilliseconds(100),
        };
        var results = await new ScenarioScheduler().RunAsync(definition);
        completion.SetResult(null);
        Assert.Equal(1, calls);
        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.Equal("before", results[0].TitleMessage.Text);
        Assert.Equal("before", Assert.Single(results[0].NoteMessages).Text);
        Assert.Equal(2, results[0].LogEntries.Count);
        Assert.All(results[0].LogEntries, e => Assert.NotNull(Assert.Single(e.Content!.Parts, p => p.Resource.HasValue).Resource));
        Assert.Single(results[0].Effects);
        Assert.Equal("value", results[0].Attachments["proof"]);
        Assert.Equal("one", Assert.Single(results[1].Notes));
        Assert.NotNull(Assert.Single(Assert.Single(results[1].NoteMessages).Parts).Resource);
        Assert.Contains(results[1].LogEntries, e => e.Content!.Parts.Any(p => p.Resource.HasValue));
    }

    [Fact]
    public async Task Branch_selection_failure_preserves_rich_title_notes_and_logs()
    {
        var resource = new Seed("one", "data");
        var node = new ScenarioNode
        {
            Index = 0, StepId = "condition", Phase = "Given", OperationName = "condition", DisplayNameTemplate = "template", DependsOn = [],
            FormatTitleMessage = (_, formatting) => StepMessage.Create(formatting, $"{resource}"),
            FormatNoteMessages = [(_, formatting) => StepMessage.Create(formatting, $"{resource:id}")],
            Invoke = (_, context) => { context.Log($"{resource}"); return Task.FromResult<object?>(true); },
            SelectArm = _ => throw new InvalidOperationException("choice failed"), Arms = ["yes"],
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node)));
        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.NotNull(Assert.Single(result.TitleMessage.Parts).Resource);
        Assert.NotNull(Assert.Single(Assert.Single(result.NoteMessages).Parts).Resource);
        Assert.NotNull(Assert.Single(Assert.Single(result.LogEntries).Content!.Parts).Resource);
    }

    [Fact]
    public void Explicit_context_resolver_wins_and_plain_log_overloads_share_canonical_storage()
    {
        var explicitResolver = new ResourceIdentityResolver();
        explicitResolver.Identify<PlainSeed>(_ => "explicit");
        var diResolver = new ResourceIdentityResolver();
        diResolver.Identify<PlainSeed>(_ => "di");
        using var services = new ServiceCollection().AddSingleton(diResolver).BuildServiceProvider();
        var context = new ScenarioContext("step", "step", services, explicitResolver, null, CancellationToken.None);
        var resource = new PlainSeed("key");
        context.Log($"{resource}");
        context.Log((FormattableString)$"{resource:id}");
        context.Log(StepMessage.FromText("typed"));
        context.Log("plain");
        Assert.Equal(["PlainSeed:explicit", "explicit", "typed", "plain"], context.Logs);
        Assert.All(context.LogEntries, e => Assert.Equal(e.Message, e.Content!.Text));
        Assert.Empty(context.Resources.Effects);
    }

    [Fact]
    public void Log_entries_preserve_value_equality_and_results_project_authoritative_log_text()
    {
        var elapsed = TimeSpan.FromSeconds(1);
        var first = new LogEntry(elapsed, "line");
        var second = new LogEntry(elapsed, "line");
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first == second);
        var identity = new ResourceIdentity(typeof(Seed), "one");
        var rich = new LogEntry(elapsed, "ignored") { Content = new StepMessage([new StepMessagePart("one", identity)]) };
        var sameRich = new LogEntry(elapsed, "ignored") { Content = new StepMessage([new StepMessagePart("one", identity)]) };
        Assert.Equal(rich, sameRich);
        Assert.NotEqual(rich, new LogEntry(elapsed, "one"));
        var node = Node(0, (_, _) => Task.FromResult<object?>(null));
        var result = new StepResult
        {
            Node = node, DisplayName = "title", Status = StepStatus.Passed, StartedAt = default,
            Logs = ["stale"], LogEntries = [rich],
        };
        Assert.Equal(["one"], result.Logs);
        var legacy = new StepResult { Node = node, DisplayName = "title", Status = StepStatus.Passed, StartedAt = default, Logs = ["legacy"] };
        Assert.Equal(["legacy"], legacy.Logs);
        Assert.Empty(legacy.LogEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_failure_or_cancellation_retains_recorded_messages(bool canceled)
    {
        var resource = new Seed("one", "data");
        using var source = new CancellationTokenSource();
        Action cancel = source.Cancel;
        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "Given", OperationName = "seed", DisplayNameTemplate = "template", DependsOn = [],
            FormatTitleMessage = (_, formatting) => StepMessage.Create(formatting, $"{resource}"),
            Invoke = async (_, context) =>
            {
                context.Log($"{resource}");
                await Task.CompletedTask.Note((FormattableString)$"{resource:id}");
                await Task.CompletedTask.Note(StepMessage.FromText("frozen"));
                if (canceled)
                {
                    cancel();
                    throw new OperationCanceledException(context.CancellationToken);
                }
                throw new InvalidOperationException("expected");
            },
        };
        var result = Assert.Single(await new ScenarioScheduler().RunAsync(Definition(node), cancellationToken: source.Token));
        Assert.Equal(canceled ? StepStatus.Skipped : StepStatus.Failed, result.Status);
        Assert.NotNull(Assert.Single(result.TitleMessage.Parts).Resource);
        Assert.NotNull(Assert.Single(result.NoteMessages[0].Parts).Resource);
        Assert.Equal(["one", "frozen"], result.Notes);
        Assert.NotNull(Assert.Single(Assert.Single(result.LogEntries).Content!.Parts).Resource);
    }

    [Fact]
    public async Task Typed_titles_use_invariant_culture_and_keep_the_configured_resolver()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var amount = 12.5m;
            var resource = new PlainSeed("key");
            var resolver = new ResourceIdentityResolver();
            resolver.Identify<PlainSeed>(_ => "configured");
            using var services = new ServiceCollection().AddSingleton(resolver).BuildServiceProvider();
            MessageFormattingContext? titleFormatting = null;
            var node = new ScenarioNode
            {
                Index = 0, StepId = "typed", Phase = "Given", OperationName = "typed", DisplayNameTemplate = "template", DependsOn = [],
                FormatTitleMessage = (_, formatting) =>
                {
                    titleFormatting = formatting;
                    return StepMessage.Create(formatting, $"{amount:F2} {resource}");
                },
                FormatNoteMessages = [(_, formatting) => StepMessage.Create(formatting, $"{amount:F2}")],
                Invoke = async (_, context) =>
                {
                    context.Log($"{amount:F2}");
                    await Task.CompletedTask.Note($"runtime {amount:F2}");
                    return null;
                },
            };
            var legacy = new ScenarioNode
            {
                Index = 1, StepId = "legacy", Phase = "Given", OperationName = "legacy", DisplayNameTemplate = "template", DependsOn = [],
                FormatDisplayName = _ => amount.ToString("F2", CultureInfo.CurrentCulture),
                Invoke = (_, _) => Task.FromResult<object?>(null),
            };
            var results = await new ScenarioScheduler().RunAsync(Definition(node, legacy), services);
            Assert.Equal("12.50 PlainSeed:configured", results[0].DisplayName);
            Assert.NotNull(titleFormatting);
            Assert.Same(resolver, titleFormatting.Resolver);
            Assert.Same(CultureInfo.InvariantCulture, titleFormatting.FormatProvider);
            Assert.Equal("configured", Assert.Single(results[0].TitleMessage.Parts, p => p.Resource.HasValue).Resource?.Key.Value);
            Assert.Equal(["12,50", "runtime 12,50"], results[0].Notes);
            Assert.Equal("12,50", Assert.Single(results[0].Logs));
            Assert.Equal("12,50", results[1].DisplayName);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ScenarioNode MetadataNode(int index, PlainSeed resource, int[] dependencies) => new()
    {
        Index = index, StepId = "metadata-" + index, Phase = "Then", OperationName = "metadata", DisplayNameTemplate = "template", DependsOn = dependencies,
        FormatTitleMessage = (_, formatting) => StepMessage.Create(formatting, $"{resource}"),
        FormatNoteMessages = [(_, formatting) => StepMessage.Create(formatting, $"{resource}"),
            (inputs, formatting) => StepMessage.Create(formatting, $"{inputs.Get<Seed>(99)}"),
            (_, _) => StepMessage.FromText("last")],
        Invoke = (_, _) => throw new InvalidOperationException("must not run"),
    };

    private sealed class StartObserver(Action action) : IStepObserver
    {
        public string? Name { get; private set; }
        public Task OnStepStartingAsync(StepContext context) { Name = context.DisplayName; action(); return Task.CompletedTask; }
        public Task OnStepFinishedAsync(StepResult result) => Task.CompletedTask;
    }

    private sealed class ResultSink : IRunEventSink
    {
        public List<StepResult> Results { get; } = [];
        public ValueTask PublishAsync(RunEvent evt)
        {
            if (evt is StepFinished finished) { Results.Add(finished.Result); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record PlainSeed(string Key);

    private static ScenarioNode Node(int index, Func<IStepInputs, ScenarioContext, Task<object?>> invoke) => new()
    {
        Index = index, StepId = "step-" + index, Phase = "Given", OperationName = "Seed", DisplayNameTemplate = "seed",
        DependsOn = [], Invoke = invoke,
    };

    private static ScenarioDefinition Definition(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "structured", DisplayName = "structured", MethodName = "Messages.Run", Nodes = nodes,
    };

    private sealed class Seed(string key, string data) : IResourceIdentity
    {
        public ResourceKey GetResourceKey() => key;
        public string Data { get; set; } = data;
        public override string ToString() => Data;
    }
}
