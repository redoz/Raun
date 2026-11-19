using System.Diagnostics;

namespace Raun.Scheduling;

/// <summary>Owns cancellation deadlines independently of user code and worker-pool progress.</summary>
internal sealed class CancellationWatchdog : IDisposable
{
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(60);
    private readonly object gate = new();
    private readonly List<Operation> operations = [];
    private readonly TimeSpan grace;
    private readonly CancellationToken hostToken;
    private readonly Thread thread;
    private readonly Func<long> timestamp;
    private readonly Func<string, Task>? beforeTermination;
    private readonly Action? afterPass;
    private bool disposed;
    private Operation? scenario;

    /// <summary>How long <c>beforeTermination</c> may take before the host is terminated regardless.</summary>
    internal static readonly TimeSpan TerminationFlushBudget = TimeSpan.FromSeconds(5);

    // timestamp: the clock, in Stopwatch ticks; a test replaces it.
    // beforeTermination: given the reason, a bounded last chance to write reports before the host is killed.
    // afterPass: called each time the supervising thread has finished looking at every operation.
    internal CancellationWatchdog(
        TimeSpan grace, CancellationToken hostToken, Func<long>? timestamp = null, Func<string, Task>? beforeTermination = null,
        Action? afterPass = null)
    {
        this.afterPass = afterPass;
        ValidateGrace(grace);
        this.grace = grace;
        this.hostToken = hostToken;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.beforeTermination = beforeTermination;
        thread = new Thread(Supervise) { IsBackground = true, Name = "Raun cancellation watchdog" };
        thread.Start();
    }

