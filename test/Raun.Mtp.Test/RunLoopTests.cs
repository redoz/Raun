using System.Collections.Concurrent;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun;
using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Phase 3 behavioral tests for the run loop. On a run request the loop reads the filter
/// (a <c>TestNodeUidListFilter</c> uid set, or null = run everything), maps each requested step-uid
/// onto its owning scenario, and runs each <em>distinct</em> scenario exactly once via the
/// <see cref="ScenarioScheduler"/> with the Phase-3 <see cref="MtpReportSink"/> and a per-run
/// <see cref="CancellationTokenSource"/> owned by the loop. Because the sink fans events out to all
/// subscribers, every step the scheduler executes lights up — including the dependency siblings of a
/// single-step run.
/// </summary>
public class RunLoopTests
{
    private static ScenarioNode Node(
        int index,
        string stepId,
        string template,
        int[]? dependsOn = null,
        Func<IStepInputs, ScenarioContext, Task<object?>>? invoke = null,
        Guard[]? guards = null,
        int[]? mergeSources = null,
        bool synthetic = false,
        Func<object?, bool>? evaluate = null) => new()
        {
            Index = index,
            StepId = stepId,
            Phase = "Given",
            OperationName = $"Op{index}",
            DisplayNameTemplate = template,
            SourceFile = @"C:\src\S.cs",
            SourceLine = index + 1,
            DependsOn = dependsOn ?? [],
            Guards = guards ?? [],
            MergeSources = mergeSources ?? [],
            IsSynthetic = synthetic,
            EvaluateCondition = evaluate,
            Invoke = invoke ?? ((_, _) => Task.FromResult<object?>(null)),
        };

    private static ScenarioDefinition Definition(string id, string display, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
    };

    private static ScenarioDefinition Definition(string id, string display, ContendedResourceUse[] uses, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
        Uses = uses,
    };

    private static ContendedResourceUse Shared<T>() where T : IContendedResource => new(typeof(T), LockMode.Shared);

    private static ContendedResourceUse Exclusive<T>() where T : IContendedResource => new(typeof(T), LockMode.Exclusive);

    private static string Uid(string scenarioId, string stepId) => scenarioId + ":" + stepId;

    /// <summary>The selector a runner's uid list reduces to.</summary>
    // CA1859 wants the concrete UidNodeSelector return type, but callers use this through the
    // NodeSelector abstraction the same way production code does.
#pragma warning disable CA1859
    private static NodeSelector Select(params string[] uids)
        => new UidNodeSelector(new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase));
