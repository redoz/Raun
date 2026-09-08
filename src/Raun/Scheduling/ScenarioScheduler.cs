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
    private readonly int _maxParallelism;
    private readonly TimeProvider _timeProvider;
    private readonly bool _simulatedTime;

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
    public ScenarioScheduler(
        int maxParallelism = 0,
        TimeProvider? timeProvider = null,
        bool simulatedTime = false)
    {
        _maxParallelism = maxParallelism;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _simulatedTime = simulatedTime;
    }


    /// <summary>Skip reason recorded on steps a filtered run left out entirely.</summary>
    public const string NotSelectedSkipReason = "not selected";

    /// <summary>Skip reason recorded on steps that never ran because the scenario was canceled.</summary>
    public const string CanceledSkipReason = "scenario canceled";

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

        var nodes = definition.Nodes;
        var count = nodes.Count;
        var status = new StepStatus[count];          // defaults to Pending
        var results = new StepResult?[count];
        var outputs = new object?[count];
        var inputs = new StepInputs(outputs);
        var capacity = _maxParallelism > 0 ? _maxParallelism : int.MaxValue;

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
                    DisplayName = nodes[i].DisplayNameTemplate,
                    Status = StepStatus.Skipped,
                    StartedAt = _timeProvider.GetUtcNow(),
                    SkipReason = NotSelectedSkipReason,
                };
            }
        }

        var teardownLog = new TeardownLog();
        var ledger = new ResourceLedger(nodes);

        // Simulated-time bookkeeping. The base instant is sampled once; per-node start/finish offsets
        // (TimeSpan from base) compose the DAG-correct timeline. Unused in real mode.
        // The simulated base instant (simulated mode only). Log offsets are measured from the scenario
        // start held in scenarioStartRef: known up front in simulated mode, learned from the first step
        // to start in real mode, so no extra clock read shifts any StartedAt.
        var scenarioStart = _simulatedTime ? _timeProvider.GetUtcNow() : default;
        var scenarioStartRef = new ScenarioStart();
        if (_simulatedTime)
        {
            scenarioStartRef.Of(scenarioStart);
        }
        var simStartOffset = _simulatedTime ? new TimeSpan[count] : null;
        var simFinishOffset = _simulatedTime ? new TimeSpan[count] : null;

        while (pending.Count > 0 || running.Count > 0)
        {
            var progressed = false;

            // 1. Resolve skips: cancellation, or all dependencies terminal with at least one bad.
            foreach (var i in pending.ToArray())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await ApplyTerminalAsync(i, StepStatus.Skipped, CanceledSkipReason).ConfigureAwait(false);
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
                    }
                }

                // 1a. Waits-for: ordering only. A not-taken predecessor is a decision that does not
                // concern this node (the arm was simply not chosen, and this node sits after the if),
                // so it neither blocks nor cascades; failed/skipped cascade exactly as for DependsOn.
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
                            if (EvaluateGuard(nodes[guard.ConditionIndex], outputs[guard.ConditionIndex]) != guard.WhenValue)
                            {
                                guardNotTaken ??= nodes[guard.ConditionIndex].OperationName;
                            }

                            break;
                        case StepStatus.NotTaken:
                            // The condition itself sat in a branch that was not chosen, so it never
                            // ran. Still a decision, not a failure — cascading to Skipped here would
                            // report an untaken nested branch as though something had gone wrong.
                            (notTaken ??= []).Add(nodes[guard.ConditionIndex].OperationName);
                            break;
                        default: // Failed / Skipped
                            (skipped ??= []).Add(nodes[guard.ConditionIndex].OperationName);
                            break;
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
                    await ApplyTerminalAsync(i, StepStatus.NotTaken, $"not taken: {guardNotTaken}").ConfigureAwait(false);
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
            if (!cancellationToken.IsCancellationRequested)
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
                            && EvaluateGuard(nodes[g.ConditionIndex], outputs[g.ConditionIndex]) == g.WhenValue))
                    {
                        pending.Remove(i);
                        status[i] = StepStatus.Running;
                        var displayName = FormatName(node, inputs);
                        if (observer is not null)
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

                        running[RunNodeAsync(
                            definition, node, inputs, services, displayName, scenarioStart, startOffset, teardownLog, ledger, scenarioStartRef, cancellationToken)] = i;
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

            var finishedTask = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var index = running[finishedTask];
            running.Remove(finishedTask);
            var outcome = await finishedTask.ConfigureAwait(false); // RunNodeAsync never throws

            status[index] = outcome.Result.Status;
            if (outcome.Result.Status == StepStatus.Passed)
            {
                outputs[index] = outcome.Output;
            }

            if (_simulatedTime)
            {
                simFinishOffset![index] = simStartOffset![index] + outcome.Result.Duration;
            }

            results[index] = outcome.Result;
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(outcome.Result).ConfigureAwait(false);
            }
        }

        if (teardownIndex >= 0)
        {
            await RunTeardownAsync(teardownIndex).ConfigureAwait(false);
        }

        return results.Select(r => r!).ToList();

        // Runs after the scheduling loop, so a cancelled scenario token has already stopped the DAG
        // and cannot suppress the cleanups that exist to prevent leaks. Cleanup delegates are invoked
        // directly, never through a step's cancellation source.
        async Task RunTeardownAsync(int i)
        {
            var node = nodes[i];
            var name = FormatName(node, inputs);

            // Success is a property of the SCENARIO, not of the step that registered a cleanup: a
            // failed run should leave the whole world intact, not a half-torn-down mix of it. Steps a
            // filter left out did not fail — they were never asked to run — so they do not count.
            var succeeded = true;
            for (var n = 0; n < count; n++)
            {
                if (n != i && !excluded[n] && status[n] is not (StepStatus.Passed or StepStatus.NotTaken))
                {
                    succeeded = false;
                    break;
                }
            }

            var optionalAllowed = definition.TeardownPolicy switch
            {
                Run.Always => true,
                Run.OnSuccess => succeeded,
                _ => false,
            };

            // Reverse topological order of the owning step, then reverse registration order within a
            // step. Registration order alone is nondeterministic: steps run concurrently.
            var ordered = teardownLog.Entries
                .OrderByDescending(e => e.OwningStepIndex)
                .ThenByDescending(e => e.Sequence)
                .ToList();

            var startedAt = _timeProvider.GetUtcNow();
            if (_simulatedTime)
            {
                simStartOffset![i] = TimeSpan.Zero;
                simFinishOffset![i] = TimeSpan.Zero;
            }

            if (observer is not null)
            {
                await observer.OnStepStartingAsync(
                    new StepContext { Node = node, DisplayName = name }).ConfigureAwait(false);
            }

            // The STATUS answers one question — did teardown succeed? Whether the policy skipped
            // anything is information, not a verdict, so it goes in the log. Otherwise every suite
            // that never uses teardown, and every deliberate Run.Never, would carry a non-passing node.
            //
            // One context for the teardown node. Cleanups log and attach through it — the step that
            // registered them has long since been reported — and it is the ambient
            // ScenarioContext.Current while they run, so domain code below a cleanup attributes its
            // lines here. Its token is None on purpose: cleanups run after cancellation so nothing
            // leaks. It is NOT attached to the conflict ledger: teardown runs after every other node is
            // terminal, so it is ordered after all of them, and a cleanup's Delete must never be refused
            // as a conflict with the step that created the thing.
            var teardownContext = new ScenarioContext(
                node.StepId, name, services, resolver: null, _timeProvider, CancellationToken.None);
            teardownContext.AttachScenarioStart(scenarioStartRef.Of(startedAt));
            using var teardownActivity = StartStepActivity(definition, node, name);
            teardownContext.AttachActivity(teardownActivity);
            var skipped = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            List<Exception>? errors = null;

            ScenarioContext.SetCurrent(teardownContext);
            try
            {
                foreach (var entry in ordered)
                {
                    var owner = nodes[entry.OwningStepIndex].OperationName;
                    if (entry.Kind == Cleanup.Optional && !optionalAllowed)
                    {
                        skipped.Add(owner);
                        continue;
                    }

                    try
                    {
                        await entry.Cleanup(teardownContext).ConfigureAwait(false);
                        teardownContext.Log($"cleaned up: {owner}");
                    }
                    catch (Exception ex)
                    {
                        // Recorded, never rethrown here: aborting would leak everything behind it.
                        (errors ??= []).Add(ex);
                        teardownContext.Log($"cleanup FAILED: {owner} — {ex.Message}");
                    }
                }
            }
            finally
            {
                ScenarioContext.SetCurrent(null);
            }

            stopwatch.Stop();

            if (ordered.Count == 0)
            {
                teardownContext.Log("no cleanup registered");
            }

            if (skipped.Count > 0)
            {
                var why = $"teardown policy is {definition.TeardownPolicy}"
                    + (definition.TeardownPolicy == Run.OnSuccess && !succeeded
                        ? " and the scenario failed"
                        : string.Empty);
                teardownContext.Log($"skipped {skipped.Count} optional cleanup(s) — {why}: {string.Join(", ", skipped)}");
            }

            status[i] = errors is null ? StepStatus.Passed : StepStatus.Failed;
            if (teardownActivity is not null)
            {
                teardownActivity.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, errors is null ? "pass" : "fail");
                if (errors is not null)
                {
                    teardownActivity.SetStatus(ActivityStatusCode.Error, $"{errors.Count} teardown action(s) failed.");
                    foreach (var error in errors)
                    {
                        teardownActivity.AddException(error);
                    }
                }
            }

            results[i] = new StepResult
            {
                Node = node,
                DisplayName = name,
                Status = status[i],
                StartedAt = startedAt,
                Duration = stopwatch.Elapsed,
                TraceId = teardownActivity?.TraceId.ToString(),
                SpanId = teardownActivity?.SpanId.ToString(),
                Logs = teardownContext.Logs,
                LogEntries = teardownContext.LogEntries,
                Attachments = teardownContext.Attachments,
                Effects = teardownContext.Resources.Effects,
                Lineage = teardownContext.Resources.Lineage,
                Exception = errors is null
                    ? null
                    : new AggregateException($"{errors.Count} teardown action(s) failed.", errors),
            };
            if (observer is not null)
            {
                await observer.OnStepFinishedAsync(results[i]!).ConfigureAwait(false);
            }
        }

        async Task ApplyTerminalAsync(int i, StepStatus terminal, string reason)
        {
            pending.Remove(i);
            status[i] = terminal;
            var node = nodes[i];
            var name = FormatName(node, inputs);

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
                DisplayName = name,
                Status = terminal,
                StartedAt = startedAt,
                SkipReason = reason,
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
                    default: // Failed / Skipped
                        (bad ??= []).Add(nodes[source].OperationName);
                        break;
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
            var name = FormatName(node, inputs);
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
                DisplayName = name,
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

    /// <summary>Coerces a condition node's boxed output to its branch value using the generator-emitted
    /// <see cref="ScenarioNode.EvaluateCondition"/>. <see cref="ScenarioDefinition.Validate"/> has
    /// already proven it is non-null for every guarded condition.</summary>
    private static bool EvaluateGuard(ScenarioNode condition, object? output)
        => condition.EvaluateCondition!(output);

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
        string displayName,
        DateTimeOffset scenarioStart,
        TimeSpan simStartOffset,
        TeardownLog teardownLog,
        ResourceLedger ledger,
        ScenarioStart scenarioStartRef,
        CancellationToken scenarioToken)
    {
        // In simulated mode each step runs on its own clock seeded at base + start offset, so the
        // step's timing AND its resource-effect timestamps share one consistent timeline. Duration is
        // whatever the body advanced that clock. In real mode the step clock is the injected provider
        // and duration comes from the stopwatch — byte-for-byte the original path.
        var simClock = _simulatedTime ? new SimulatedClock(scenarioStart + simStartOffset) : null;
        var stepTimeProvider = simClock ?? _timeProvider;
        var startedAt = simClock is not null ? scenarioStart + simStartOffset : _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        TimeSpan Elapsed() => simClock?.Advanced ?? stopwatch.Elapsed;
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(scenarioToken);
        var context = new ScenarioContext(
            node.StepId, displayName, services, resolver: null, stepTimeProvider, stepCts.Token);

        context.AttachTeardown(teardownLog, node.Index);
        context.AttachLedger(ledger, node.Index);
        context.AttachScenarioStart(scenarioStartRef.Of(startedAt));

        // The step's span: a child of whatever is ambient (the scenario span when the run loop started
        // one), and Activity.Current for the body, so outgoing HTTP carries its traceparent. Null when
        // nothing listens. Real time, not the simulated clock — a trace viewer wants wall time.
        using var activity = StartStepActivity(definition, node, displayName);
        context.AttachActivity(activity);

        // Ambient for the duration of this step. Set inside RunNodeAsync (which each step enters on
        // its own async flow) so it is visible to the step body and to anything it awaits, but never
        // to the scheduler loop or to a sibling step.
        ScenarioContext.SetCurrent(context);

        try
        {
            object? output;
            if (node.Timeout is { } timeout)
            {
                var invokeTask = node.Invoke(inputs, context);
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token);
                var delayTask = Task.Delay(timeout, delayCts.Token);
                var winner = await Task.WhenAny(invokeTask, delayTask).ConfigureAwait(false);

                if (winner == delayTask && !invokeTask.IsCompleted)
                {
                    await stepCts.CancelAsync().ConfigureAwait(false); // best-effort: ask the operation to stop
                    // Observe the abandoned operation's eventual fault so it doesn't surface as an
                    // unobserved task exception in the test host.
                    _ = invokeTask.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw new TimeoutException(
                        $"Step '{displayName}' exceeded its timeout of {timeout}.");
                }

                await delayCts.CancelAsync().ConfigureAwait(false); // operation won the race; stop the timer
                output = await invokeTask.ConfigureAwait(false);
            }
            else
            {
                output = await node.Invoke(inputs, context).ConfigureAwait(false);
            }

            stopwatch.Stop();
            activity?.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "pass");
            return new NodeOutcome(
                new StepResult
                {
                    Node = node,
                    DisplayName = displayName,
                    Status = StepStatus.Passed,
                    StartedAt = startedAt,
                    Duration = Elapsed(),
                    Logs = context.Logs,
                    LogEntries = context.LogEntries,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
                    Lineage = context.Resources.Lineage,
                    TraceId = activity?.TraceId.ToString(),
                    SpanId = activity?.SpanId.ToString(),
                },
                output);
        }
        catch (OperationCanceledException) when (scenarioToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            activity?.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "skipped");
            return Outcome(StepStatus.Skipped, skipReason: CanceledSkipReason);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (activity is not null)
            {
                activity.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, "fail");
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }

            return Outcome(StepStatus.Failed, exception: ex);
        }

        NodeOutcome Outcome(StepStatus statusValue, Exception? exception = null, string? skipReason = null)
            => new(
                new StepResult
                {
                    Node = node,
                    DisplayName = displayName,
                    Status = statusValue,
                    StartedAt = startedAt,
                    Duration = Elapsed(),
                    Exception = exception,
                    SkipReason = skipReason,
                    Logs = context.Logs,
                    LogEntries = context.LogEntries,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
                    Lineage = context.Resources.Lineage,
                    TraceId = activity?.TraceId.ToString(),
                    SpanId = activity?.SpanId.ToString(),
                },
                null);
    }

    /// <summary>Starts a step's span with its identity tags, or returns null when nothing listens.</summary>
    private static Activity? StartStepActivity(ScenarioDefinition definition, ScenarioNode node, string displayName)
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

    private static string FormatName(ScenarioNode node, IStepInputs inputs)
    {
        if (node.FormatDisplayName is null)
        {
            return node.DisplayNameTemplate;
        }

        try
        {
            return node.FormatDisplayName(inputs);
        }
        catch
        {
            // A throwing formatter (or, for skips, unavailable dependency outputs) must not abort
            // the scenario — fall back to the unformatted template.
            return node.DisplayNameTemplate;
        }
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
    private sealed class ScenarioStart
    {
        private long _ticks = -1;

        public DateTimeOffset Of(DateTimeOffset candidate)
        {
            Interlocked.CompareExchange(ref _ticks, candidate.UtcTicks, -1);
            return new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        }
    }

    private readonly record struct NodeOutcome(StepResult Result, object? Output);

    private sealed class StepInputs(object?[] outputs) : IStepInputs
    {
        public T Get<T>(int producerIndex) => (T)outputs[producerIndex]!;
    }
}
