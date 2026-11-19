using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Raun.Model;
using Microsoft.Extensions.DependencyInjection;
using Raun.Reporting;
using Raun.Scheduling;

namespace Raun.Running;

/// <summary>
/// The run loop: turns a run request's selection into a set of <em>distinct</em> scenarios
/// and runs each one exactly once through a <see cref="ScenarioScheduler"/>, emitting the run-event
/// envelope (<see cref="RunStarted"/> → per-scenario <see cref="ScenarioStarted"/>/steps/
/// <see cref="ScenarioFinished"/> → <see cref="RunFinished"/>) onto an <see cref="IRunEventSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Selecting several step uids of one scenario yields a single scheduler run (the DAG executes the
/// scenario's steps once and memoizes their outputs), so a multi-step filter ⇒ one run. Within that
/// run the scheduler executes only the selected steps and what they transitively need (dependencies,
/// merge sources, guard conditions) plus teardown; steps outside that closure are neither run nor
/// reported. Naming one step therefore runs everything up to and including it, and nothing after.
/// </para>
/// <para>
/// Each scenario run gets its own <see cref="CancellationTokenSource"/>, linked to the platform's
/// token and <strong>owned by the loop</strong> — never tied to a single step node's lifecycle — so
/// one step can never cancel the shared run out from under its siblings. Scenarios run concurrently
/// up to <see cref="MaxParallelScenarios"/>; the scheduler additionally parallelizes steps
/// <em>within</em> a scenario.
/// </para>
/// </remarks>
public sealed class RunLoop
{
    /// <summary>Runs one scenario to completion and returns its step results. The extension point
    /// for a host that wraps or replaces the scheduler; the default drives a real
    /// <see cref="ScenarioScheduler"/>, and the tests substitute it to count runs.</summary>
    public delegate Task<IReadOnlyList<StepResult>> RunScenario(
        ScenarioDefinition definition,
        IStepObserver observer,
        IServiceProvider? services,
        IReadOnlySet<int>? targets,
        CancellationToken cancellationToken);

    private readonly Func<IEnumerable<ScenarioDefinition>> scenarioSource;
    private readonly RunScenario runScenario;
    private readonly bool simulateTime;
    private readonly IServiceProvider? services;
    private readonly Func<ScenarioContext, Task>? preflight;
    private readonly int maxParallelScenarios;
    private readonly int maxParallelSteps;
    private readonly RunStopSignal? stopSignal;
    private readonly TimeSpan cancellationGracePeriod;
    private readonly TimeSpan cleanupTimeout;

    /// <summary>How many scenarios may run at once.</summary>
    public int MaxParallelScenarios => maxParallelScenarios;

    /// <summary>Maximum active steps per scenario; 0 means unbounded.</summary>
    public int MaxParallelSteps => maxParallelSteps;

