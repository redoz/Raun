using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Capabilities.TestFramework;
using Microsoft.Testing.Platform.Helpers;

namespace Raun.Mtp;

/// <summary>
/// The public bootstrap for running a Raun test project under Microsoft.Testing.Platform.
/// </summary>
/// <remarks>
/// <para>
/// This is the escape-hatch API. By default the Raun source generator emits a <c>Program.cs</c>
/// whose <c>Main</c> calls <see cref="RunAsync(string[], Action{ITestApplicationBuilder}?, bool, IServiceProvider?, Func{ScenarioContext,Task}?, int)"/>, giving
/// "just add the package" UX. Setting the MSBuild property <c>&lt;RaunGenerateProgram&gt;false&lt;/RaunGenerateProgram&gt;</c>
/// suppresses that emission so a consumer can write their own <c>Program.cs</c> and call this method
/// directly, taking full control of the host (custom MTP extensions, builder configuration, etc.)
/// via the optional <c>configure</c> callback.
/// </para>
/// </remarks>
public static class RaunTestApplication
{
    /// <summary>
    /// Builds the Microsoft.Testing.Platform host, registers Raun's <see cref="RaunTestFramework"/>,
    /// and runs it.
    /// </summary>
    /// <param name="args">The command-line arguments passed to the test executable.</param>
    /// <param name="configure">
    /// Optional callback to configure the <see cref="ITestApplicationBuilder"/> before the framework
    /// is registered (e.g. to add custom extensions). May be <see langword="null"/>.
    /// </param>
    /// <param name="simulateTime">
    /// Sample-local opt-in: when <see langword="true"/>, the registered <see cref="RaunTestFramework"/>
    /// runs scenarios on a deterministic simulated timeline (durations driven from step bodies via
    /// <c>ScenarioContext.SimulateElapsed</c>, no real waiting). Defaults to <see langword="false"/>, so
    /// the source-generated <c>Program</c> (which calls the 2-arg form) leaves production runs on real
    /// timing.
    /// </param>
    /// <param name="services">
    /// The consumer's own service provider, surfaced to step bodies as <c>ctx.Services</c> (scoped
    /// per scenario when it can supply an <c>IServiceScopeFactory</c>). Built and disposed by the
    /// consumer in their own <c>Main</c>, which is what lets registrations do async setup — an Aspire
    /// AppHost, for instance. <see langword="null"/> leaves <c>ctx.Services</c> null.
    /// </param>
    /// <param name="preflight">
    /// Run-level setup executed once before any scenario and reported as its own <c>Preflight</c>
    /// node, so a failure is a failing test rather than a process that exits before anything reports.
    /// When it fails, every scenario's steps report skipped naming preflight.
    /// </param>
    /// <param name="maxParallelScenarios">
    /// The suite's default degree of scenario parallelism: how many scenarios may run at once.
    /// <c>0</c> (the default) means the processor count; <c>1</c> runs scenarios one after another.
    /// <c>--max-parallel-scenarios &lt;n&gt;</c> overrides it per run. Steps inside a scenario stay
    /// unbounded. Scenarios that contend for the same thing declare it with <c>[Uses&lt;T&gt;]</c>.
    /// </param>
    /// <returns>The process exit code to return from <c>Main</c>.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        Action<ITestApplicationBuilder>? configure = null,
        bool simulateTime = false,
        IServiceProvider? services = null,
        Func<ScenarioContext, Task>? preflight = null,
        int maxParallelScenarios = 0)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);

        configure?.Invoke(builder);

        builder.CommandLine.AddProvider(() => new HtmlReport.HtmlReportOptionsProvider());
        builder.CommandLine.AddProvider(() => new RunOptionsProvider());

        // Opt into the platform's own filter rather than inventing a Raun dialect: this registers
        // --treenode-filter, and the framework receives the parsed TreeNodeFilter in the request.
        var extension = new RaunExtension();
#pragma warning disable TPEXP // AddTreeNodeFilterService is an experimental Microsoft.Testing.Platform API.
        builder.AddTreeNodeFilterService(extension);
#pragma warning restore TPEXP

        var stopSignal = new RunStopSignal();

        // --maximum-failed-tests is offered only to a framework that declares a graceful stop.
#pragma warning disable TPEXP // AddMaximumFailedTestsService is an experimental Microsoft.Testing.Platform API.
        builder.AddMaximumFailedTestsService(extension);
#pragma warning restore TPEXP

        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(
                new RaunBannerCapability(),
                new RaunGracefulStopCapability(stopSignal)),
            (_, serviceProvider) => new RaunTestFramework(
                serviceProvider, simulateTime, services, preflight, maxParallelScenarios, stopSignal));

        using var app = await builder.BuildAsync().ConfigureAwait(false);
        return await app.RunAsync().ConfigureAwait(false);
    }
}
