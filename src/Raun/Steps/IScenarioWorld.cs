namespace Raun;

/// <summary>
/// Per-scenario state you own: a test isolation, clients, stubs — anything a scenario's steps share
/// that is not a step result. Raun creates one per scenario, in the scenario's Setup node, before any
/// step runs, and never shares it with another scenario.
/// </summary>
/// <remarks>
/// <para>
/// Creation is a static factory rather than a constructor plus an init method, so a world that
/// exists is a world that finished initializing: its members need no <c>null!</c>, and a failure
/// half-way leaves no half-built object to dispose. Anything created before the failure is released
/// by registering it with <see cref="ScenarioContext.OnTeardown(Cleanup, Func{ScenarioContext, Task})"/>
/// as soon as it exists; Setup's cleanups run after every step's.
/// </para>
/// <para>
/// A world that implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> is disposed
/// in teardown whatever the scenario's outcome. Sibling steps of a parallel group use one world at
/// the same time: its references must not change after creation, and what they point at must be safe
/// for concurrent use.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The world type itself.</typeparam>
public interface IScenarioWorld<TSelf>
    where TSelf : class, IScenarioWorld<TSelf>
{
    /// <summary>
    /// Creates the world for one scenario. <paramref name="context"/> is the Setup node's context: its
    /// logs land on Setup, its <see cref="ScenarioContext.Scenario"/> describes the scenario being set
    /// up, and its <see cref="ScenarioContext.Services"/> is the scenario's DI scope.
    /// </summary>
#pragma warning disable CA1000 // A static abstract factory is the point: Raun calls it through the type parameter.
    static abstract ValueTask<TSelf> CreateAsync(ScenarioContext context);
#pragma warning restore CA1000
}

/// <summary>The world of a suite that has no scenario state.</summary>
public sealed class NoWorld : IScenarioWorld<NoWorld>
{
    private static readonly NoWorld Instance = new();

    private NoWorld()
    {
    }

    /// <inheritdoc />
    public static ValueTask<NoWorld> CreateAsync(ScenarioContext context) => ValueTask.FromResult(Instance);
}