#pragma warning restore CA1859

    private static string PassedUid(StepFinished e) =>
        RaunDiscoverer.MakeUid(e.Definition.ScenarioId, e.Result.Node.StepId);

    /// <summary>A step body that advances its per-step clock by <paramref name="delta"/> via
    /// <see cref="ScenarioContext.SimulateElapsed"/> with no real waiting (an inert no-op in real mode).</summary>
    private static Func<IStepInputs, ScenarioContext, Task<object?>> Elapse(TimeSpan delta)
        => (_, ctx) => { ctx.SimulateElapsed(delta); return Task.FromResult<object?>(null); };

    // -- Scenario selection (filter -> distinct scenarios) ---------------------------------------

    [Fact]
    public void Null_filter_selects_every_registered_scenario()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));

        var selected = RaunRunLoop.SelectScenarios([a, b], selector: null);

        Assert.Equal(["a", "b"], selected.Select(d => d.ScenarioId).Order());
    }

    [Fact]
    public void Empty_uid_set_selects_nothing()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));

        var selected = RaunRunLoop.SelectScenarios([a], selector: Select());

        Assert.Empty(selected);
    }

    [Fact]
    public void Multi_step_filter_for_one_scenario_selects_that_scenario_once()
    {
        var a = Definition("a", "A", Node(0, "x", "x"), Node(1, "y", "y"), Node(2, "z", "z"));
        var b = Definition("b", "B", Node(0, "x", "x"));

        // Three step uids of the SAME scenario must map to a single distinct scenario.
        var selector = Select(Uid("a", "x"), Uid("a", "y"), Uid("a", "z"));
        var selected = RaunRunLoop.SelectScenarios([a, b], selector);

        var only = Assert.Single(selected);
        Assert.Equal("a", only.ScenarioId);
    }

    [Fact]
    public void Filter_spanning_two_scenarios_selects_both()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));

        var selector = Select(Uid("a", "x"), Uid("c", "z"));
        var selected = RaunRunLoop.SelectScenarios([a, b, c], selector);

        Assert.Equal(["a", "c"], selected.Select(d => d.ScenarioId).Order());
    }

    // -- Run loop end-to-end (over a real ScenarioScheduler) -------------------------------------

    [Fact]
    public async Task Multi_step_filter_for_one_scenario_runs_the_scheduler_exactly_once()
    {
        var def = Definition("a", "A",
            Node(0, "x", "x"),
            Node(1, "y", "y", dependsOn: [0]),
            Node(2, "z", "z", dependsOn: [1]));

        var runs = 0;
        var loop = new RaunRunLoop(
            () => [def],
            runScenario: (_, _, _, _, _) => { Interlocked.Increment(ref runs); return Task.FromResult<IReadOnlyList<StepResult>>([]); });

        var selector = Select(Uid("a", "x"), Uid("a", "z"));
        var sink = new RecordingSink();
        await loop.RunAsync(selector, sink, CancellationToken.None);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Single_step_filter_lights_up_all_executed_siblings()
    {
        // z depends on y depends on x. A filter naming only the last step must still publish the
        // whole chain, because running z requires running x and y first.
        var def = Definition("chain", "chain",
            Node(0, "x", "x"),
            Node(1, "y", "y", dependsOn: [0]),
            Node(2, "z", "z", dependsOn: [1]));

        var loop = new RaunRunLoop(() => [def]);

        var sink = new RecordingSink();
        var selector = Select(Uid("chain", "z"));
        await loop.RunAsync(selector, sink, CancellationToken.None);

        // All three siblings reported (each at least one finished Passed update).
        Assert.Contains(Uid("chain", "x"), sink.PassedUids);
        Assert.Contains(Uid("chain", "y"), sink.PassedUids);
        Assert.Contains(Uid("chain", "z"), sink.PassedUids);
    }

    [Fact]
    public async Task Null_filter_runs_all_registered_scenarios()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));

        var loop = new RaunRunLoop(() => [a, b]);

        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Contains(Uid("a", "x"), sink.PassedUids);
        Assert.Contains(Uid("b", "y"), sink.PassedUids);
    }

    [Fact]
    public async Task Degree_one_runs_scenarios_sequentially()
    {
        // Records the max observed concurrency across scenario runs; degree 1 => never exceeds 1.
        var current = 0;
        var max = 0;
        var sync = new object();

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
            }

            await Task.Delay(20);

            lock (sync)
            {
                current--;
            }

            return null;
        }

        var a = Definition("a", "A", Node(0, "x", "x", invoke: Body));
        var b = Definition("b", "B", Node(0, "y", "y", invoke: Body));
        var c = Definition("c", "C", Node(0, "z", "z", invoke: Body));

        var loop = new RaunRunLoop(() => [a, b, c], maxParallelScenarios: 1);
        await loop.RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        Assert.Equal(1, max);
    }

    [Fact]
    public async Task Scenarios_run_concurrently_up_to_the_degree()
    {
        // Six scenarios, degree 3. Every body blocks until three are in flight, so "== 3" is proven
        // by rendezvous rather than sampled; the launcher itself guarantees "<= 3".
        var current = 0;
        var max = 0;
        var sync = new object();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
                if (current == 3)
                {
                    third.TrySetResult();
                }
            }

            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (sync)
            {
                current--;
            }

            return null;
        }

        var definitions = Enumerable.Range(0, 6)
            .Select(i => Definition($"s{i}", $"S{i}", Node(0, "x", "x", invoke: Body)))
            .ToArray();

        var sink = new RecordingSink();
        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Equal(3, max);
        Assert.Equal(6, sink.PassedUids.Count());
    }

    // -- Admission through the ContentionGate: overlap, order, and wait accounting ----------------

    [Fact]
    public async Task Exclusive_users_of_one_resource_never_overlap_while_an_unrelated_scenario_does()
    {
        // A holds the db until C has started (so A and C overlap); B also wants the db and must wait
        // for A. Degree 3 leaves room for all three, so only the gate can hold B back.
        var cStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dbCurrent = 0;
        var dbMax = 0;
        var sync = new object();

        async Task<object?> DbBody(IStepInputs _, ScenarioContext __)
        {
            lock (sync) { dbCurrent++; dbMax = Math.Max(dbMax, dbCurrent); }
            await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (sync) { dbCurrent--; }
            return null;
        }

        var a = Definition("a", "A", [Exclusive<ExclusiveDb>()], Node(0, "x", "x", invoke: DbBody));
        var b = Definition("b", "B", [Exclusive<ExclusiveDb>()], Node(0, "y", "y", invoke: DbBody));
        var c = Definition("c", "C", Node(0, "z", "z", invoke: (_, _) => { cStarted.TrySetResult(); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [a, b, c], maxParallelScenarios: 3).RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Equal(1, dbMax);
        Assert.Equal(3, sink.PassedUids.Count());
        Assert.Equal(["a", "c", "b"], sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
    }

    [Fact]
    public async Task Shared_users_overlap_and_an_exclusive_user_waits_for_all_of_them()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedInFlight = 0;
        var sharedFinished = 0;
        var seenBySweeper = -1;

        async Task<object?> SharedBody(IStepInputs _, ScenarioContext __)
        {
            if (Interlocked.Increment(ref sharedInFlight) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(TimeSpan.FromSeconds(10)); // proves the two shared users overlap
            Interlocked.Increment(ref sharedFinished);
            return null;
        }

        var s1 = Definition("s1", "S1", [Shared<SharedCatalog>()], Node(0, "x", "x", invoke: SharedBody));
        var s2 = Definition("s2", "S2", [Shared<SharedCatalog>()], Node(0, "y", "y", invoke: SharedBody));
        var sweeper = Definition("sweep", "Sweep", [Exclusive<SharedCatalog>()],
            Node(0, "z", "z", invoke: (_, _) => { seenBySweeper = Volatile.Read(ref sharedFinished); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [s1, s2, sweeper], maxParallelScenarios: 3).RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Equal(2, seenBySweeper);
        var sweepStarted = Assert.Single(sink.Events.OfType<ScenarioStarted>(), e => e.Definition.ScenarioId == "sweep");
        Assert.True(sweepStarted.Waited > TimeSpan.Zero);
        Assert.Equal(typeof(SharedCatalog), sweepStarted.WaitedFor);
    }

    [Fact]
    public async Task Pooled_capacity_caps_concurrent_holders_below_the_degree()
    {
        // Degree 4, pool capacity 2. The rendezvous trips one PAST capacity (current == 3) rather
        // than at capacity itself: tripping at capacity would let the test's own synchronization pin
        // max at 2 even with the gate removed (every body's await would then resume synchronously
        // and decrement before the next launch gets scheduled). Un-gated, all four bodies run and
        // current reaches 3, tripping the breach and pushing max past 2 -> red. Gated, only two
        // bodies ever run at once, current never reaches 3, breach never trips, and the fallback
        // delay is what releases each pair instead -> green, and the fallback keeps this from hanging.
        var current = 0;
        var max = 0;
        var sync = new object();
        var breach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
                if (current == 3) { breach.TrySetResult(); }
            }

            await Task.WhenAny(breach.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
            lock (sync) { current--; }
            return null;
        }

        var definitions = Enumerable.Range(0, 4)
            .Select(i => Definition($"m{i}", $"M{i}", [Shared<PooledSmtp>()], Node(0, "x", "x", invoke: Body)))
            .ToArray();

        var sink = new RecordingSink();
        await new RaunRunLoop(() => definitions, maxParallelScenarios: 4).RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Equal(2, max);
        Assert.Equal(4, sink.PassedUids.Count());
    }

    [Fact]
    public async Task Admission_prefers_registration_order_among_admissible_scenarios()
    {
        // Degree 2. A holds the db until D starts; B wants the db; C and D are free. Expected launch
        // order: A, C (pass one), D (when C finishes; B is still refused), B (when A releases).
        var dStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = Definition("a", "A", [Exclusive<ExclusiveDb>()], Node(0, "x", "x", invoke: async (_, _) =>
        {
            await dStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }));
        var b = Definition("b", "B", [Exclusive<ExclusiveDb>()], Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));
        var d = Definition("d", "D", Node(0, "w", "w", invoke: (_, _) => { dStarted.TrySetResult(); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [a, b, c, d], maxParallelScenarios: 2).RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Equal(["a", "c", "d", "b"], sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
    }

    [Fact]
    public async Task Waiting_scenarios_report_their_wait_on_the_span_and_uses_are_tagged()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderId = "hold-" + Guid.NewGuid().ToString("N")[..8];
        var waiterId = "wait-" + Guid.NewGuid().ToString("N")[..8];

        var holder = Definition(holderId, "holder", [Exclusive<ExclusiveDb>(), Shared<SharedCatalog>()],
            Node(0, "x", "x", invoke: async (_, _) => { await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); return null; }));
        var waiter = Definition(waiterId, "waiter", [Shared<ExclusiveDb>()], Node(0, "y", "y"));
        var opener = Definition("open", "opener", Node(0, "z", "z", invoke: (_, _) => { release.TrySetResult(); return Task.FromResult<object?>(null); }));
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => [holder, waiter, opener], maxParallelScenarios: 3)
            .RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        var holderSpan = Assert.Single(capture.ForScenario(holderId), s => s.DisplayName == "holder");
        Assert.Equal("ExclusiveDb:Exclusive,SharedCatalog:Shared", holderSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioUses));
        Assert.Null(holderSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedMs));

        var waiterSpan = Assert.Single(capture.ForScenario(waiterId), s => s.DisplayName == "waiter");
        Assert.Equal("ExclusiveDb:Shared", waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioUses));
        Assert.True(Convert.ToInt64(waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedMs), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal("ExclusiveDb", waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedFor));
    }

    [Fact]
    public async Task A_misdeclared_resource_token_halts_launching_drains_siblings_and_surfaces_the_error()
    {
        // Degree 3: "a" is running and blocked until "release" is set; "bad" declares a token with no
        // kind attribute, so the gate throws when the scan reaches it. The loop must stop launching,
        // let "a" finish, publish RunFinished once, and surface the gate's error from RunAsync.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Definition("a", "A", Node(0, "x", "x", invoke: async (_, _) => { await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); return null; }));
        var bad = Definition("bad", "Bad", [new ContendedResourceUse(typeof(MisdeclaredToken), LockMode.Shared)], Node(0, "y", "y"));
        var never = Definition("never", "Never", Node(0, "z", "z"));
        var sink = new RecordingSink();
        var loop = new RaunRunLoop(() => [a, bad, never], maxParallelScenarios: 3);

        var run = loop.RunAsync(selector: null, sink, CancellationToken.None).AsTask();

        // The run must still be in flight here: it is draining "a". Without the catch around
        // TryAcquire the gate's throw would have faulted the whole run synchronously already.
        Assert.False(run.IsCompleted);
        release.TrySetResult();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await run);

        Assert.Contains(nameof(MisdeclaredToken), ex.Message, StringComparison.Ordinal);
        Assert.Equal(["a"], sink.Events.OfType<ScenarioFinished>().Select(e => e.Definition.ScenarioId));
        Assert.DoesNotContain("never", sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public void A_negative_degree_is_rejected_and_zero_means_the_processor_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaunRunLoop(() => [], maxParallelScenarios: -1));
        Assert.Equal(Environment.ProcessorCount, new RaunRunLoop(() => []).MaxParallelScenarios);
        Assert.Equal(2, new RaunRunLoop(() => [], maxParallelScenarios: 2).MaxParallelScenarios);
    }

    [Fact]
    public async Task RunStarted_carries_the_registration_order_with_preflight_first()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));
        var sink = new RecordingSink();

        await new RaunRunLoop(() => [a, b], preflight: _ => Task.CompletedTask, maxParallelScenarios: 2)
            .RunAsync(selector: null, sink, CancellationToken.None);

        var started = Assert.Single(sink.Events.OfType<RunStarted>());
        Assert.Equal(2, started.ScenarioCount);
        Assert.NotNull(started.Scenarios);
        Assert.Equal([Preflight.ScenarioId, "a", "b"], started.Scenarios.Select(d => d.ScenarioId)); // "raun", then registration order
    }

    [Fact]
    public async Task A_failed_preflight_skips_every_scenario_at_any_degree()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));
        var sink = new RecordingSink();

        await new RaunRunLoop(
                () => [a, b, c],
                preflight: _ => throw new InvalidOperationException("no container runtime"),
                maxParallelScenarios: 3)
            .RunAsync(selector: null, sink, CancellationToken.None);

        Assert.Empty(sink.PassedUids);
        foreach (var uid in new[] { Uid("a", "x"), Uid("b", "y"), Uid("c", "z") })
        {
            Assert.Contains(uid, sink.SkippedUids);
        }

        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public async Task One_steps_cancellation_does_not_kill_the_shared_run_for_siblings()
    {
        // The loop owns ONE CTS per scenario run, linked to the platform token — it is never tied to
        // a single step node's lifecycle. So a step that itself throws OperationCanceledException
        // (a step that observed cancellation locally / timed out internally) must NOT abort the
        // independent siblings sharing that run: the loop's run token stays live, and the scheduler
        // keeps launching the other ready steps. Here "lonely" and "sibling" have no dependency
        // between them, so the scheduler runs both regardless of "lonely"'s outcome.
        var siblingRan = false;

        var def = Definition("scn", "scn",
            Node(0, "lonely", "lonely", invoke: (_, _) =>
                // An OCE NOT tied to the run token surfaces as an ordinary failure, not a scenario
                // cancel — proving the run token was untouched.
                throw new OperationCanceledException("this step canceled itself")),
            Node(1, "sibling", "sibling", invoke: (_, _) =>
            {
                siblingRan = true;
                return Task.FromResult<object?>(null);
            }));

        var loop = new RaunRunLoop(() => [def]);

        var sink = new RecordingSink();
        // Both steps are selected: a filter naming only "lonely" would (correctly) leave "sibling" out.
        var selector = Select(Uid("scn", "lonely"), Uid("scn", "sibling"));
        await loop.RunAsync(selector, sink, CancellationToken.None);

        Assert.True(siblingRan);
        // The sibling completes successfully; nothing was skipped (the run token never canceled).
        Assert.Contains(Uid("scn", "sibling"), sink.PassedUids);
        Assert.Empty(sink.SkippedUids);
    }

    [Fact]
    public async Task Honors_an_already_cancelled_platform_token_by_skipping_steps()
    {
        var bodyRan = false;
        var def = Definition("scn", "scn",
            Node(0, "x", "x", invoke: (_, _) => { bodyRan = true; return Task.FromResult<object?>(null); }));

        var loop = new RaunRunLoop(() => [def]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, cts.Token);

        // The scheduler skips steps when the run token is already canceled; the body never runs.
        Assert.False(bodyRan);
        Assert.Contains(Uid("scn", "x"), sink.SkippedUids);
    }

    [Fact]
    public async Task Cancellation_mid_run_stops_launching_further_scenarios()
    {
        // When the platform cancels mid-run, the loop must stop launching scenarios it has not yet
        // started — rather than iterating every remaining scenario only to report all-skipped (which
        // would flood the runner with skipped updates for work the user never started). The first
        // scenario cancels the platform token from inside its body; the second must never run.
        using var cts = new CancellationTokenSource();
        var secondRan = false;

        var first = Definition("first", "first",
            Node(0, "a", "a", invoke: (_, _) => { cts.Cancel(); return Task.FromResult<object?>(null); }));
        var second = Definition("second", "second",
            Node(0, "b", "b", invoke: (_, _) => { secondRan = true; return Task.FromResult<object?>(null); }));

        var loop = new RaunRunLoop(() => [first, second], maxParallelScenarios: 1);

        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, cts.Token);

        Assert.False(secondRan);
        // The second scenario was never started at all (not even as skipped).
        Assert.DoesNotContain("second",
            sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
    }

    [Fact]
    public async Task Cancellation_mid_run_stops_launching_scenarios_beyond_the_first_pass()
    {
        // Degree 3, five scenarios: the first pass launches up to three; the first body cancels the
        // platform token, so no later slot may be refilled — scenarios four and five never start.
        using var cts = new CancellationTokenSource();
        var launched = new List<string>();
        var sync = new object();

        ScenarioDefinition Def(string id, bool cancels) => Definition(id, id,
            Node(0, "a", "a", invoke: (_, _) =>
            {
                lock (sync) { launched.Add(id); }
                if (cancels) { cts.Cancel(); }
                return Task.FromResult<object?>(null);
            }));

        var definitions = new[] { Def("one", cancels: true), Def("two", false), Def("three", false), Def("four", false), Def("five", false) };
        var sink = new RecordingSink();

        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(selector: null, sink, cts.Token);

        var startedIds = sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId).ToList();
        Assert.Contains("one", startedIds);
        Assert.DoesNotContain("four", startedIds);
        Assert.DoesNotContain("five", startedIds);
        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public async Task A_faulted_scenario_task_drains_its_siblings_publishes_RunFinished_and_rethrows()
    {
        // Three scenarios at degree 3. "boom" faults from the run seam once every scenario has
        // started; the others must still finish and be reported, RunFinished must still publish,
        // and the fault must surface from RunAsync rather than vanish.
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        Task<IReadOnlyList<StepResult>> Seam(
            ScenarioDefinition definition, IStepObserver observer, IServiceProvider? services,
            IReadOnlySet<int>? targets, CancellationToken token)
            => SeamAsync(definition);

        async Task<IReadOnlyList<StepResult>> SeamAsync(ScenarioDefinition definition)
        {
            if (Interlocked.Increment(ref startedCount) == 3)
            {
                allStarted.TrySetResult();
            }

            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (definition.ScenarioId == "boom")
            {
                throw new InvalidOperationException("scheduler bug");
            }

            return definition.Nodes.Select(n => new StepResult
            {
                Node = n, DisplayName = n.DisplayNameTemplate, Status = StepStatus.Passed, StartedAt = DateTimeOffset.UtcNow,
            }).ToList();
        }

        var a = Definition("a", "A", Node(0, "x", "x"));
        var boom = Definition("boom", "Boom", Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));
        var sink = new RecordingSink();
        var loop = new RaunRunLoop(() => [a, boom, c], runScenario: Seam, maxParallelScenarios: 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await loop.RunAsync(selector: null, sink, CancellationToken.None));

        Assert.Equal("scheduler bug", ex.Message);
        Assert.Equal(["a", "c"], sink.Events.OfType<ScenarioFinished>().Select(e => e.Definition.ScenarioId).Order());
        Assert.Single(sink.Events.OfType<RunFinished>());
    }

    [Fact]
    public async Task Through_the_framework_run_request_executes_the_registered_scenario()
    {
        // End-to-end through RaunTestFramework.OnExecute (registry-backed), proving the framework
        // wires the run loop into the run request path.
        var method = $"Raun.Mtp.Test.RunLoop.{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => Definition("fw-scn", "fw scenario",
            Node(0, "a", "a"),
            Node(1, "b", "b", dependsOn: [0])));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("fw-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnExecute(uid, filter: null, bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);
        var passed = bus.Nodes
            .Where(n => n.Properties.OfType<PassedTestNodeStateProperty>().Length != 0)
            .Select(n => n.Uid.Value).ToList();
        Assert.Contains("fw-scn:a", passed);
        Assert.Contains("fw-scn:b", passed);
    }

    // -- Simulated-time opt-in threaded through the loop (A4) -------------------------------------

    [Fact]
    public async Task Default_runner_in_simulated_mode_yields_nonzero_overlapping_timings()
    {
        // simulateTime:true must reach DefaultRunScenario, which builds a ScenarioScheduler(simulatedTime:true).
        // Two parallel siblings advance their own per-step clocks (NO real waiting), producing exact,
        // overlapping durations on one shared timeline — only possible if the flag threaded all the way down.
        var slow = TimeSpan.FromMilliseconds(700);
        var fast = TimeSpan.FromMilliseconds(500);
        var def = Definition("sim", "sim",
            Node(0, "root", "root", invoke: Elapse(TimeSpan.Zero)),
            Node(1, "slow", "slow", dependsOn: [0], invoke: Elapse(slow)),
            Node(2, "fast", "fast", dependsOn: [0], invoke: Elapse(fast)));

        var loop = new RaunRunLoop(() => [def], runScenario: null, simulateTime: true);
        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        var finished = sink.Events.OfType<StepFinished>()
            .Where(e => e.Result.Status == StepStatus.Passed)
            .ToDictionary(e => e.Result.Node.StepId, e => e.Result, StringComparer.Ordinal);

        // Durations are exactly the simulated amounts (non-zero, deterministic).
        Assert.Equal(slow, finished["slow"].Duration);
        Assert.Equal(fast, finished["fast"].Duration);

        // The two siblings share a start instant and genuinely overlap on the one timeline.
        var s1 = finished["slow"].StartedAt; var e1 = s1 + finished["slow"].Duration;
        var s2 = finished["fast"].StartedAt; var e2 = s2 + finished["fast"].Duration;
        Assert.True(s1 < e2 && s2 < e1, "parallel siblings must overlap under simulated time");
    }

    [Fact]
    public async Task Default_runner_in_real_mode_ignores_SimulateElapsed()
    {
        // simulateTime defaults to false: the same SimulateElapsed body is an inert no-op and the measured
        // duration is real wall-clock for a no-op step — nowhere near the simulated 5s.
        var def = Definition("real", "real",
            Node(0, "x", "x", invoke: Elapse(TimeSpan.FromSeconds(5))));

        var loop = new RaunRunLoop(() => [def]); // simulateTime defaults to false
        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        var finished = sink.Events.OfType<StepFinished>().Single(e => e.Result.Status == StepStatus.Passed);
        Assert.True(finished.Result.Duration < TimeSpan.FromSeconds(1),
            $"real mode must not absorb the simulated 5s (was {finished.Result.Duration})");
    }

    [Fact]
    public async Task Framework_built_with_simulateTime_threads_the_flag_to_the_scheduler()
    {
        // The (IServiceProvider, bool) overload carries the opt-in flag from RunAsync down through the run
        // loop to the scheduler: a body that SimulateElapsed(800ms) reports exactly that on the published
        // node's TimingProperty — only reachable if simulated mode arrived at the scheduler.
        var method = $"Raun.Mtp.Test.SimRun.{Guid.NewGuid():N}";
        var work = TimeSpan.FromMilliseconds(800);
        ScenarioRegistry.Register(method, () => Definition("sim-fw", "sim scenario",
            Node(0, "a", "a", invoke: Elapse(work))));

        var framework = new RaunTestFramework(services: null!, simulateTime: true);
        var uid = new SessionUid("sim-fw-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        await framework.OnExecute(uid, filter: null, bus, () => { }, CancellationToken.None);

        var node = bus.Nodes.Single(n => n.Uid.Value == "sim-fw:a"
            && n.Properties.OfType<PassedTestNodeStateProperty>().Length != 0);
        var timing = node.Properties.OfType<TimingProperty>().Single();
        Assert.Equal(work, timing.GlobalTiming.Duration);
    }

    // -- Service provider threaded through the loop into ScenarioContext.Services ------------------

    [Fact]
    public async Task Provider_given_to_the_loop_reaches_a_steps_ScenarioContext()
    {
        // The loop's default runner must hand its provider to the scheduler, which puts it on every
        // ScenarioContext — otherwise ctx.Services is null for user step code in a real run.
        IServiceProvider? seen = null;
        var def = Definition("di", "di",
            Node(0, "x", "x", invoke: (_, ctx) => { seen = ctx.Services; return Task.FromResult<object?>(null); }));

        var provider = new StubServiceProvider();
        var loop = new RaunRunLoop(() => [def], services: provider);
        await loop.RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        Assert.Same(provider, seen);
    }

    [Fact]
    public async Task No_provider_leaves_ScenarioContext_Services_null_without_throwing()
    {
        // The null path is real: RaunTestFramework's parameterless ctor leaves the provider null.
        IServiceProvider? seen = new StubServiceProvider();
        var ran = false;
        var def = Definition("no-di", "no-di",
            Node(0, "x", "x", invoke: (_, ctx) => { seen = ctx.Services; ran = true; return Task.FromResult<object?>(null); }));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        Assert.True(ran);
        Assert.Null(seen);
        Assert.Contains(Uid("no-di", "x"), sink.PassedUids);
    }

    [Fact]
    public async Task Framework_built_with_a_provider_threads_it_to_the_step_context()
    {
        // End-to-end: the CONSUMER's provider -> run loop -> scheduler -> ctx.Services.
        //
        // MTP's own provider is deliberately NOT what steps see. It carries platform internals
        // (command-line options, the logger factory), and letting a step resolve those would couple
        // user code to the platform. The framework keeps it for its own use and threads the
        // consumer's provider — the one built in their Program.cs — to step bodies instead.
        var method = $"Raun.Mtp.Test.DiRun.{Guid.NewGuid():N}";
        IServiceProvider? seen = null;
        ScenarioRegistry.Register(method, () => Definition("di-fw", "di scenario",
            Node(0, "a", "a", invoke: (_, ctx) => { seen = ctx.Services; return Task.FromResult<object?>(null); })));

        var mtpProvider = new StubServiceProvider();
        var userProvider = new StubServiceProvider();
        var framework = new RaunTestFramework(mtpProvider, simulateTime: false, userProvider);
        var uid = new SessionUid("di-fw-run");
        await framework.CreateTestSession(uid);

        await framework.OnExecute(uid, filter: null, new RecordingMessageBus(), () => { }, CancellationToken.None);

        Assert.Same(userProvider, seen);
        Assert.NotSame(mtpProvider, seen);
    }

    [Fact]
    public async Task Without_a_consumer_provider_the_step_context_has_no_services()
    {
        // MTP's provider must not leak in as a fallback.
        var method = $"Raun.Mtp.Test.DiRun.{Guid.NewGuid():N}";
        IServiceProvider? seen = null;
        var ran = false;
        ScenarioRegistry.Register(method, () => Definition("di-none", "no di scenario",
            Node(0, "a", "a", invoke: (_, ctx) => { seen = ctx.Services; ran = true; return Task.FromResult<object?>(null); })));

        var framework = new RaunTestFramework(new StubServiceProvider());
        var uid = new SessionUid("di-none-run");
        await framework.CreateTestSession(uid);

        await framework.OnExecute(uid, filter: null, new RecordingMessageBus(), () => { }, CancellationToken.None);

        Assert.True(ran);
        Assert.Null(seen);
    }

    /// <summary>
    /// A provider standing in for MTP's own. Identity is what these tests assert on; it only has to
    /// answer <see cref="ICommandLineOptions"/> because the framework consults it (for --report-html)
    /// before the run loop starts, and MTP's accessor extension throws on a missing service.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandLineOptions) ? new NoOptions() : null;

        private sealed class NoOptions : ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => false;

            public bool TryGetOptionArgumentList(
                string optionName,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments)
            {
                arguments = null;
                return false;
            }
        }
    }

    private sealed class RecordingSink : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];

        public ValueTask PublishAsync(RunEvent evt)
        {
            lock (Events) { Events.Add(evt); }
            return default;
        }

        public IEnumerable<string> PassedUids => Events
            .OfType<StepFinished>()
            .Where(e => e.Result.Status == StepStatus.Passed)
            .Select(PassedUid);

        public IEnumerable<string> SkippedUids => Events
            .OfType<StepFinished>()
            .Where(e => e.Result.Status == StepStatus.Skipped)
            .Select(e => RaunDiscoverer.MakeUid(e.Definition.ScenarioId, e.Result.Node.StepId));
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        private readonly List<TestNodeUpdateMessage> updates = [];

        public IReadOnlyList<TestNode> Nodes
        {
            get { lock (updates) { return updates.Select(u => u.TestNode).ToList(); } }
        }

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            if (data is TestNodeUpdateMessage update)
            {
                lock (updates) { updates.Add(update); }
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Conditional_scenario_runs_the_taken_arm_and_reports_the_other_as_not_green()
    {
        var def = Definition("c", "Conditional",
            Node(0, "cond", "is priority",
                invoke: (_, _) => Task.FromResult<object?>(true),
                evaluate: static o => (bool)o!),
            Node(1, "urgent", "create urgent", dependsOn: [0], guards: [new Guard(0, true)]),
            Node(2, "standard", "create standard", dependsOn: [0], guards: [new Guard(0, false)]),
            Node(3, "merge", "«merge appt»", mergeSources: [1, 2], synthetic: true));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        var finished = sink.Events.OfType<StepFinished>().ToDictionary(e => e.Result.Node.StepId, e => e.Result);

        Assert.Equal(StepStatus.Passed, finished["urgent"].Status);
        Assert.Equal(StepStatus.NotTaken, finished["standard"].Status);
        Assert.DoesNotContain("standard", sink.PassedUids.Select(u => u.Split(':')[1]));
    }

    [Fact]
    public async Task Not_taken_step_never_reaches_the_passed_tally()
    {
        var def = Definition("c2", "Conditional",
            Node(0, "cond", "is priority",
                invoke: (_, _) => Task.FromResult<object?>(false),
                evaluate: static o => (bool)o!),
            Node(1, "urgent", "create urgent", dependsOn: [0], guards: [new Guard(0, true)]));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(selector: null, sink, CancellationToken.None);

        Assert.DoesNotContain(Uid("c2", "urgent"), sink.PassedUids);
    }

    // -- Filtered runs: a step runs with everything up to and including it, and nothing after ------

    private static IEnumerable<string> FinishedUids(RecordingSink sink) =>
        sink.Events.OfType<StepFinished>().Select(PassedUid);

    private static IEnumerable<string> StartedUids(RecordingSink sink) =>
        sink.Events.OfType<StepStarted>().Select(e => RaunDiscoverer.MakeUid(e.Definition.ScenarioId, e.Context.Node.StepId));

    [Fact]
    public async Task Selecting_a_middle_step_runs_its_predecessors_and_leaves_the_rest_out()
    {
        var def = Definition("chain", "chain",
            Node(0, "x", "x"),
            Node(1, "y", "y", dependsOn: [0]),
            Node(2, "z", "z", dependsOn: [1]));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(Select(Uid("chain", "y")), sink, CancellationToken.None);

        Assert.Contains(Uid("chain", "x"), sink.PassedUids);
        Assert.Contains(Uid("chain", "y"), sink.PassedUids);
        // z is not reported at all — not started, not finished, not skipped.
        Assert.DoesNotContain(Uid("chain", "z"), FinishedUids(sink));
        Assert.DoesNotContain(Uid("chain", "z"), StartedUids(sink));
    }

    [Fact]
    public async Task Selecting_one_branch_of_a_fork_leaves_the_other_branch_out()
    {
        var def = Definition("fork", "fork",
            Node(0, "root", "root"),
            Node(1, "left", "left", dependsOn: [0]),
            Node(2, "right", "right", dependsOn: [0]));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(Select(Uid("fork", "left")), sink, CancellationToken.None);

        Assert.Contains(Uid("fork", "root"), sink.PassedUids);
        Assert.Contains(Uid("fork", "left"), sink.PassedUids);
        Assert.DoesNotContain(Uid("fork", "right"), FinishedUids(sink));
    }

    [Fact]
    public async Task Selecting_a_guarded_step_pulls_in_its_condition()
    {
        var def = Definition("cond", "cond",
            Node(0, "patient", "patient"),
            Node(1, "priority", "priority", dependsOn: [0], invoke: (_, _) => Task.FromResult<object?>(true), evaluate: static o => (bool)o!),
            Node(2, "urgent", "urgent", dependsOn: [1], guards: [new Guard(1, true)]),
            Node(3, "notify", "notify", dependsOn: [0]));

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(Select(Uid("cond", "urgent")), sink, CancellationToken.None);

        Assert.Contains(Uid("cond", "priority"), sink.PassedUids);
        Assert.Contains(Uid("cond", "urgent"), sink.PassedUids);
        Assert.DoesNotContain(Uid("cond", "notify"), FinishedUids(sink));
    }

    [Fact]
    public async Task A_filtered_run_still_runs_and_reports_teardown()
    {
        var teardown = new ScenarioNode
        {
            Index = 2,
            StepId = "teardown",
            Phase = "Then",
            OperationName = "Teardown",
            DisplayNameTemplate = "Teardown",
            DependsOn = [],
            IsTeardown = true,
            Invoke = (_, _) => Task.FromResult<object?>(null),
        };
        var def = Definition("td", "td",
            Node(0, "x", "x"),
            Node(1, "y", "y", dependsOn: [0]),
            teardown);

        var loop = new RaunRunLoop(() => [def]);
        var sink = new RecordingSink();
        await loop.RunAsync(Select(Uid("td", "x")), sink, CancellationToken.None);

        Assert.Contains(Uid("td", "x"), sink.PassedUids);
        Assert.Contains(Uid("td", "teardown"), sink.PassedUids);
        Assert.DoesNotContain(Uid("td", "y"), FinishedUids(sink));
    }

    [Fact]
    public void SelectTargets_maps_uids_to_node_indices_and_null_to_everything()
    {
        var def = Definition("a", "A", Node(0, "x", "x"), Node(1, "y", "y"), Node(2, "z", "z"));

        Assert.Null(RaunRunLoop.SelectTargets(def, selector: null));

        var targets = RaunRunLoop.SelectTargets(
            def, Select(Uid("a", "y"), Uid("other", "x")));
        Assert.Equal([1], targets!.Order());
    }

    // -- Tracing: run span, scenario roots linked to it, step spans nested under the scenario --------

    /// <summary>Captures Raun spans whose <c>raun.run</c> or <c>raun.scenario</c> tag matches this test.</summary>
    private sealed class SpanCapture : IDisposable
    {
        private readonly List<System.Diagnostics.Activity> _all = [];
        private readonly System.Diagnostics.ActivityListener _listener;

        public SpanCapture()
        {
            _listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == RaunTelemetry.SourceName,
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _)
                    => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_all)
                    {
                        _all.Add(activity);
                    }
                },
            };
            System.Diagnostics.ActivitySource.AddActivityListener(_listener);
        }

        public List<System.Diagnostics.Activity> ForScenario(string scenarioId)
        {
            lock (_all)
            {
                return _all.Where(a => (string?)a.GetTagItem(RaunTelemetry.Attributes.Scenario) == scenarioId).ToList();
            }
        }

        public System.Diagnostics.Activity? Run(string runId)
        {
            lock (_all)
            {
                return _all.SingleOrDefault(a => a.DisplayName == "raun.run" && (string?)a.GetTagItem(RaunTelemetry.Attributes.Run) == runId);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Each_scenario_is_its_own_trace_linked_to_the_run_span_with_steps_nested_under_it()
    {
        var scenarioId = "trace-" + Guid.NewGuid().ToString("N")[..8];
        var def = Definition(scenarioId, "traced scenario",
            Node(0, "x", "x"),
            Node(1, "y", "y", dependsOn: [0]));
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => [def]).RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        var spans = capture.ForScenario(scenarioId);
        var scenario = Assert.Single(spans, s => s.DisplayName == "traced scenario");
        var steps = spans.Where(s => s != scenario).ToList();
        Assert.Equal(2, steps.Count);

        // The scenario span is a ROOT that links to the run span; it does not nest under it.
        Assert.Null(scenario.Parent);
        Assert.Equal(default, scenario.ParentSpanId);
        var link = Assert.Single(scenario.Links);
        var runId = (string?)scenario.GetTagItem(RaunTelemetry.Attributes.Run);
        Assert.NotNull(runId);
        var run = capture.Run(runId);
        Assert.NotNull(run);
        Assert.Equal(run.Context.TraceId, link.Context.TraceId);
        Assert.NotEqual(run.TraceId, scenario.TraceId);

        // Steps share the scenario's trace and hang directly under it.
        Assert.All(steps, step =>
        {
            Assert.Equal(scenario.TraceId, step.TraceId);
            Assert.Equal(scenario.SpanId, step.ParentSpanId);
        });
        Assert.Equal("success", scenario.GetTagItem(RaunTelemetry.Attributes.TestSuiteRunStatus));
        Assert.Equal("traced scenario", scenario.GetTagItem(RaunTelemetry.Attributes.TestSuiteName));
    }

    [Fact]
    public async Task Concurrent_scenarios_keep_their_own_traces_and_step_parents()
    {
        var ids = Enumerable.Range(0, 3).Select(i => $"ctrace-{i}-" + Guid.NewGuid().ToString("N")[..6]).ToArray();
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            if (Interlocked.Increment(ref arrived) == 3)
            {
                rendezvous.TrySetResult();
            }

            await rendezvous.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }

        var definitions = ids.Select(id => Definition(id, "traced " + id, Node(0, "x", "x", invoke: Body), Node(1, "y", "y", dependsOn: [0]))).ToArray();
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        foreach (var id in ids)
        {
            var spans = capture.ForScenario(id);
            var scenario = Assert.Single(spans, s => s.DisplayName == "traced " + id);
            Assert.Null(scenario.Parent);
            var steps = spans.Where(s => s != scenario).ToList();
            Assert.Equal(2, steps.Count);
            Assert.All(steps, step =>
            {
                Assert.Equal(scenario.TraceId, step.TraceId);
                Assert.Equal(scenario.SpanId, step.ParentSpanId);
            });
        }

        // Three different traces, not one.
        Assert.Equal(3, ids.Select(id => capture.ForScenario(id).First().TraceId).Distinct().Count());
    }

    [Fact]
    public async Task A_failing_step_marks_the_scenario_span_as_a_failure()
    {
        var scenarioId = "trace-" + Guid.NewGuid().ToString("N")[..8];
        var def = Definition(scenarioId, "failing scenario",
            Node(0, "x", "x", invoke: (_, _) => throw new InvalidOperationException("boom")));
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => [def]).RunAsync(selector: null, new RecordingSink(), CancellationToken.None);

        var scenario = Assert.Single(capture.ForScenario(scenarioId), s => s.DisplayName == "failing scenario");
        Assert.Equal("failure", scenario.GetTagItem(RaunTelemetry.Attributes.TestSuiteRunStatus));
        Assert.Equal(System.Diagnostics.ActivityStatusCode.Error, scenario.Status);
    }
}
