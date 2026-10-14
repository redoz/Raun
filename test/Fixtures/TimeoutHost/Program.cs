#pragma warning disable CA1303 // Output is a fixed machine-readable regression-test protocol, not localized UI.
using Raun;
using Raun.Model;
using Raun.Scheduling;

var mode = args[0];
if (mode == "process-exit") AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    Console.WriteLine("PROCESS_EXIT_HANDLER");
    Thread.Sleep(Timeout.Infinite);
};
using var host = new CancellationTokenSource();
if (mode == "already-canceled") host.Cancel();
var bodyStopped = false;
var node = new ScenarioNode
{
    Index = 0, StepId = "hung", Phase = "Given", OperationName = "Hung",
    DisplayNameTemplate = "hung step", DependsOn = [],
    Timeout = mode is "scenario" or "host" ? null : TimeSpan.FromSeconds(mode == "slow-startup" ? 0.1 : 5),
    PrepareArguments = mode == "preparation" ? _ =>
    {
        Console.WriteLine("PREPARATION");
        Thread.Sleep(Timeout.Infinite);
        return [];
    } : mode == "slow-startup" ? _ =>
    {
        Thread.Sleep(500);
        return [];
    } : null,
    Invoke = async (_, ctx) =>
    {
        Console.WriteLine("BODY");
        if (mode.StartsWith("cleanup-", StringComparison.Ordinal))
        {
            ctx.OnTeardown(Cleanup.Required, () =>
            {
                Console.WriteLine("NEXT_CLEANUP");
                return Task.CompletedTask;
            });
            ctx.OnTeardown(Cleanup.Required, async cleanup =>
            {
                Console.WriteLine("CLEANUP_ENTERED");
                using var cleanupRegistration = cleanup.CancellationToken.Register(() =>
                {
                    Console.WriteLine("CLEANUP_CANCELED");
                    if (mode == "cleanup-callback") Thread.Sleep(Timeout.Infinite);
                });
                if (mode == "cleanup-synchronous") Thread.Sleep(Timeout.Infinite);
                if (mode is "cleanup-cooperative" or "cleanup-callback")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cleanup.CancellationToken);
                }
                else await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            });
            return null;
        }
        using var registration = ctx.CancellationToken.Register(() =>
        {
            Console.WriteLine("CANCELED");
            if (mode == "callback") Thread.Sleep(Timeout.Infinite);
        });
        if (mode == "host") host.CancelAfter(100);
        ctx.OnTeardown(Cleanup.Required, () =>
        {
            Console.WriteLine(bodyStopped ? "SAFE_CLEANUP" : "EARLY_CLEANUP");
            return Task.CompletedTask;
        });
        if (mode == "synchronous") Thread.Sleep(Timeout.Infinite);
        if (mode is "cooperative" or "callback")
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken); }
            catch (OperationCanceledException) { bodyStopped = true; throw; }
        }
        else await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        bodyStopped = true;
        return null;
    },
};
var teardown = new ScenarioNode
{
    Index = 1, StepId = "cleanup", Phase = "Then", OperationName = "Teardown",
    DisplayNameTemplate = "Teardown", DependsOn = [], IsTeardown = true,
    Invoke = (_, _) => Task.FromResult<object?>(null),
};
var definition = new ScenarioDefinition
{
    ScenarioId = "timeout-fixture", DisplayName = "timeout fixture", MethodName = "Fixture.Run",
    Timeout = mode == "scenario" ? TimeSpan.FromSeconds(mode == "slow-startup" ? 0.1 : 5) : null,
    Nodes = [node, teardown],
};
var results = await new ScenarioScheduler(cancellationGracePeriod: mode == "already-canceled" ? TimeSpan.Zero : TimeSpan.FromSeconds(2),
    cleanupTimeout: TimeSpan.FromSeconds(5))
    .RunAsync(definition, cancellationToken: host.Token);
Console.WriteLine($"RESULT:{results[0].Status}");
Console.WriteLine($"TEARDOWN:{results[1].Status}");
// Prove completed operations cannot leave a watchdog that kills a later healthy host.
await Task.Delay(2500);
Console.WriteLine("COMPLETED");
