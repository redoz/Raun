using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Testing.Platform.Builder;
using Raun.Mtp;

namespace Raun;

/// <summary>
/// Configures a Raun run against an Aspire application: which resources must be healthy before
/// scenarios start, how long to wait, and what else to register for step bodies to resolve.
/// </summary>
/// <remarks>
/// Every collection here is <b>additive</b> across calls, so a suite can compose its configuration
/// from several helpers rather than being forced into one call site.
/// </remarks>
public sealed class AspireRunOptions
{
    private readonly List<string> _waitFor = [];
    private readonly List<Action<IServiceCollection>> _services = [];
    private readonly List<Action<IDistributedApplicationTestingBuilder>> _builder = [];
    private readonly List<Action<ITestApplicationBuilder>> _testApplication = [];
    private readonly List<Func<ScenarioContext, Task>> _sessionPreflight = [];

    /// <summary>
    /// How long the whole of preflight may take — building, starting, and waiting for every resource
    /// named by <see cref="WaitFor"/>, measured together rather than per resource.
    /// </summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many scenarios may run at once against the application. <c>0</c> (the default) means the
    /// processor count; <c>1</c> runs them one after another. <c>--max-parallel-scenarios</c> overrides it.
    /// </summary>
    public int MaxParallelScenarios { get; set; }

    /// <summary>Maximum active steps per scenario; 0 means unbounded. --max-parallel-steps overrides this default.</summary>
    public int MaxParallelSteps { get; set; }

    /// <summary>
    /// What the suite reports as one test: every step or every scenario. <see langword="null"/> (the
    /// default) decides automatically — one test per step, except under Stryker.NET, where a scenario
    /// is one test. The environment variable <c>RAUN_TEST_GRANULARITY</c> overrides it for a single run.
    /// </summary>
    public TestGranularity? TestGranularity { get; set; }

    /// <summary>
    /// Names resources that must report healthy before any scenario runs. Additive: calling it twice
    /// waits for the union.
    /// </summary>
    public void WaitFor(params string[] resourceNames)
    {
        ArgumentNullException.ThrowIfNull(resourceNames);
        _waitFor.AddRange(resourceNames);
    }

    /// <summary>
    /// Registers services for step bodies to resolve through <c>ctx.Services</c>. The
    /// <see cref="Aspire.Hosting.DistributedApplication"/> is already registered as a singleton.
    /// </summary>
    public void Services(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _services.Add(configure);
    }

    /// <summary>Alters the Aspire app model before it is built — for swapping a resource out under test.</summary>
    public void ConfigureBuilder(Action<IDistributedApplicationTestingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _builder.Add(configure);
    }

    /// <summary>Configures the Microsoft.Testing.Platform host, for adding platform extensions.</summary>
    public void ConfigureTestApplication(Action<ITestApplicationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _testApplication.Add(configure);
    }

    /// <summary>
    /// Runs <paramref name="setup"/> at the start of every test session, once the application is up —
    /// for cheap resets between runs that share it, such as clearing a database. The application is
    /// started once per test host, so a host serving many sessions (Stryker.NET runs one per mutant)
    /// would otherwise carry each session's data into the next. Reach the application with
    /// <c>ctx.Aspire()</c>. Additive: callbacks run in registration order. A failure skips that
    /// session's scenarios only.
    /// </summary>
    public void SessionPreflight(Func<ScenarioContext, Task> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _sessionPreflight.Add(setup);
    }

    /// <summary>Resource names awaited during preflight, in declaration order.</summary>
    public IReadOnlyList<string> WaitForResources => _waitFor;

    internal Func<ScenarioContext, Task>? SessionPreflightCallback => _sessionPreflight.Count == 0
        ? null
        : async ctx =>
        {
            foreach (var setup in _sessionPreflight)
            {
                await setup(ctx).ConfigureAwait(false);
            }
        };

    internal void ApplyServices(IServiceCollection services)
    {
        foreach (var configure in _services)
        {
            configure(services);
        }
    }

    internal void ApplyBuilder(IDistributedApplicationTestingBuilder builder)
    {
        foreach (var configure in _builder)
        {
            configure(builder);
        }
    }

    internal void ApplyTestApplication(ITestApplicationBuilder builder)
    {
        foreach (var configure in _testApplication)
        {
            configure(builder);
        }
    }
}
