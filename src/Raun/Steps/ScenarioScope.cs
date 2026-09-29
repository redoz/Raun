using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Raun;

/// <summary>
/// One scenario's world and step-class instances. Created by the Setup node; generated code reaches
/// a step through it (<c>scope.Steps&lt;PatientSteps&gt;().Exists("Jane")</c>). Not for use in
/// scenario or step code.
/// </summary>
/// <typeparam name="TWorld">The scenario state the steps share.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ScenarioScope<TWorld>
    where TWorld : class, IScenarioWorld<TWorld>
{
    private readonly Dictionary<Type, object> _steps = [];
    private readonly IServiceProvider? _services;

    internal ScenarioScope(TWorld world, IServiceProvider? services)
    {
        World = world;
        _services = services;
    }

    /// <summary>The scenario's world.</summary>
    public TWorld World { get; }

    /// <summary>
    /// The scenario's instance of step class <typeparamref name="T"/>, created on first use (through the
    /// scenario's DI scope when there is one, so a step class may take constructor dependencies) and
    /// bound to this scope. Siblings of a parallel group asking at once get the same instance.
    /// </summary>
    public T Steps<T>()
        where T : Phase<TWorld>
    {
        lock (_steps)
        {
            if (!_steps.TryGetValue(typeof(T), out var instance))
            {
                var created = _services is null
                    ? Activator.CreateInstance<T>()
                    : ActivatorUtilities.CreateInstance<T>(_services);
                created.Bind(this);
                _steps[typeof(T)] = instance = created;
            }

            return (T)instance;
        }
    }
}

/// <summary>What a scenario's Setup node runs. Not for use in scenario or step code.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ScenarioScope
{
    /// <summary>
    /// Creates the scenario's world and scope. A disposable world is registered for disposal the
    /// moment it exists, as a required cleanup of the Setup node, so it is disposed after every
    /// step's cleanups — and after cancellation, a timeout, or a failed step.
    /// </summary>
    public static async Task<ScenarioScope<TWorld>> SetupAsync<TWorld>(ScenarioContext context)
        where TWorld : class, IScenarioWorld<TWorld>
    {
        ArgumentNullException.ThrowIfNull(context);

        var world = await TWorld.CreateAsync(context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{typeof(TWorld).Name}.CreateAsync returned null.");

        switch (world)
        {
            case IAsyncDisposable asyncDisposable:
                context.OnTeardown(Cleanup.Required, _ => asyncDisposable.DisposeAsync().AsTask());
                break;
            case IDisposable disposable:
                context.OnTeardown(Cleanup.Required, () =>
                {
                    disposable.Dispose();
                    return Task.CompletedTask;
                });
                break;
        }

        return new ScenarioScope<TWorld>(world, context.Services);
    }
}
