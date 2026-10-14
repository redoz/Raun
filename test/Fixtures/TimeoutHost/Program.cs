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
    Timeout = mode is "scenario" or "host" ? null : TimeSpan.FromMilliseconds(100),
    PrepareArguments = mode == "preparation" ? _ =>
    {
        Console.WriteLine("PREPARATION");
        Thread.Sleep(Timeout.Infinite);
        return [];
    } : null,
    Invoke = async (_, ctx) =>
    {
        Console.WriteLine("BODY");
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
    Timeout = mode == "scenario" ? TimeSpan.FromMilliseconds(100) : null,
    Nodes = [node, teardown],
};
var results = await new ScenarioScheduler(cancellationGracePeriod: mode == "already-canceled" ? TimeSpan.Zero : TimeSpan.FromMilliseconds(250))
    .RunAsync(definition, cancellationToken: host.Token);
Console.WriteLine($"RESULT:{results[0].Status}");
// Prove completed operations cannot leave a watchdog that kills a later healthy host.
await Task.Delay(500);
Console.WriteLine("COMPLETED");
