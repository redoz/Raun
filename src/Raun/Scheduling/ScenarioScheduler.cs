using System.Diagnostics;
using Raun.Model;

namespace Raun.Scheduling;

/// <summary>
/// Executes a <see cref="ScenarioDefinition"/> as a DAG: a node runs only once all its
/// dependencies have passed; independent ready nodes run concurrently (bounded by an optional
/// max-parallelism). When a node fails, its transitive dependents are skipped with a summarizing
/// reason while independent branches continue. Per-step timeouts and scenario cancellation are
/// honored. The scheduler is runner-neutral; reporting flows through an optional
/// <see cref="IStepObserver"/>.
/// </summary>
public sealed class ScenarioScheduler
{
    /// <summary>Default real-time grace after cancellation: 60 seconds.</summary>
    public static TimeSpan DefaultCancellationGracePeriod => CancellationWatchdog.DefaultGrace;

    /// <summary>Default independent deadline for each cleanup callback: 60 seconds.</summary>
    public static TimeSpan DefaultCleanupTimeout => TimeSpan.FromSeconds(60);

    private readonly int _maxParallelism;
    private readonly TimeProvider _timeProvider;
    private readonly bool _simulatedTime;
    private readonly TimeSpan _cancellationGracePeriod;
    private readonly TimeSpan _cleanupTimeout;

    /// <param name="maxParallelism">Maximum steps running at once; 0 (default) means unbounded.</param>
    /// <param name="timeProvider">Clock for step <see cref="StepResult.StartedAt"/> stamps and step
    /// resource effects; defaults to <see cref="TimeProvider.System"/>. In simulated-time mode this is
    /// sampled once for the run's base instant; per-step clocks are derived from it.</param>
    /// <param name="simulatedTime">When true, drives a deterministic DAG-correct timeline: each step
    /// gets its own <see cref="SimulatedClock"/> seeded at <c>base + start offset</c>, where a node's
    /// start offset is the MAX of its dependencies' finish offsets (parallel siblings overlap; a join
    /// never starts at the SUM of its parallel deps). Step duration is whatever the step advanced its
    /// clock via <see cref="ScenarioContext.SimulateElapsed"/>. When false (the default) timing is
    /// real and byte-for-byte unchanged.</param>
    /// <param name="cancellationGracePeriod">Real time allowed after cancellation before terminating the test host; defaults to 60 seconds.</param>
    /// <param name="cleanupTimeout">Finite positive real-time deadline per cleanup callback; defaults to 60 seconds.</param>
    public ScenarioScheduler(
        int maxParallelism = 0,
        TimeProvider? timeProvider = null,
        bool simulatedTime = false,
        TimeSpan? cancellationGracePeriod = null,
        TimeSpan? cleanupTimeout = null)
    {
        _maxParallelism = maxParallelism;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _simulatedTime = simulatedTime;
        _cancellationGracePeriod = cancellationGracePeriod ?? CancellationWatchdog.DefaultGrace;
        CancellationWatchdog.ValidateGrace(_cancellationGracePeriod);
        _cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
        ValidateCleanupTimeout(_cleanupTimeout);
    }


    internal DeadlineClock? DeadlineClock { get; init; }

