using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Testing.Platform.Builder;

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

    /// <summary>Resource names awaited during preflight, in declaration order.</summary>
    public IReadOnlyList<string> WaitForResources => _waitFor;

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
