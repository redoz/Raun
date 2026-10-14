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
    private bool disposed;
    private Operation? scenario;

    internal CancellationWatchdog(TimeSpan grace, CancellationToken hostToken)
    {
        ValidateGrace(grace);
        this.grace = grace;
        this.hostToken = hostToken;
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
    {
        lock (gate)
        {
            var operation = new Operation(this, source, timeout, description);
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
                foreach (var operation in operations)
                {
                    if (operation.Completed) continue;
                    var cancelScenario = hostToken.IsCancellationRequested || scenario?.CancellationStarted == true;
                    if (cancelScenario || operation.Source.IsCancellationRequested ||
                        (operation.Timeout is { } timeout && Stopwatch.GetElapsedTime(operation.Started) >= timeout))
                    {
                        operation.RequestCancellation(timedOut: !cancelScenario && !operation.Source.IsCancellationRequested);
                    }
                    if (operation == scenario && !operations.Any(other => other != scenario && !other.Completed)) continue;
                    if (operation.CancellationStarted && Stopwatch.GetElapsedTime(operation.CanceledAt) >= grace)
                    {
                        fatal = $"Raun: {operation.Description} did not stop within cancellation grace {grace}. Terminating the test host; cleanup and reports may be incomplete.";
                        break;
                    }
                }
                if (fatal is null) Monitor.Wait(gate, TimeSpan.FromMilliseconds(10));
            }
            if (fatal is not null) Terminate(fatal);
        }
    }

    private static void Terminate(string message)
    {
        // A replaced/blocked stderr must not hold the watchdog hostage. Diagnostics get a small,
        // bounded chance to reach the parent; process termination never waits on reporting sinks.
        try
        {
            Task.Run(() => Console.Error.WriteLine(message)).Wait(TimeSpan.FromMilliseconds(100));
        }
        catch (AggregateException)
        {
            // Termination still happens when diagnostic output fails.
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

    internal sealed class Operation(CancellationWatchdog owner, CancellationTokenSource source, TimeSpan? timeout, string description) : IDisposable
    {
        internal CancellationTokenSource Source { get; } = source;
        internal TimeSpan? Timeout { get; } = timeout;
        internal string Description { get; } = description;
        internal long Started { get; } = Stopwatch.GetTimestamp();
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
                CanceledAt = Stopwatch.GetTimestamp();
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