    internal static void ValidateGrace(TimeSpan grace)
    {
        if (grace < TimeSpan.Zero || grace.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(grace), grace, "Cancellation grace must be between zero and Int32.MaxValue milliseconds.");
        }
    }

    internal Operation Watch(CancellationTokenSource source, TimeSpan? timeout, string description, bool isScenario = false)
        => Register(source, timeout, description, isScenario, timeoutRunning: true);

    /// <summary>Watches an operation that is cancelled with the scenario from now on, and whose own
    /// timeout counts from <see cref="Operation.StartTimeout"/>.</summary>
    internal Operation WatchBeforeItsTimeoutStarts(CancellationTokenSource source, TimeSpan? timeout, string description)
        => Register(source, timeout, description, isScenario: false, timeoutRunning: false);

    private Operation Register(CancellationTokenSource source, TimeSpan? timeout, string description, bool isScenario, bool timeoutRunning)
    {
        lock (gate)
        {
            var operation = new Operation(this, source, timeout, description, timeoutRunning);
            operations.Add(operation);
            if (isScenario) scenario = operation;
            else if (scenario?.CancellationStarted == true) operation.RequestCancellation();
            Monitor.PulseAll(gate);
            return operation;
        }
    }

    private void Supervise()
    {
        while (true)
        {
            string? fatal = null;
            lock (gate)
            {
                if (disposed) return;

                // One reading of the clock for the whole pass. This thread can lose the processor
                // between two operations; with a reading per operation, time that passed in that gap
                // let a step's deadline latch as the step's own timeout in a pass that had just
                // judged the scenario's deadline, which is at least as old, as not yet reached.
                var now = timestamp();

                if (scenario is { Completed: false } current)
                {
                    TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios(current, now);
                    Expire(current, now);
                }
                foreach (var operation in operations)
                {
                    if (operation.Completed) continue;
                    if (operation != scenario) Expire(operation, now);
                    if (operation == scenario && !operations.Any(other => other != scenario && !other.Completed)) continue;
                    if (operation.CancellationStarted && Stopwatch.GetElapsedTime(operation.CanceledAt, now) >= grace)
                    {
                        fatal = $"Raun: {operation.Description} did not stop within cancellation grace {grace}. Terminating the test host; cleanup and reports may be incomplete.";
                        break;
                    }
                }
                if (fatal is null)
                {
                    afterPass?.Invoke();
                    Monitor.Wait(gate, TimeSpan.FromMilliseconds(10));
                }
            }
            if (fatal is not null) Terminate(fatal, beforeTermination);
        }
    }

    // When a step's deadline and the scenario's have both passed, the one that passed first owns
    // the timeout, and on a tie the scenario does. The supervising thread can be kept off the
    // processor for longer than the distance between the two, so the order it looks in decides
    // nothing: the deadlines do.
    private void TimeOutStepsWhoseDeadlinePassedBeforeTheScenarios(Operation scenarioOperation, long now)
    {
        if (hostToken.IsCancellationRequested || scenarioOperation.CancellationStarted || Overdue(scenarioOperation, now) is not { } scenarioOverdue)
        {
            return;
        }

        foreach (var operation in operations)
        {
            if (operation != scenarioOperation && !operation.Completed && !operation.Source.IsCancellationRequested
                && Overdue(operation, now) is { } overdue && overdue > scenarioOverdue)
            {
                operation.RequestCancellation(timedOut: true);
            }
        }
    }

    private static TimeSpan? Overdue(Operation operation, long now)
        => operation.Timeout is { } timeout && operation.TimeoutStartedAt is { } started
            && Stopwatch.GetElapsedTime(started, now) - timeout is var overdue && overdue >= TimeSpan.Zero
            ? overdue
            : null;

    private void Expire(Operation operation, long now)
    {
        var cancelScenario = hostToken.IsCancellationRequested || scenario?.CancellationStarted == true;
        if (cancelScenario || operation.Source.IsCancellationRequested || Overdue(operation, now) is not null)
        {
            operation.RequestCancellation(timedOut: !cancelScenario && !operation.Source.IsCancellationRequested);
        }
    }

    /// <summary>How long the reason may take to reach standard error before the host is terminated regardless.</summary>
    internal static readonly TimeSpan TerminationMessageBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs <paramref name="work"/> for at most <paramref name="budget"/>, on a thread of its own.
    /// Not on the thread pool: the host is being terminated because something does not return, and
    /// that something is usually sitting on pool threads. Queued behind it, the reason for the
    /// termination did not always reach standard error in time.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Last words before the process is killed; a failure to say them must not stop the kill.")]
    private static void Bounded(Action work, TimeSpan budget)
    {
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception) { /* termination still happens */ }
        })
        { IsBackground = true, Name = "Raun termination" };
        thread.Start();
        thread.Join(budget);
    }

    private static void Terminate(string message, Func<string, Task>? beforeTermination)
    {
        // A replaced/blocked stderr must not hold the watchdog hostage. Diagnostics get a small,
        // bounded chance to reach the parent, and reporting gets a bounded chance to write what the
        // run produced so far; process termination never waits on either beyond its budget.
        try
        {
            Bounded(() => Console.Error.WriteLine(message), TerminationMessageBudget);
            if (beforeTermination is not null)
            {
                Bounded(() => beforeTermination(message).GetAwaiter().GetResult(), TerminationFlushBudget);
            }
        }
        finally
        {
            // Kill bypasses managed ProcessExit handlers, which can otherwise hang without bound.
            try
            {
                using var process = Process.GetCurrentProcess();
                process.Kill();
            }
            finally
            {
                Environment.FailFast(message); // fail closed if OS termination cannot complete
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            // An observer or scheduler fault must not disable supervision of still-live bodies.
            if (operations.Any(operation => !operation.Completed))
            {
                foreach (var operation in operations) operation.RequestCancellation();
            }
        }
        scenario?.Dispose();
        lock (gate)
        {
            while (operations.Any(operation => !operation.Completed)) Monitor.Wait(gate);
            disposed = true;
            Monitor.PulseAll(gate);
        }
        thread.Join();
    }

    internal sealed class Operation(CancellationWatchdog owner, CancellationTokenSource source, TimeSpan? timeout, string description, bool timeoutRunning = true) : IDisposable
    {
        internal CancellationTokenSource Source { get; } = source;
        internal TimeSpan? Timeout { get; } = timeout;
        internal string Description { get; } = description;
        internal long? TimeoutStartedAt { get; private set; } = timeoutRunning ? owner.timestamp() : null;

        internal void StartTimeout()
        {
            lock (owner.gate)
            {
                TimeoutStartedAt ??= owner.timestamp();
                Monitor.PulseAll(owner.gate);
            }
        }

        internal long CanceledAt { get; private set; }
        private volatile bool cancellationStarted;
        internal bool CancellationStarted => cancellationStarted;
        internal bool Completed { get; private set; }
        private volatile bool timedOut;
        internal bool TimedOut => timedOut;
        private Task callbacks = Task.CompletedTask;

        internal void RequestCancellation(bool timedOut = false)
        {
            lock (owner.gate)
            {
                if (Completed || CancellationStarted) return;
                CanceledAt = owner.timestamp();
                this.timedOut = timedOut;
                cancellationStarted = true;
                callbacks = Source.CancelAsync();
                if (this == owner.scenario)
                {
                    foreach (var operation in owner.operations)
                    {
                        if (operation != this) operation.RequestCancellation();
                    }
                }
                Monitor.PulseAll(owner.gate);
            }
        }

        public void Dispose() => CompleteAsync().GetAwaiter().GetResult();

        internal async Task CompleteAsync()
        {
            Task cancellation;
            lock (owner.gate)
            {
                if (Completed) return;
                // The body has stopped; no new timeout may start while this handle is completing.
                if (!CancellationStarted) Completed = true;
                cancellation = callbacks;
            }
            try
            {
                await cancellation.ConfigureAwait(false);
            }
            finally
            {
                lock (owner.gate)
                {
                    Completed = true;
                    Monitor.PulseAll(owner.gate);
                }
            }
        }
    }
}
