namespace Raun.Mtp;

/// <summary>
/// Makes a preflight run once per test host process rather than once per test session.
/// </summary>
/// <remarks>
/// <para>
/// One host can serve many sessions: an IDE in server mode, or a mutation-testing tool such as
/// Stryker.NET, which reuses one host for every test run it requests. Preflight is run-level setup —
/// typically starting an application — so running it again in each session would start a second copy
/// next to the first, which usually fails and skips every scenario.
/// </para>
/// <para>
/// Every session still reports its own preflight node. A later session waits for the start that is
/// already running, or passes straight away when that start succeeded. A start that failed or was
/// cancelled is retried by the next session, so a fixed environment (a container runtime started,
/// say) does not need a new host.
/// </para>
/// </remarks>
internal sealed class ProcessPreflight
{
    private readonly Func<ScenarioContext, Task> preflight;
    private readonly object gate = new();
    private Lazy<Task>? attempt;

    public ProcessPreflight(Func<ScenarioContext, Task> preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        this.preflight = preflight;
    }

    /// <summary>
    /// Builds the body of each session's preflight node: the process-wide <paramref name="preflight"/>
    /// (started once, shared by every session), then <paramref name="sessionPreflight"/> (run in
    /// every session). <see langword="null"/> when there is neither, so no preflight node exists.
    /// </summary>
    public static Func<ScenarioContext, Task>? Compose(
        Func<ScenarioContext, Task>? preflight,
        Func<ScenarioContext, Task>? sessionPreflight)
    {
        Func<ScenarioContext, Task>? process = preflight is null ? null : new ProcessPreflight(preflight).RunAsync;
        if (sessionPreflight is null)
        {
            return process;
        }

        return async context =>
        {
            if (process is not null)
            {
                await process(context).ConfigureAwait(false);
            }

            context.Log("running session preflight");
            await sessionPreflight(context).ConfigureAwait(false);
        };
    }

    public Task RunAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Lazy<Task> current;
        bool started;
        lock (gate)
        {
            // A value not yet created is a start whose first caller is just about to make it.
            started = attempt is null
                || (attempt.IsValueCreated && (attempt.Value.IsFaulted || attempt.Value.IsCanceled));
            if (started)
            {
                // Invoked outside the lock: the delegate's synchronous part may be long.
                attempt = new Lazy<Task>(() => preflight(context));
            }

            current = attempt!;
        }

        if (!started)
        {
            context.Log("preflight already ran in this test host; reusing it");
        }

        return current.Value.WaitAsync(context.CancellationToken);
    }
}