    internal static void ValidateCleanupTimeout(TimeSpan cleanupTimeout)
    {
        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout), cleanupTimeout, "Cleanup timeout must be positive and at most Int32.MaxValue milliseconds.");
        }
    }

    /// <summary>Skip reason recorded on steps a filtered run left out entirely.</summary>
    public const string NotSelectedSkipReason = "not selected";

    /// <summary>Skip reason recorded on steps that never ran because the scenario was canceled.</summary>
    public const string CanceledSkipReason = "scenario canceled";

    /// <summary>Skip reason recorded on steps that never ran because the scenario timed out. Distinct
    /// from <see cref="CanceledSkipReason"/> because a timeout is the suite's own fault, not the host's.</summary>
    public const string TimedOutSkipReason = "scenario timed out";

    /// <summary>Skip reason prefix recorded on steps left unresolved when the scheduler itself failed.</summary>
    private const string SchedulerFaultSkipReason = "scheduler failed";

    /// <param name="definition">The scenario graph to run.</param>
    /// <param name="services">Per-scenario service provider surfaced as <c>ctx.Services</c>.</param>
    /// <param name="observer">Receives step lifecycle callbacks; nothing is raised for steps a filter left out.</param>
    /// <param name="cancellationToken">Cancels the scenario; teardown still runs.</param>
    /// <param name="targets">
    /// When non-null, a filtered run: only these node indices and everything they transitively need
    /// (dependencies, merge sources, guard conditions) run, plus teardown. Every other node is left out
    /// entirely — no observer callback, so neither the runner nor the report ever sees it — and is
    /// recorded as <see cref="StepStatus.Skipped"/> with <see cref="NotSelectedSkipReason"/> so the
    /// returned list still has one result per node. Null runs the whole scenario.
    /// </param>
    public async Task<IReadOnlyList<StepResult>> RunAsync(
        ScenarioDefinition definition,
        IServiceProvider? services = null,
        IStepObserver? observer = null,
        IReadOnlySet<int>? targets = null,
        CancellationToken cancellationToken = default)
    {
        definition.Validate();

        // Asked directly, not through the serialized observer: the host is about to be terminated
        // because something does not return, and that something may hold the observer's turn.
        var beforeTermination = observer is IHostTerminationObserver last
            ? last.OnHostTerminatingAsync
            : (Func<string, Task>?)null;
        var serialized = observer is null ? null : new SerializedObserver(observer);
        observer = serialized;

        var nodes = definition.Nodes;
        var count = nodes.Count;
        var status = new StepStatus[count];          // defaults to Pending
        var results = new StepResult?[count];
        var outputs = new object?[count];
        var inputs = new StepInputs(outputs, status);
        var messageFormatting = ScenarioContext.CreateMessageFormattingContext(services, resourceMappings: definition.ResourceMappings).WithLabelMemory(nodes);
        var titleMessages = new StepMessage?[count];

        // The arm each passed condition node selected, decided once when it passed; -1 = no arm.
        var selectedArm = new int[nodes.Count];
        var capacity = _maxParallelism > 0 ? _maxParallelism : int.MaxValue;

        // The scenario-wide timeout from [Scenario(Timeout = …)]. Its token asks running steps to stop,
        // and the independent watchdog bounds draining before teardown and scope/resource release.
        // An uncooperative body terminates the host after cancellation grace, never becoming orphaned.
        // Real time even in simulated mode, exactly like a per-step timeout — a simulated run advances
        // its clock from step bodies and returns promptly, so a wall-clock timer cannot misfire on it.
        var scenarioTimeout = definition.Timeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : (TimeSpan?)null;
        var stepContexts = scenarioTimeout is null ? null : new ScenarioContext?[count];
        using var timeoutCts = new CancellationTokenSource();
        using var watchdog = new CancellationWatchdog(
            _cancellationGracePeriod, cancellationToken, DeadlineClock?.Timestamp, beforeTermination, DeadlineClock?.WatchdogLooked);
        var scenarioWatch = watchdog.Watch(timeoutCts, scenarioTimeout, $"scenario '{definition.DisplayName}'", isScenario: true);
        using var hostRegistration = cancellationToken.Register(() => scenarioWatch.RequestCancellation());
        if (cancellationToken.IsCancellationRequested) scenarioWatch.RequestCancellation();
        var scenarioToken = timeoutCts.Token;
        var timedOut = false;
        var launchedAt = new DateTimeOffset[count];

        var pending = new HashSet<int>(Enumerable.Range(0, count));
        var running = new Dictionary<Task<NodeOutcome>, int>();

        // The teardown node is not part of the DAG: it runs after every other node is terminal, so it
        // is removed from the pending set rather than scheduled.
        var teardownIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (nodes[i].IsTeardown)
            {
                teardownIndex = i;
                pending.Remove(i);
            }
        }

        // A filtered run keeps the targets' predecessor closure and drops the rest before scheduling
        // starts. Dropped nodes never enter the DAG loop, so nothing can depend on them (the closure
        // contains every predecessor of every kept node) and no observer ever hears of them.
        var excluded = new bool[count];
        if (targets is not null)
        {
            var included = ScenarioGraph.Closure(nodes, targets);
            for (var i = 0; i < count; i++)
            {
                if (i == teardownIndex || included.Contains(i))
                {
                    continue;
                }

                excluded[i] = true;
                pending.Remove(i);
                status[i] = StepStatus.Skipped;
                results[i] = new StepResult
                {
                    Node = nodes[i],
                    Status = StepStatus.Skipped,
                    StartedAt = _timeProvider.GetUtcNow(),
                    SkipReason = NotSelectedSkipReason,
                };
            }
        }

        var teardownLog = new TeardownLog(runs: teardownIndex >= 0);
        var ledger = new ResourceLedger(nodes);

        // Simulated mode samples the base instant once; per-node start/finish offsets from it compose
        // the timeline. Log offsets are measured from the scenario start held in scenarioStartRef:
        // known up front in simulated mode, learned from the first step to start in real mode, so no
        // extra clock read shifts any StartedAt.
        var scenarioStart = _simulatedTime ? _timeProvider.GetUtcNow() : default;
        var scenarioStartRef = new ScenarioStart();
        if (_simulatedTime)
        {
            scenarioStartRef.Of(scenarioStart);
        }
        var simStartOffset = _simulatedTime ? new TimeSpan[count] : null;
        var simFinishOffset = _simulatedTime ? new TimeSpan[count] : null;

        // A fault of the scheduler itself, as opposed to a step's: kept, like an observer's, and thrown
        // once the scenario has ended. Thrown where it happens it would leave with steps still
        // running, starts without their finish and every registered cleanup unrun.
        var faults = new List<Exception>();
        try
        {
            await ScheduleAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // whatever it was is thrown again, after the scenario
        catch (Exception ex)
#pragma warning restore CA1031
        {
            faults.Add(ex);
            await AbandonAsync(ex).ConfigureAwait(false);
        }

        await scenarioWatch.CompleteAsync().ConfigureAwait(false);
        watchdog.Dispose(); // teardown has its own deadlines

        if (teardownIndex >= 0)
        {
            try
            {
                await RunTeardownAsync(teardownIndex).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // joins the fault that came before it
            catch (Exception ex) when (faults.Count > 0)
#pragma warning restore CA1031
            {
                faults.Add(ex);
            }
        }

        if (serialized is not null)
        {
            faults.AddRange(serialized.Failures);
        }

        if (faults.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(faults[0]);
        }

        if (faults.Count > 1)
        {
            throw new AggregateException("The scenario did not run to its end cleanly.", faults);
        }

        // Filtered steps may reference values produced by selected predecessors.
        for (var i = 0; i < count; i++)
        {
            if (excluded[i])
            {
                var previous = results[i]!;
                var titleMessage = StepNotes.FormatTitle(nodes[i], messageFormatting, inputs);
                results[i] = new StepResult
                {
                    Node = previous.Node,
                    TitleMessage = titleMessage,
                    Status = previous.Status,
                    StartedAt = previous.StartedAt,
                    SkipReason = previous.SkipReason,
                    NoteMessages = StepNotes.FormatMessages(nodes[i], messageFormatting, inputs),
                };
            }
        }

        return results.Select(r => r!).ToList();

        // Ends a scenario the scheduler can no longer schedule: what is running is asked to stop and
        // waited for, and every node left without a result is skipped, so each start an observer was
        // given has its finish before teardown.
        async Task AbandonAsync(Exception fault)
        {
            scenarioWatch.RequestCancellation();
            while (running.Count > 0)
            {
                var finishedTask = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                var index = running[finishedTask];
                running.Remove(finishedTask);
                try
                {
                    await ApplyFinishedAsync(finishedTask, index).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // the step is skipped below, and the first fault is the one reported
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }

            for (var i = 0; i < count; i++)
            {
                if (i == teardownIndex || results[i] is not null)
                {
                    continue;
                }

                pending.Remove(i);
                status[i] = StepStatus.Skipped;
                var node = nodes[i];
                var titleMessage = titleMessages[i] ?? StepMessage.FromText(node.DisplayNameTemplate);
                var result = new StepResult
                {
                    Node = node,
                    TitleMessage = titleMessage,
                    Status = StepStatus.Skipped,
                    StartedAt = null, // the clock may be what failed
                    SkipReason = $"{SchedulerFaultSkipReason}: {fault.Message}",
                };
                results[i] = result;
                if (serialized is not null)
                {
                    if (!serialized.IsOpen(i))
                    {
                        await serialized.OnStepStartingAsync(
                            new StepContext { Node = node, DisplayName = titleMessage.Text }).ConfigureAwait(false);
                    }

                    await serialized.OnStepFinishedAsync(result).ConfigureAwait(false);
                }
            }
        }

        async Task ScheduleAsync()
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                // One reading of cancellation for the whole pass. It comes from another thread; read
                // again further down, it could say "cancelled" to the launch below after saying "not
                // cancelled" to the skips above, leaving a ready step neither skipped nor launched.
                // A step launched in the pass cancellation lands in is cancelled before its body runs.
                var cancelled = scenarioToken.IsCancellationRequested;
                timedOut |= scenarioWatch.TimedOut; // read after: a timeout is marked before it cancels
                var progressed = false;

                // 1. Resolve skips: cancellation, or all dependencies terminal with at least one bad.
                foreach (var i in pending.ToArray())
                {
                    if (cancelled)
                    {
                        // Capture cancelled-step evidence once, after in-flight producers have drained.
                        if (running.Count > 0) { continue; }
                        await ApplyTerminalAsync(
                            i, StepStatus.Skipped, timedOut ? TimedOutSkipReason : CanceledSkipReason).ConfigureAwait(false);
                        progressed = true;
                        continue;
                    }

                    var node = nodes[i];

                    var anyUnresolved = false;
                    List<string>? failed = null;
                    List<string>? skipped = null;
                    List<string>? notTaken = null;

                    foreach (var dep in node.DependsOn)
                    {
                        switch (status[dep])
                        {
                            case StepStatus.Pending:
                            case StepStatus.Running:
                                anyUnresolved = true;
                                break;
                            case StepStatus.Failed:
                                (failed ??= []).Add(nodes[dep].OperationName);
                                break;
                            case StepStatus.Skipped:
                                (skipped ??= []).Add(nodes[dep].OperationName);
                                break;
                            case StepStatus.NotTaken:
                                (notTaken ??= []).Add(nodes[dep].OperationName);
                                break;
                            case StepStatus.Passed:
                                break;
                            default:
                                throw new UnreachableException($"Unhandled step status {status[dep]}.");
                        }
                    }

                    // 1a. Waits-for: ordering only (see ScenarioNode.WaitsFor).
                    foreach (var wait in node.WaitsFor)
                    {
                        switch (status[wait])
                        {
                            case StepStatus.Pending:
                            case StepStatus.Running:
                                anyUnresolved = true;
                                break;
                            case StepStatus.Failed:
                                (failed ??= []).Add(nodes[wait].OperationName);
                                break;
                            case StepStatus.Skipped:
                                (skipped ??= []).Add(nodes[wait].OperationName);
                                break;
                            case StepStatus.Passed:
                            case StepStatus.NotTaken:
                                break;
                            default:
                                throw new UnreachableException($"Unhandled step status {status[wait]}.");
                        }
                    }

                    // 1b. Guards. These apply to EVERY node, merge and pass-through nodes included — a
                    // pass-through is guarded on the arm it stands in for, so resolving its sources
                    // without first checking its guard would let the parent value through on both sides. A resolved-false guard is a decision (NotTaken); a guard whose condition
                    // failed/was skipped/was itself not taken is a cascade (Skipped) — no branch was chosen.
                    var guardNotTaken = (string?)null;
                    foreach (var guard in node.Guards)
                    {
                        switch (status[guard.ConditionIndex])
                        {
                            case StepStatus.Pending:
                            case StepStatus.Running:
                                anyUnresolved = true;
                                break;
                            case StepStatus.Passed:
                                if (selectedArm[guard.ConditionIndex] != guard.Arm)
                                {
                                    guardNotTaken ??= NotTakenReason(nodes[guard.ConditionIndex], selectedArm[guard.ConditionIndex]);
                                }

                                break;
                            case StepStatus.NotTaken:
                                // The condition itself sat in a branch that was not chosen, so it never
                                // ran. Still a decision, not a failure — cascading to Skipped here would
                                // report an untaken nested branch as though something had gone wrong.
                                (notTaken ??= []).Add(nodes[guard.ConditionIndex].OperationName);
                                break;
                            case StepStatus.Failed:
                            case StepStatus.Skipped:
                                (skipped ??= []).Add(nodes[guard.ConditionIndex].OperationName);
                                break;
                            default:
                                throw new UnreachableException($"Unhandled step status {status[guard.ConditionIndex]}.");
                        }
                    }

                    if (anyUnresolved)
                    {
                        continue;
                    }

                    if (failed is not null || skipped is not null)
                    {
                        await ApplyTerminalAsync(i, StepStatus.Skipped, BuildSkipReason(failed, skipped)).ConfigureAwait(false);
                        progressed = true;
                    }
                    else if (guardNotTaken is not null)
                    {
                        await ApplyTerminalAsync(i, StepStatus.NotTaken, guardNotTaken).ConfigureAwait(false);
                        progressed = true;
                    }
                    else if (notTaken is not null)
                    {
                        await ApplyTerminalAsync(
                            i, StepStatus.NotTaken, $"not taken: {string.Join(", ", notTaken)}").ConfigureAwait(false);
                        progressed = true;
                    }
                    else if (node.MergeSources.Count > 0)
                    {
                        // Guards hold; now the any-of over mutually exclusive sources.
                        if (TryResolveMerge(node, out var mergeStatus, out var mergeReason, out var mergeOutput))
                        {
                            if (mergeStatus == StepStatus.Passed)
                            {
                                pending.Remove(i);
                                outputs[i] = mergeOutput;
                                status[i] = StepStatus.Passed;
                                await ApplyMergePassAsync(i).ConfigureAwait(false);
                            }
                            else
                            {
                                await ApplyTerminalAsync(i, mergeStatus, mergeReason!).ConfigureAwait(false);
                            }

                            progressed = true;
                        }
                    }
                }

                // 2. Launch ready nodes (all dependencies passed), bounded by capacity.
                if (!cancelled)
                {
                    foreach (var i in pending.ToArray())
                    {
                        if (running.Count >= capacity)
                        {
                            break;
                        }

                        var node = nodes[i];
                        if (node.MergeSources.Count > 0)
                        {
                            continue; // resolved in phase 1, never invoked
                        }

                        if (node.DependsOn.All(d => status[d] == StepStatus.Passed)
                            && node.WaitsFor.All(w => status[w] is StepStatus.Passed or StepStatus.NotTaken)
                            && node.Guards.All(g => status[g.ConditionIndex] == StepStatus.Passed
                                && selectedArm[g.ConditionIndex] == g.Arm))
                        {
                            pending.Remove(i);
                            status[i] = StepStatus.Running;
                            var titleMessage = node.PrepareArguments is null
                                ? StepNotes.FormatTitle(node, messageFormatting, inputs)
                                : StepMessage.FromText(node.DisplayNameTemplate);
                            titleMessages[i] = titleMessage;
                            var displayName = titleMessage.Text;
                            if (observer is not null && node.PrepareArguments is null)
                            {
                                await observer.OnStepStartingAsync(
                                    new StepContext { Node = node, DisplayName = displayName })
                                    .ConfigureAwait(false);
                            }

                            var startOffset = TimeSpan.Zero;
                            if (_simulatedTime)
                            {
                                startOffset = StartOffset(node, simFinishOffset!);
                                simStartOffset![i] = startOffset;
                            }

                            if (scenarioTimeout is not null)
                            {
                                launchedAt[i] = _timeProvider.GetUtcNow(); // only read the clock when a timeout can need it
                            }

                            // Every launched operation drains before watchdog disposal, including exceptional exits.
#pragma warning disable CA2025 // The watchdog also retains ownership in Dispose until registered operations finish.
                            running[RunNodeAsync(
                                definition, node, inputs, services, titleMessage, messageFormatting, scenarioStart, startOffset, teardownLog, ledger, scenarioStartRef, stepContexts, observer, titleMessages, watchdog, scenarioToken)] = i;
#pragma warning restore CA2025
                            progressed = true;
                        }
                    }
                }

                // 3. Wait for a running step, or guard against a stuck schedule.
                if (running.Count == 0)
                {
                    if (pending.Count == 0)
                    {
                        break;
                    }

                    if (!progressed)
                    {
                        throw new InvalidOperationException(
                            "Scenario scheduler stalled with unresolved steps; this indicates a graph defect.");
                    }

                    continue;
                }

                // The scenario deadline has one owner, the watchdog: when it passes, the watchdog cancels
                // the scenario token and every step still running, and each of those returns here marked
                // as cancelled. A step that finished before that moment is not marked, and keeps its result.
                var finishedTask = await Task.WhenAny(running.Keys).ConfigureAwait(false);
                var index = running[finishedTask];
                running.Remove(finishedTask);
                await ApplyFinishedAsync(finishedTask, index).ConfigureAwait(false);
            }
        }

        // Teardown is not a node of the graph: it runs here, after the scheduling loop, and its
        // cleanups are run by TeardownRunner. This is only the scheduler's side of it: where it
        // stands on the timeline, and telling the observer.
        async Task RunTeardownAsync(int i)
        {
            var node = nodes[i];
            var titleMessage = StepNotes.FormatTitle(node, messageFormatting, inputs);

            // Success is a property of the SCENARIO, not of the step that registered a cleanup: a
            // failed run should leave the whole world intact, not a half-torn-down mix of it. Steps a
            // filter left out did not fail — they were never asked to run — so they do not count.
            var succeeded = ScenarioOutcome.Of(results.Where((r, n) => r is not null && n != i)!).Succeeded;

            // Reverse topological order of the owning step, then reverse registration order within a
            // step. Registration order alone is nondeterministic: steps run concurrently.
            var ordered = teardownLog.Entries
                .OrderByDescending(e => e.OwningStepIndex)
                .ThenByDescending(e => e.Sequence)
                .ToList();

            var startedAt = _timeProvider.GetUtcNow();
            if (_simulatedTime)
            {
                // On the simulated timeline teardown starts when the step that ends last has ended.
                var offset = TimeSpan.Zero;
                for (var n = 0; n < count; n++)
                {
                    if (n != i && simFinishOffset![n] > offset)
                    {
                        offset = simFinishOffset[n];
                    }
                }

                simStartOffset![i] = offset;
                simFinishOffset![i] = offset;
                startedAt = scenarioStart + offset;
            }

            if (observer is not null)
            {
                await observer.OnStepStartingAsync(
                    new StepContext { Node = node, DisplayName = titleMessage.Text }).ConfigureAwait(false);
            }

            var result = await new TeardownRunner(_timeProvider, _cancellationGracePeriod, _cleanupTimeout)
                .RunAsync(new TeardownRunner.Request(
                    definition, node, titleMessage, ordered, succeeded, startedAt, _simulatedTime, services,
                    messageFormatting, inputs, scenarioStartRef, beforeTermination))
                .ConfigureAwait(false);
            status[i] = result.Status;
            results[i] = result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(result).ConfigureAwait(false);
            }
        }

        // Records a step task that ran to completion: its status, its output, and its result.
        async Task ApplyFinishedAsync(Task<NodeOutcome> task, int i)
        {
            var outcome = await task.ConfigureAwait(false); // RunNodeAsync never throws
            if (scenarioWatch.TimedOut && outcome.Canceled && !outcome.StepTimedOut)
            {
                timedOut = true;
                await ApplyTimedOutAsync(i, scenarioTimeout!.Value).ConfigureAwait(false);
                return;
            }

            var result = outcome.Result;
            status[i] = result.Status;
            if (result.Status == StepStatus.Passed)
            {
                outputs[i] = outcome.Output;
                if (nodes[i].SelectArm is { } select)
                {
                    // The branch decision is part of the condition step: a when-clause that throws
                    // fails the step that holds it, never the scheduler, and its arms cascade as for
                    // any failed dependency.
                    try
                    {
                        var arm = select(inputs);
                        if (arm < Guard.NoArm || arm >= nodes[i].Arms.Count)
                        {
                            throw new InvalidOperationException(
                                $"'{nodes[i].OperationName}' chose arm {arm}, but it has {nodes[i].Arms.Count} arm(s) "
                                + $"(and {Guard.NoArm} for matching none).");
                        }

                        selectedArm[i] = arm;
                    }
                    catch (Exception ex)
                    {
                        status[i] = StepStatus.Failed;
                        var initialTitle = titleMessages[i]!;
                        result = new StepResult
                        {
                            Node = result.Node,
                            TitleMessage = initialTitle,
                            Status = StepStatus.Failed,
                            StartedAt = result.StartedAt,
                            Duration = result.Duration,
                            Exception = new InvalidOperationException(
                                $"Choosing a branch after '{nodes[i].OperationName}' threw.", ex),
                            NoteMessages = result.NoteMessages,
                            LogEntries = result.LogEntries,
                            Attachments = result.Attachments,
                            Effects = result.Effects,
                            Links = result.Links,
                            TraceId = result.TraceId,
                            SpanId = result.SpanId,
                        };
                    }
                }
            }

            if (_simulatedTime)
            {
                simFinishOffset![i] = simStartOffset![i] + result.Duration;
            }

            results[i] = result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(result).ConfigureAwait(false);
            }
        }

        // Reports a step the scenario timeout cut short. Failed, not skipped: the scenario ran out of
        // time while this step held it, and a run that times out has to come back red.
        async Task ApplyTimedOutAsync(int i, TimeSpan timeout)
        {
            var node = nodes[i];
            var titleMessage = titleMessages[i]!;
            var context = stepContexts?[i];
            var name = titleMessage.Text;
            status[i] = StepStatus.Failed;

            var startedAt = _simulatedTime ? scenarioStart + simStartOffset![i] : launchedAt[i];
            var duration = _simulatedTime ? TimeSpan.Zero : _timeProvider.GetUtcNow() - startedAt;
            if (_simulatedTime)
            {
                simFinishOffset![i] = simStartOffset![i];
            }

            var result = new StepResult
            {
                Node = node,
                TitleMessage = titleMessage,
                Status = StepStatus.Failed,
                StartedAt = startedAt,
                Duration = duration,
                NoteMessages = context?.NoteMessages ?? StepNotes.FormatMessages(node, messageFormatting, inputs),
                LogEntries = context?.LogEntries ?? [],
                Attachments = context?.Attachments ?? new Dictionary<string, string>(),
                Effects = context?.Resources.Effects ?? [],
                Links = context?.Resources.Links ?? [],
                TraceId = context?.Activity?.TraceId.ToString(),
                SpanId = context?.Activity?.SpanId.ToString(),
                TimedOut = true,
                Exception = new TimeoutException(
                    $"Scenario '{definition.DisplayName}' exceeded its timeout of {timeout} while running step '{name}'."),
            };

            results[i] = result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(result).ConfigureAwait(false);
            }
        }

        async Task ApplyTerminalAsync(int i, StepStatus terminal, string reason)
        {
            pending.Remove(i);
            status[i] = terminal;
            var node = nodes[i];
            var titleMessage = StepNotes.FormatTitle(node, messageFormatting, inputs);
            var name = titleMessage.Text;

            // A not-taken branch never started, so no observer sees it start — that is what lets a
            // reporter leave the node in its discovered state instead of stranding it "in progress".
            if (observer is not null && terminal != StepStatus.NotTaken)
            {
                await observer.OnStepStartingAsync(
                    new StepContext { Node = node, DisplayName = name })
                    .ConfigureAwait(false);
            }

            var startedAt = _timeProvider.GetUtcNow();
            if (_simulatedTime)
            {
                var startOffset = StartOffset(node, simFinishOffset!);
                simStartOffset![i] = startOffset;
                simFinishOffset![i] = startOffset; // skipped and not-taken steps have zero duration
                startedAt = scenarioStart + startOffset;
            }

            var result = new StepResult
            {
                Node = node,
                TitleMessage = titleMessage,
                Status = terminal,
                StartedAt = startedAt,
                SkipReason = reason,
                NoteMessages = StepNotes.FormatMessages(node, messageFormatting, inputs),
            };

            // A step that never ran gets no span of its own — a span for no work is noise in a trace
            // viewer — but the scenario span (ambient here when the run loop started one) records it.
            if (Activity.Current is { IsAllDataRequested: true } scenarioActivity)
            {
                scenarioActivity.AddEvent(new ActivityEvent(
                    RaunTelemetry.Events.StepSkipped,
                    tags: new ActivityTagsCollection
                    {
                        ["step"] = name,
                        [RaunTelemetry.Attributes.Step] = node.StepId,
                        ["status"] = terminal.ToString(),
                        ["reason"] = reason,
                    }));
            }

            results[i] = result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(result).ConfigureAwait(false);
            }
        }

        // A merge resolves once ALL its sources are terminal: exactly one Passed => pass with that
        // source's output; every source NotTaken => NotTaken; any Failed/Skipped => Skipped, cascading.
        bool TryResolveMerge(ScenarioNode node, out StepStatus resolved, out string? reason, out object? output)
        {
            resolved = StepStatus.Passed;
            reason = null;
            output = null;
            var passedIndex = -1;
            List<string>? bad = null;

            foreach (var source in node.MergeSources)
            {
                switch (status[source])
                {
                    case StepStatus.Pending:
                    case StepStatus.Running:
                        return false;
                    case StepStatus.Passed:
                        passedIndex = source;
                        break;
                    case StepStatus.NotTaken:
                        break;
                    case StepStatus.Failed:
                    case StepStatus.Skipped:
                        (bad ??= []).Add(nodes[source].OperationName);
                        break;
                    default:
                        throw new UnreachableException($"Unhandled step status {status[source]}.");
                }
            }

            if (bad is not null)
            {
                resolved = StepStatus.Skipped;
                reason = $"dependency failed: {string.Join(", ", bad)}";
                return true;
            }

            if (passedIndex < 0)
            {
                resolved = StepStatus.NotTaken;
                reason = "not taken: no branch produced a value";
                return true;
            }

            output = outputs[passedIndex];
            return true;
        }

        async Task ApplyMergePassAsync(int i)
        {
            var node = nodes[i];
            var titleMessage = StepNotes.FormatTitle(node, messageFormatting, inputs);
            var name = titleMessage.Text;
            var startedAt = _timeProvider.GetUtcNow();
            if (_simulatedTime)
            {
                var startOffset = MergeStartOffset(node, simFinishOffset!);
                simStartOffset![i] = startOffset;
                simFinishOffset![i] = startOffset; // a merge is instantaneous
                startedAt = scenarioStart + startOffset;
            }

            var result = new StepResult
            {
                Node = node,
                TitleMessage = titleMessage,
                Status = StepStatus.Passed,
                StartedAt = startedAt,
            };
            results[i] = result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(result).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why a guarded step did not run: the arm its condition took instead, or that none
    /// matched.</summary>
    private static string NotTakenReason(ScenarioNode condition, int arm)
        => arm < 0
            ? $"not taken: {condition.OperationName} matched no arm"
            : $"not taken: {condition.OperationName} took {condition.Arms[arm]}";

    /// <summary>A merge's simulated start offset: the MAX of its sources' finish offsets (it has no
    /// DependsOn edges of its own).</summary>
    private static TimeSpan MergeStartOffset(ScenarioNode node, TimeSpan[] finishOffsets)
    {
        var offset = TimeSpan.Zero;
        foreach (var source in node.MergeSources)
        {
            if (finishOffsets[source] > offset)
            {
                offset = finishOffsets[source];
            }
        }

        return offset;
    }

    private async Task<NodeOutcome> RunNodeAsync(
        ScenarioDefinition definition,
        ScenarioNode node,
        IStepInputs inputs,
        IServiceProvider? services,
        StepMessage titleMessage,
        MessageFormattingContext messageFormatting,
        DateTimeOffset scenarioStart,
        TimeSpan simStartOffset,
        TeardownLog teardownLog,
        ResourceLedger ledger,
        ScenarioStart scenarioStartRef,
        ScenarioContext?[]? stepContexts,
        IStepObserver? observer,
        StepMessage?[] titleMessages,
        CancellationWatchdog watchdog,
        CancellationToken scenarioToken)
    {
        // In simulated mode each step runs on its own clock seeded at base + start offset, so the
        // step's timing AND its resource-effect timestamps share one consistent timeline. Duration is
        // whatever the body advanced that clock. In real mode the step clock is the injected provider
        // and duration comes from the stopwatch.
        var simClock = _simulatedTime ? new SimulatedClock(scenarioStart + simStartOffset) : null;
        var stepTimeProvider = simClock ?? _timeProvider;
        var startedAt = simClock is not null ? scenarioStart + simStartOffset : _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        TimeSpan Elapsed() => simClock?.Advanced ?? stopwatch.Elapsed;
        using var stepCts = new CancellationTokenSource();
        using var operationWatch = watchdog.WatchBeforeItsTimeoutStarts(
            stepCts, node.Timeout, $"step '{node.DisplayNameTemplate}' in scenario '{definition.DisplayName}'");
        var displayName = titleMessage.Text;
        var context = new ScenarioContext(
            node.StepId, displayName, services, messageFormatting.ForStep(node.Index), stepTimeProvider, stepCts.Token);

        if (stepContexts is not null)
        {
            stepContexts[node.Index] = context;
        }

        void FormatterFailed(Exception ex)
            => context.Log($"A title or note formatter threw {ex.GetType().Name}: {ex.Message}");

        foreach (var note in StepNotes.FormatMessages(node, messageFormatting, inputs, FormatterFailed))
        {
            context.AddNote(note);
        }

        context.AttachArgumentTitleObserver(title => titleMessages[node.Index] = title);
        context.AttachScenario(definition.Info);
        context.AttachTeardown(teardownLog, node.Index);
        context.AttachLedger(ledger, node.Index);
        context.AttachScenarioStart(scenarioStartRef.Of(startedAt));

        // The step's span: a child of whatever is ambient (the scenario span when the run loop started
        // one), and Activity.Current for the body, so outgoing HTTP carries its traceparent. Null when
        // nothing listens. Real time, not the simulated clock — a trace viewer wants wall time.
        using var activity = StartStepActivity(definition, node, displayName);
        context.AttachActivity(activity);

        // Leave the scheduler's loop before anything the scenario wrote runs. An async method runs on
        // its caller until its first incomplete await, so without this a step that blocks or computes
        // before awaiting (or never awaits) would hold the loop: its siblings could not start beside
        // it, and the loop could not see the scenario deadline pass.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        // Not at the launch: the wait for a thread is not the step's time.
        operationWatch.StartTimeout();

        // Ambient for the duration of this step. Set inside RunNodeAsync (which each step enters on
        // its own async flow) so it is visible to the step body and to anything it awaits, but never
        // to the scheduler loop or to a sibling step.
        ScenarioContext.SetCurrent(context);

        try
        {
            Exception? preparationFailure = null;
            if (node.PrepareArguments is { } prepare)
            {
                try
                {
                    scenarioToken.ThrowIfCancellationRequested();
                    inputs = new PreparedInputs(inputs, prepare(inputs));
                    titleMessage = StepNotes.FormatTitle(node, messageFormatting, inputs, FormatterFailed);
                    titleMessages[node.Index] = titleMessage;
                    displayName = titleMessage.Text;
                    context.SetDisplayName(displayName);
                    if (activity is not null)
                    {
                        activity.DisplayName = displayName;
                        activity.SetTag(RaunTelemetry.Attributes.TestCaseName, displayName);
                    }
                }
                catch (Exception ex)
                {
                    preparationFailure = ex;
                }
            }
            if (observer is not null && node.PrepareArguments is not null)
            {
                await observer.OnStepStartingAsync(new StepContext { Node = node, DisplayName = displayName })
                    .ConfigureAwait(false);
            }
            if (preparationFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(preparationFailure);
            }
            object? output;
            try
            {
                stepCts.Token.ThrowIfCancellationRequested();
                output = await node.Invoke!(inputs, context).ConfigureAwait(false); // Validate: every kind that runs has one
            }
            finally
            {
                await operationWatch.CompleteAsync().ConfigureAwait(false);
            }
            if (!operationWatch.TimedOut && operationWatch.CancellationStarted && scenarioToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(scenarioToken);
            }
            if (operationWatch.TimedOut)
            {
                throw new TimeoutException($"Step '{displayName}' exceeded its timeout of {node.Timeout}.");
            }

            stopwatch.Stop();
            activity?.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "pass");
            titleMessage = context.ArgumentTitleMessage ?? titleMessage;
            var completedTitle = StepNotes.FormatCompletedTitle(node, titleMessage, output, messageFormatting, FormatterFailed);
            return new NodeOutcome(
                new StepResult
                {
                    Node = node,
                    TitleMessage = completedTitle,
                    Status = StepStatus.Passed,
                    StartedAt = startedAt,
                    Duration = Elapsed(),
                    NoteMessages = context.NoteMessages,
                    LogEntries = context.LogEntries,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
                    Links = context.Resources.Links,
                    TraceId = activity?.TraceId.ToString(),
                    SpanId = activity?.SpanId.ToString(),
                },
                output, operationWatch.CancellationStarted, operationWatch.TimedOut);
        }
        catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested && !operationWatch.TimedOut)
        {
            stopwatch.Stop();
            activity?.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "skipped");
            return Outcome(StepStatus.Skipped, skipReason: CanceledSkipReason);
        }
        catch (Exception ex)
        {
            if (operationWatch.TimedOut)
            {
                ex = new TimeoutException($"Step '{displayName}' exceeded its timeout of {node.Timeout}.", ex);
            }
            stopwatch.Stop();
            if (activity is not null)
            {
                activity.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "fail");
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }

            return Outcome(StepStatus.Failed, exception: ex);
        }

        finally
        {
            await operationWatch.CompleteAsync().ConfigureAwait(false);
        }

        NodeOutcome Outcome(StepStatus statusValue, Exception? exception = null, string? skipReason = null)
            => new(
                new StepResult
                {
                    Node = node,
                    TitleMessage = context.ArgumentTitleMessage ?? titleMessage,
                    Status = statusValue,
                    StartedAt = startedAt,
                    Duration = Elapsed(),
                    Exception = exception,
                    TimedOut = statusValue == StepStatus.Failed && operationWatch.TimedOut,
                    SkipReason = skipReason,
                    NoteMessages = context.NoteMessages,
                    LogEntries = context.LogEntries,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
                    Links = context.Resources.Links,
                    TraceId = activity?.TraceId.ToString(),
                    SpanId = activity?.SpanId.ToString(),
                },
                null, operationWatch.CancellationStarted, operationWatch.TimedOut);
    }

    /// <summary>Starts a step's span with its identity tags, or returns null when nothing listens.</summary>
    internal static Activity? StartStepActivity(ScenarioDefinition definition, ScenarioNode node, string displayName)
    {
        var activity = RaunTelemetry.Source.StartActivity(displayName, ActivityKind.Internal);
        if (activity is null || !activity.IsAllDataRequested)
        {
            return activity;
        }

        activity.SetTag(RaunTelemetry.Attributes.TestSuiteName, definition.DisplayName);
        activity.SetTag(RaunTelemetry.Attributes.TestCaseName, displayName);
        activity.SetTag(RaunTelemetry.Attributes.Scenario, definition.ScenarioId);
        activity.SetTag(RaunTelemetry.Attributes.Step, node.StepId);
        activity.SetTag(RaunTelemetry.Attributes.StepPhase, node.Phase);
        activity.SetTag(RaunTelemetry.Attributes.StepOperation, node.OperationName);
        if (!string.IsNullOrEmpty(node.SourceFile) && node.SourceLine > 0)
        {
            activity.SetTag(RaunTelemetry.Attributes.CodeFilePath, node.SourceFile);
            activity.SetTag(RaunTelemetry.Attributes.CodeLineNumber, node.SourceLine);
        }

        return activity;
    }

    /// <summary>
    /// A node's simulated start offset (from the run's base instant): zero for a root, otherwise the
    /// MAX of its dependencies' finish offsets. Using the max means parallel siblings overlap and a
    /// join starts when its last parallel dependency finished — never at their sum.
    /// </summary>
    private static TimeSpan StartOffset(ScenarioNode node, TimeSpan[] finishOffsets)
    {
        var offset = TimeSpan.Zero;
        foreach (var dep in node.DependsOn.Concat(node.WaitsFor))
        {
            if (finishOffsets[dep] > offset)
            {
                offset = finishOffsets[dep];
            }
        }

        return offset;
    }

    private static string BuildSkipReason(List<string>? failed, List<string>? skipped)
    {
        if (failed is not null && skipped is not null)
        {
            return $"dependency failed: {string.Join(", ", failed)}; "
                + $"dependency skipped: {string.Join(", ", skipped)}";
        }

        return failed is not null
            ? $"dependency failed: {string.Join(", ", failed)}"
            : $"dependency skipped: {string.Join(", ", skipped!)}";
    }

    /// <summary>The instant a scenario's log offsets are measured from: fixed up front in simulated mode,
    /// otherwise the first step's StartedAt. Thread-safe: concurrent first steps agree on one value.</summary>
    internal sealed class ScenarioStart
    {
        private long _ticks = -1;

        public DateTimeOffset Of(DateTimeOffset candidate)
        {
            Interlocked.CompareExchange(ref _ticks, candidate.UtcTicks, -1);
            return new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        }
    }

    private readonly record struct NodeOutcome(StepResult Result, object? Output, bool Canceled = false, bool StepTimedOut = false);

    // Prepared nodes can yield during starting callbacks. Serialize starts and finishes together,
    // preserving IStepObserver's contract without holding any scenario-global execution lock.
    //
    // An observer reports the scenario; it is not part of it. A callback that throws is kept here and
    // thrown once the scenario has ended, teardown included: thrown where it happens, it would leave
    // through the scheduler's loop with steps still running and every registered cleanup unrun.
    private sealed class SerializedObserver(IStepObserver inner) : IStepObserver
    {
        private readonly Turns _turns = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _failures = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _open = new();

        public Task OnStepStartingAsync(StepContext context)
        {
            _open[context.Node.Index] = true;
            return Tell(() => inner.OnStepStartingAsync(context));
        }

        public Task OnStepFinishedAsync(StepResult result)
        {
            _open.TryRemove(result.Node.Index, out _);
            return Tell(() => inner.OnStepFinishedAsync(result));
        }

        /// <summary>Whether the node was given a start that has not had its finish yet.</summary>
        public bool IsOpen(int node) => _open.ContainsKey(node);

        /// <summary>What the observer threw during the scenario, if it did.</summary>
        public IReadOnlyCollection<Exception> Failures => _failures.ToArray();

        private async Task Tell(Func<Task> callback)
        {
            try
            {
                await _turns.TakeAsync(callback).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // whatever it threw is thrown again, after the scenario
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _failures.Enqueue(ex);
            }
        }
    }

    private sealed class PreparedInputs(IStepInputs producers, object?[] arguments) : IStepInputs
    {
        public T Get<T>(int producerIndex) => producers.Get<T>(producerIndex);
        public T GetArgument<T>(int argumentIndex, Func<T>? unavailable = null, bool allowUnavailable = false) => (T)arguments[argumentIndex]!;
    }

    /// <summary>
    /// The producer outputs a step's body reads. A slot is readable only once its node has passed;
    /// reading it earlier means the reader does not depend on the producer, which only a generator
    /// bug can arrange, and a null cast would let that pass silently (or surface as a bare
    /// NullReferenceException for a value type). Display-name formatting for a skipped step may
    /// legitimately hit this and is caught by <see cref="StepNotes.FormatTitle"/>.
    /// </summary>
    private sealed class StepInputs(object?[] outputs, StepStatus[] status) : IStepInputs
    {
        public T Get<T>(int producerIndex)
        {
            if (status[producerIndex] != StepStatus.Passed)
            {
                throw new StepInputUnavailableException(
                    $"Step input {producerIndex} was read before that node produced a value: the reading step does not depend on it.");
            }

            return (T)outputs[producerIndex]!;
        }
    }
}
