using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Raun.Mtp;
using Microsoft.Extensions.DependencyInjection;

namespace Raun;

/// <summary>
/// Bootstrap for a Raun suite that drives an Aspire application. Call it from your own
/// <c>Program.cs</c> (with <c>&lt;RaunGenerateProgram&gt;false&lt;/RaunGenerateProgram&gt;</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ships <b>no phase markers and no steps</b>. The DSL is yours; everything here is reachable from a
/// step you write, through <c>ctx.Services</c> or <see cref="ScenarioContextAspireExtensions.Aspire"/>.
/// </para>
/// <para>
/// The application is built here but <b>started as the run's preflight</b>, so startup and the wait
/// for resource health are a reported, timed test node instead of work that happens before the test
/// platform exists — a failed start is then a failing test rather than a process that exits before
/// anything reports.
/// </para>
/// </remarks>
public static class RaunAspire
{
    /// <summary>Builds the AppHost, runs the suite against it, and disposes it.</summary>
    /// <typeparam name="TAppHost">The AppHost project, via its generated <c>Projects.*</c> type.</typeparam>
    /// <param name="args">The command-line arguments passed to the test executable.</param>
    /// <param name="configure">Declares what to wait for and what to register.</param>
    /// <returns>The process exit code to return from <c>Main</c>.</returns>
    public static async Task<int> RunAsync<TAppHost>(
        string[] args,
        Action<AspireRunOptions>? configure = null)
        where TAppHost : class
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new AspireRunOptions();
        configure?.Invoke(options);

        // Nothing Aspire-related happens until preflight runs. A discovery request (--list-tests,
        // Test Explorer populating) must not build an AppHost, and must certainly not probe for a
        // container runtime — which is what disposing a built-but-unstarted application does.
        var holder = new ApplicationHolder();

        try
        {
            var services = new ServiceCollection();
            services.AddSingleton(_ => holder.Application
                ?? throw new InvalidOperationException(
                    "The Aspire application is not available: preflight has not run."));
            options.ApplyServices(services);

            var provider = services.BuildServiceProvider();
            await using (provider.ConfigureAwait(false))
            {
                return await RaunTestApplication.RunAsync(
                    args,
                    configure: options.ApplyTestApplication,
                    services: provider,
                    preflight: ctx => StartAsync<TAppHost>(holder, options, ctx),
                    maxParallelScenarios: options.MaxParallelScenarios).ConfigureAwait(false);
            }
        }
        finally
        {
            if (holder.Application is { } app)
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Holds the application once preflight has built it, so the DI registration can be
    /// made before it exists.</summary>
    private sealed class ApplicationHolder
    {
        public DistributedApplication? Application { get; set; }
    }

    /// <summary>
    /// The preflight body: build the app, start it, then wait for each declared resource to report
    /// healthy, logging each transition onto the preflight node. The whole of it shares one
    /// <see cref="AspireRunOptions.StartupTimeout"/> rather than one timeout per resource.
    /// </summary>
    private static async Task StartAsync<TAppHost>(
        ApplicationHolder holder, AspireRunOptions options, ScenarioContext ctx)
        where TAppHost : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        timeout.CancelAfter(options.StartupTimeout);

        var healthy = new List<string>();
        DistributedApplication? app = null;
        try
        {
            ctx.Log("building AppHost");
            var builder = await DistributedApplicationTestingBuilder
                .CreateAsync<TAppHost>(timeout.Token)
                .ConfigureAwait(false);
            options.ApplyBuilder(builder);

            app = await builder.BuildAsync(timeout.Token).ConfigureAwait(false);
            holder.Application = app;

            ctx.Log("starting AppHost");
            await app.StartAsync(timeout.Token).ConfigureAwait(false);

            foreach (var resource in options.WaitForResources)
            {
                var started = ctx.TimeProvider.GetUtcNow();
                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync(resource, timeout.Token)
                    .ConfigureAwait(false);

                healthy.Add(resource);
                ctx.Log($"{resource} → Healthy ({(ctx.TimeProvider.GetUtcNow() - started).TotalSeconds:0.0}s)");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !ctx.CancellationToken.IsCancellationRequested)
        {
            // This message is the primary diagnostic when an app will not come up, so it has to be
            // self-sufficient: what was awaited, what made it, and what each straggler's last state was.
            throw new TimeoutException(BuildTimeoutMessage(app, options, healthy));
        }
    }

    private static string BuildTimeoutMessage(
        DistributedApplication? app, AspireRunOptions options, List<string> healthy)
    {
        var pending = options.WaitForResources.Where(r => !healthy.Contains(r)).ToList();

        var message = new System.Text.StringBuilder()
            .Append("Aspire startup exceeded the ")
            .Append(options.StartupTimeout)
            .AppendLine(" StartupTimeout.")
            .Append("  awaited: ")
            .AppendLine(string.Join(", ", options.WaitForResources))
            .Append("  healthy: ")
            .AppendLine(healthy.Count == 0 ? "(none)" : string.Join(", ", healthy));

        foreach (var resource in pending)
        {
            var state = app is not null
                && app.ResourceNotifications.TryGetCurrentState(resource, out var current)
                ? current.Snapshot.State?.Text ?? "(no state)"
                : "(unknown resource)";
            message.Append("  pending: ").Append(resource).Append(" — last state ").AppendLine(state);
        }

        return message.ToString();
    }
}

/// <summary>Reaches the running Aspire application from a step.</summary>
public static class ScenarioContextAspireExtensions
{
    /// <summary>
    /// The <see cref="DistributedApplication"/> this run is driving. Aspire's own extensions
    /// (<c>CreateHttpClient</c>, <c>GetEndpoint</c>, <c>GetConnectionStringAsync</c>) hang off it, so
    /// this returns the application rather than wrapping it in a facade that would only forward.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run was not started by
    /// <see cref="RaunAspire.RunAsync{TAppHost}"/>.</exception>
    public static DistributedApplication Aspire(this ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Services?.GetService<DistributedApplication>()
            ?? throw new InvalidOperationException(
                "No DistributedApplication is registered. Start the run with "
                + "RaunAspire.RunAsync<TAppHost>(args, ...) from your own Program.cs "
                + "(and set <RaunGenerateProgram>false</RaunGenerateProgram>).");
    }
}