    /// <param name="scenarioSource">Supplies the registered scenarios to consider for the run.</param>
    /// <param name="runScenario">
    /// How to run one scenario; defaults to a fresh <see cref="ScenarioScheduler"/> per run.
    /// </param>
    /// <param name="simulateTime">
    /// When true, the default scenario runner builds a <see cref="ScenarioScheduler"/> in simulated-time
    /// mode (deterministic DAG-correct timeline driven from step bodies via
    /// <see cref="ScenarioContext.SimulateElapsed"/>). Defaults to <see langword="false"/> so production
    /// runs use real timing. Ignored when an explicit <paramref name="runScenario"/> seam is supplied.
    /// </param>
    /// <param name="services">
    /// The service provider handed to every <see cref="ScenarioContext"/> the default runner creates,
    /// surfacing as <c>ctx.Services</c> to step bodies. <see langword="null"/> (the default) is a real,
    /// supported path — an adapter built without a provider — and leaves <c>ctx.Services</c> null. Ignored when an explicit <paramref name="runScenario"/> seam is supplied.
    /// </param>
    /// <param name="preflight">
    /// Run-level setup executed once before any scenario and reported as its own node. When it fails,
    /// every scenario's steps report skipped naming preflight, and the run still completes so the
    /// report stays whole. <see langword="null"/> (the default) means no preflight node exists at all.
    /// </param>
    /// <param name="maxParallelScenarios">
    /// How many scenarios may run at once. <c>0</c> (the default) means <see cref="Environment.ProcessorCount"/>;
    /// <c>1</c> runs scenarios one after another. The separate per-scenario step limit controls fan-out within each scenario.
    /// </param>
    /// <param name="stopSignal">
    /// Set by the host when it wants the run to wind down (under Microsoft.Testing.Platform, the
    /// graceful-stop capability behind <c>--maximum-failed-tests</c>). When a stop is requested the
    /// launcher stops admitting scenarios; whatever is running drains and reports, exactly as it
    /// does when a scenario faults. <see langword="null"/> (the default) means no stop can be requested.
    /// </param>
    /// <param name="cancellationGracePeriod">Real-time cancellation grace before terminating the test host; defaults to 60 seconds. Custom runners own their own cancellation policy.</param>
    /// <param name="maxParallelSteps">Maximum active steps per scenario; 0 means unbounded. Custom runners own their step limits.</param>
    /// <param name="cleanupTimeout">Finite positive deadline per cleanup callback; defaults to 60 seconds. Custom runners own their cleanup policy.</param>
    public RunLoop(
        Func<IEnumerable<ScenarioDefinition>> scenarioSource,
        RunScenario? runScenario = null,
        bool simulateTime = false,
        IServiceProvider? services = null,
        Func<ScenarioContext, Task>? preflight = null,
        int maxParallelScenarios = 0,
        RunStopSignal? stopSignal = null,
        TimeSpan? cancellationGracePeriod = null,
        TimeSpan? cleanupTimeout = null,
        int maxParallelSteps = 0)
    {
        ArgumentNullException.ThrowIfNull(scenarioSource);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelScenarios);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelSteps);
        this.scenarioSource = scenarioSource;
        this.simulateTime = simulateTime;
        this.services = services;
        this.preflight = preflight;
        this.maxParallelScenarios = maxParallelScenarios == 0 ? Environment.ProcessorCount : maxParallelScenarios;
        this.maxParallelSteps = maxParallelSteps;
        this.runScenario = runScenario ?? DefaultRunScenario;
        this.stopSignal = stopSignal;
        this.cancellationGracePeriod = cancellationGracePeriod ?? ScenarioScheduler.DefaultCancellationGracePeriod;
        CancellationWatchdog.ValidateGrace(this.cancellationGracePeriod);
        this.cleanupTimeout = cleanupTimeout ?? ScenarioScheduler.DefaultCleanupTimeout;
        ScenarioScheduler.ValidateCleanupTimeout(this.cleanupTimeout);
    }

    /// <summary>
    /// Maps a run selector onto the distinct scenarios it selects. A <see langword="null"/> selector
    /// (the request had no filter, or a no-op one) selects every scenario; otherwise a scenario is
    /// selected when any of its steps matches.
    /// </summary>
    internal static IReadOnlyList<ScenarioDefinition> SelectScenarios(
        IEnumerable<ScenarioDefinition> scenarios,
        NodeSelector? selector)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        if (selector is null)
        {
            return scenarios.ToList();
        }

        var selected = new List<ScenarioDefinition>();
        foreach (var definition in scenarios)
        {
            foreach (var step in definition.Nodes)
            {
                if (selector.Matches(definition, step))
                {
                    selected.Add(definition);
                    break; // distinct scenario, regardless of how many of its steps matched
                }
            }
        }

        return selected;
    }

    /// <summary>Runs every scenario the <paramref name="selector"/> selects (or all when null),
    /// emitting the run-event envelope (<see cref="RunStarted"/> → per scenario
    /// <see cref="ScenarioStarted"/>/steps/<see cref="ScenarioFinished"/> → <see cref="RunFinished"/>).</summary>
    public async ValueTask RunAsync(NodeSelector? selector, IRunEventSink bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var selected = SelectScenarios(scenarioSource(), selector);

        // A filter that selects nothing runs nothing, preflight included: bringing the environment
        // up for no scenario would also turn a mistyped filter into a passing run of one test.
        var nothingSelected = selector is not null && selected.Count == 0;
        var preflightDefinition = preflight is null || nothingSelected ? null : Preflight.Definition(preflight);
        IReadOnlyList<ScenarioDefinition> order = preflightDefinition is null ? selected : [preflightDefinition, .. selected];
        await bus.PublishAsync(new RunStarted(selected.Count, order)).ConfigureAwait(false);

        // The run's own span: a small root that every scenario span LINKS to rather than nests under,
        // so a suite never becomes one giant trace and sampling can decide per scenario. Started, then
        // cleared from Activity.Current so nothing parents under it (see RaunTelemetry).
        var runId = Guid.NewGuid().ToString("N");
        using var runActivity = RaunTelemetry.Source.StartActivity("raun.run", ActivityKind.Internal);
        if (runActivity is { IsAllDataRequested: true })
        {
            runActivity.SetTag(RaunTelemetry.Attributes.Run, runId);
            runActivity.SetTag("raun.scenario.count", selected.Count);
        }

        Activity.Current = null;
        var runContext = runActivity?.Context;

        try
        {
            // Run-level setup, before any scenario: a filtered run of one step still needs whatever
            // preflight brings up.
            string? skipReason = null;
            if (preflightDefinition is not null)
            {
                var results = await RunOneAsync(preflightDefinition, bus, selector: null, runContext, runId, waited: TimeSpan.Zero, waitedFor: null, cancellationToken)
                    .ConfigureAwait(false);

                // A preflight the host cancelled did not fail, and the scenarios behind it are
                // cancelled too, not victims of a failure.
                skipReason = ScenarioOutcome.Of(results).Status switch
                {
                    ScenarioStatus.Passed => null,
                    ScenarioStatus.Canceled => ScenarioScheduler.CanceledSkipReason,
                    _ => Preflight.FailedSkipReason,
                };
            }

            if (skipReason is not null)
            {
                // Attribute the failure to a row rather than to an exit code: every selected step
                // reports skipped naming preflight, and the run still completes so the report is whole.
                foreach (var definition in selected)
                {
                    await SkipScenarioAsync(definition, bus, selector, skipReason, runContext, runId).ConfigureAwait(false);
                }
            }
            else
            {
                await LaunchAsync(selected, bus, selector, runContext, runId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // RunFinished always publishes, even when the body above throws (a launcher can orphan a
            // scenario fault).
            await bus.PublishAsync(new RunFinished()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the selected scenarios with at most <see cref="MaxParallelScenarios"/> in flight, each
    /// admitted only when its contended-resource uses are compatible with everything running.
    /// Scenarios launch in registration order as slots free up; a refused scenario is retried each
    /// time a running one finishes, and is admitted at the latest when everything else has drained.
    /// Cancellation keeps the sequential loop's contract: the first scenario always launches (an
    /// up-front cancellation still reports its steps as skipped through the scheduler); after any
    /// launch, an observed cancellation stops further launches; whatever is running drains through
    /// its own linked token. A scenario task that faults (never a step failure — see the comment
    /// below) does not stop the others: every scenario still runs and reports, and the faults are
    /// thrown together at the end as <see cref="ScenarioRunException"/>, after which
    /// <c>RunFinished</c> publishes from the caller's <c>finally</c>.
    /// </summary>
    private async ValueTask LaunchAsync(
        IReadOnlyList<ScenarioDefinition> selected,
        IRunEventSink bus,
        NodeSelector? selector,
        ActivityContext? runContext,
        string runId,
        CancellationToken cancellationToken)
    {
        var pending = new List<ScenarioDefinition>(selected);
        var running = new Dictionary<Task<IReadOnlyList<StepResult>>, ScenarioDefinition>();
        var waits = new Dictionary<ScenarioDefinition, Wait>();
        var gate = new ContentionGate();
        var started = false;
        List<Exception>? faults = null;

        while (pending.Count > 0 || running.Count > 0)
        {
            var i = 0;
            while (i < pending.Count && running.Count < maxParallelScenarios)
            {
                if ((started && cancellationToken.IsCancellationRequested) || stopSignal?.IsStopRequested == true)
                {
                    break;
                }

                var definition = pending[i];
                Type? refusedBy;
                try
                {
                    refusedBy = gate.Acquire(definition.Uses);
                }
                catch (InvalidOperationException ex)
                {
                    // A mis-declared resource token (no kind attribute, or a pool capacity below 1):
                    // this scenario cannot be admitted, the others can.
                    pending.RemoveAt(i);
                    (faults ??= []).Add(new ScenarioRunException(definition, ex));
                    continue;
                }

                if (refusedBy is not null)
                {
                    // A slot was free and the gate said no: that is contention, worth reporting.
                    if (!waits.ContainsKey(definition))
                    {
                        waits[definition] = new Wait(Stopwatch.GetTimestamp(), refusedBy);
                    }

                    i++;
                    continue;
                }

                pending.RemoveAt(i);
                var waited = TimeSpan.Zero;
                Type? waitedFor = null;
                if (waits.Remove(definition, out var wait))
                {
                    waited = Stopwatch.GetElapsedTime(wait.Since);
                    waitedFor = wait.By;
                }

                var run = RunOneAsync(definition, bus, selector, runContext, runId, waited, waitedFor, cancellationToken).AsTask();
                running[run] = definition;
                started = true;
            }

            if (running.Count == 0)
            {
                break; // cancelled with nothing left in flight; the gate is empty, so nothing else can be blocked
            }

            var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var done = running[finished];
            running.Remove(finished);
            gate.Release(done.Uses);

            // Step failures never fault a scenario task: the scheduler turns them into
            // StepResult.Failed and returns normally. What faults one is something outside its steps
            // (a scoped service whose Dispose throws, an invalid definition, a scheduler bug). That
            // is this scenario's problem: record it and keep going, so the other scenarios still run
            // and report before the faults are rethrown together.
            if (finished.IsFaulted)
            {
                (faults ??= []).AddRange(finished.Exception!.InnerExceptions.Select(ex => new ScenarioRunException(done, ex)));
            }
            else if (finished.IsCanceled)
            {
                (faults ??= []).Add(new ScenarioRunException(done, new TaskCanceledException(finished)));
            }
        }

        if (faults is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(faults[0]).Throw();
        }
        else if (faults is { Count: > 1 })
        {
            throw new AggregateException(faults);
        }
    }

    /// <summary>When a scenario was first refused by the gate, and by which resource.</summary>
    private readonly record struct Wait(long Since, Type By);

    /// <summary>
    /// Reports the steps of a scenario that never ran because preflight did not pass. Only the steps
    /// the run selected, with what they need, exactly as the scheduler would have run them: a step
    /// outside that set was never part of the run, so no sink hears of it and its result says "not
    /// selected".
    /// </summary>
    private async ValueTask SkipScenarioAsync(
        ScenarioDefinition definition, IRunEventSink bus, NodeSelector? selector, string reason, ActivityContext? runContext, string runId)
    {
        var targets = SelectTargets(definition, selector);
        var included = targets is null ? null : ScenarioGraph.Closure(definition.Nodes, targets);
        await bus.PublishAsync(new ScenarioStarted(definition)).ConfigureAwait(false);
        using var scenarioActivity = StartScenarioActivity(definition, runContext, runId, waited: TimeSpan.Zero, waitedFor: null);
        scenarioActivity?.SetTag(RaunTelemetry.Attributes.TestSuiteRunStatus, "skipped");

        var scope = services?.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory factory
            ? factory.CreateAsyncScope()
            : (AsyncServiceScope?)null;
        try
        {
            var formatting = ScenarioContext.CreateMessageFormattingContext(scope?.ServiceProvider ?? services, resourceMappings: definition.ResourceMappings).WithLabelMemory(definition.Nodes);
            var results = new List<StepResult>(definition.Nodes.Count);
            foreach (var node in definition.Nodes)
            {
                // The teardown node is part of every run of its scenario, filtered or not.
                var selectedNode = included is null || node.IsTeardown || included.Contains(node.Index);
                var titleMessage = StepNotes.FormatTitle(node, formatting);
                var result = new StepResult
                {
                    Node = node,
                    TitleMessage = titleMessage,
                    Status = StepStatus.Skipped,
                    StartedAt = null,
                    SkipReason = selectedNode ? reason : ScenarioScheduler.NotSelectedSkipReason,
                    NoteMessages = StepNotes.FormatMessages(node, formatting),
                };
                results.Add(result);
                if (selectedNode)
                {
                    await bus.PublishAsync(new StepFinished(definition, result)).ConfigureAwait(false);
                }
            }

            await bus.PublishAsync(new ScenarioFinished(definition, results)).ConfigureAwait(false);
        }
        finally
        {
            if (scope is { } ownedScope)
            {
                await ownedScope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<IReadOnlyList<StepResult>> RunOneAsync(
        ScenarioDefinition definition,
        IRunEventSink bus,
        NodeSelector? selector,
        ActivityContext? runContext,
        string runId,
        TimeSpan waited,
        Type? waitedFor,
        CancellationToken cancellationToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await bus.PublishAsync(new ScenarioStarted(definition, waited, waitedFor)).ConfigureAwait(false);

        // The scenario's span is a ROOT (Activity.Current is null here) linked to the run span. It is
        // ambient while the scheduler runs, so every step span nests under it and every outgoing
        // HttpClient call inside a step carries this trace's id across the wire.
        using var scenarioActivity = StartScenarioActivity(definition, runContext, runId, waited, waitedFor);

        var observer = new BusObserver(definition, bus);
        var targets = SelectTargets(definition, selector);

        // One DI scope per scenario, so AddScoped means "per scenario" and AddSingleton means "per
        // run" through ordinary .NET semantics. Disposed only after the scenario returns — the
        // scheduler runs teardown as the last thing inside it, and a cleanup may hold something
        // resolved from this scope.
        var scope = services?.GetService(typeof(IServiceScopeFactory)) is IServiceScopeFactory factory
            ? factory.CreateAsyncScope()
            : (AsyncServiceScope?)null;

        try
        {
            var scenarioServices = scope?.ServiceProvider ?? services;
            var results = await runScenario(definition, observer, scenarioServices, targets, runCts.Token).ConfigureAwait(false);

            if (scenarioActivity is not null)
            {
                var status = SuiteRunStatus(ScenarioOutcome.Of(results));
                scenarioActivity.SetTag(RaunTelemetry.Attributes.TestSuiteRunStatus, status);
                if (status != "success")
                {
                    scenarioActivity.SetStatus(ActivityStatusCode.Error, status);
                }
            }

            await bus.PublishAsync(new ScenarioFinished(definition, results)).ConfigureAwait(false);
            return results;
        }
        finally
        {
            if (scope is { } ownedScope)
            {
                await ownedScope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Starts a scenario's root span, linked (not parented) to the run span, with its identity tags.</summary>
    private static Activity? StartScenarioActivity(
        ScenarioDefinition definition, ActivityContext? runContext, string runId, TimeSpan waited, Type? waitedFor)
    {
        var links = runContext is { } context ? new[] { new ActivityLink(context) } : null;
        var activity = RaunTelemetry.Source.StartActivity(
            definition.DisplayName, ActivityKind.Internal, parentContext: default, tags: null, links: links);
        if (activity is null || !activity.IsAllDataRequested)
        {
            return activity;
        }

        activity.SetTag(RaunTelemetry.Attributes.TestSuiteName, definition.DisplayName);
        activity.SetTag(RaunTelemetry.Attributes.Scenario, definition.ScenarioId);
        activity.SetTag(RaunTelemetry.Attributes.Run, runId);
        activity.SetTag("raun.scenario.method", definition.MethodName);
        if (!string.IsNullOrEmpty(definition.SourceFile) && definition.SourceLine > 0)
        {
            activity.SetTag(RaunTelemetry.Attributes.CodeFilePath, definition.SourceFile);
            activity.SetTag(RaunTelemetry.Attributes.CodeLineNumber, definition.SourceLine);
        }

        if (definition.Uses.Count > 0)
        {
            activity.SetTag(
                RaunTelemetry.Attributes.ScenarioUses,
                string.Join(",", definition.Uses.Select(u => $"{u.Resource.Name}:{u.Mode}")));
        }

        if (waited > TimeSpan.Zero)
        {
            // A wait under a millisecond truncates to 0, which reads as "did not wait": report at least 1.
            activity.SetTag(RaunTelemetry.Attributes.ScenarioWaitedMs, Math.Max(1L, (long)waited.TotalMilliseconds));
            activity.SetTag(RaunTelemetry.Attributes.ScenarioWaitedFor, waitedFor?.Name);
        }

        return activity;
    }

    /// <summary>The semconv <c>test.suite.run.status</c> of a scenario's outcome.</summary>
    private static string SuiteRunStatus(ScenarioOutcome outcome) => outcome.Status switch
    {
        ScenarioStatus.Passed => "success",
        ScenarioStatus.Failed => "failure",
        ScenarioStatus.TimedOut => "timed_out",
        ScenarioStatus.Canceled => "aborted",
        _ => "skipped",
    };

    /// <summary>
    /// The node indices a selector names within one scenario, or <see langword="null"/> for an
    /// unfiltered run. The scheduler expands these to their predecessor closure; the loop only says
    /// which steps were asked for.
    /// </summary>
    internal static IReadOnlySet<int>? SelectTargets(ScenarioDefinition definition, NodeSelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var targets = new HashSet<int>();
        foreach (var node in definition.Nodes)
        {
            if (selector.Matches(definition, node))
            {
                targets.Add(node.Index);
            }
        }

        return targets;
    }

    /// <summary>The clock scenario and step deadlines are measured on; a test replaces it.</summary>
    internal Func<long>? DeadlineClock { get; init; }

    // The provider comes in per scenario: it is the scenario's DI scope, not the root.
    private async Task<IReadOnlyList<StepResult>> DefaultRunScenario(
        ScenarioDefinition definition,
        IStepObserver observer,
        IServiceProvider? scenarioServices,
        IReadOnlySet<int>? targets,
        CancellationToken cancellationToken)
        => await new ScenarioScheduler(maxParallelism: maxParallelSteps, simulatedTime: simulateTime, cancellationGracePeriod: cancellationGracePeriod, cleanupTimeout: cleanupTimeout) { DeadlineClock = DeadlineClock }.RunAsync(
            definition,
            services: scenarioServices,
            observer: observer,
            cancellationToken: cancellationToken,
            targets: targets).ConfigureAwait(false);

    /// <summary>Republishes the scheduler's per-step callbacks onto the bus, tagged with the scenario.</summary>
    private sealed class BusObserver(ScenarioDefinition definition, IRunEventSink bus) : IStepObserver, IHostTerminationObserver
    {
        // The run ends here, with whatever was reported so far: the sinks get their RunFinished, so a
        // report is written, and the reason says why steps are missing from it.
        public Task OnHostTerminatingAsync(string reason)
            => bus.PublishAsync(new RunFinished { AbortReason = reason }).AsTask();

        public Task OnStepStartingAsync(StepContext context)
            => bus.PublishAsync(new StepStarted(definition, context)).AsTask();

        public Task OnStepFinishedAsync(StepResult result)
            => bus.PublishAsync(new StepFinished(definition, result)).AsTask();
    }
}
