// CA1033: the ITestFramework / IDataProducer members are implemented explicitly so Raun's own
// SessionUid-keyed worker API (CreateTestSession/OnDiscover/...) is the visible surface; the class
// is intentionally left unsealed so later phases (and tests) can override the discover/run bodies.
#pragma warning disable CA1033 // Interface methods should be callable by child types

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Logging;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.Services;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;
using Raun.Reporting.Html;
using Raun.Running;
using Raun.Scheduling;
using ITestPlatformTestFramework = Microsoft.Testing.Platform.Extensions.TestFramework.ITestFramework;

namespace Raun.Mtp;

/// <summary>
/// Raun's own Microsoft.Testing.Platform <see cref="ITestPlatformTestFramework"/>. It owns the
/// session lifecycle and the discover-vs-run dispatch, so each scenario step can be reported as a
/// first-class MTP test node (instead of being folded onto a single scenario node, as happened
/// when Raun rode xUnit as an extension).
/// </summary>
/// <remarks>
/// <para>
/// Modeled on xUnit.net's <c>TestPlatformTestFramework</c> but without xUnit's project / config /
/// reporter / serialization machinery: Raun's <c>ScenarioScheduler</c> already owns execution, so
/// this shell only has to bridge MTP's request pipeline to it.
/// </para>
/// <para>
/// This type is public because the escape-hatch bootstrap (<see cref="RaunTestApplication.RunAsync"/>)
/// registers it; the discovery and run bodies (<see cref="OnDiscoverAsync"/> /
/// <see cref="OnExecuteAsync"/>) are filled in by later phases. The interface methods are thin
/// wrappers over the <see cref="SessionUid"/>-keyed worker methods so they can be unit-tested
/// without constructing MTP's internal session-context types.
/// </para>
/// </remarks>
public class RaunTestFramework :
    IExtension, ITestPlatformTestFramework, IDataProducer
{
    /// <summary>Stable extension UID for Raun's MTP test framework.</summary>
    internal const string ExtensionUid = "raun.mtp.testframework";

    private readonly ConcurrentDictionary<string, byte> sessions = new(StringComparer.Ordinal);
    private readonly IServiceProvider? _services;
    private readonly IServiceProvider? _userServices;
    private readonly Func<ScenarioContext, Task>? _preflight;
    private readonly bool _simulateTime;
    private readonly int _maxParallelScenarios;
    private readonly RunStopSignal? _stopSignal;

    /// <summary>Parameterless ctor for tests and the default registration path.</summary>
    public RaunTestFramework() { }

    /// <summary>Production ctor: the MTP <see cref="IServiceProvider"/> supplies command-line options
    /// (the <c>--report-html</c> flag) and the resolved results directory.</summary>
    public RaunTestFramework(IServiceProvider services) => _services = services;

    /// <summary>Carries the sample-local opt-in <paramref name="simulateTime"/> flag (off in production)
    /// from <see cref="RaunTestApplication"/>'s <c>RunAsync</c> down to the run loop / scheduler.
    /// Defaults elsewhere keep production runs on real timing.</summary>
    public RaunTestFramework(IServiceProvider services, bool simulateTime)
        : this(services) => _simulateTime = simulateTime;

    /// <summary>Production ctor with the consumer's own provider. <paramref name="services"/> is
    /// MTP's (framework-internal: command-line options, logger factory); <paramref name="userServices"/>
    /// is what reaches step bodies as <c>ctx.Services</c>. Keeping them apart is deliberate — steps
    /// must not be able to resolve platform internals.</summary>
    public RaunTestFramework(IServiceProvider services, bool simulateTime, IServiceProvider? userServices)
        : this(services, simulateTime) => _userServices = userServices;

    /// <summary>Production ctor including run-level setup. <paramref name="preflight"/> runs once
    /// before any scenario and is reported as its own node; null means no preflight node exists.</summary>
    public RaunTestFramework(
        IServiceProvider services,
        bool simulateTime,
        IServiceProvider? userServices,
        Func<ScenarioContext, Task>? preflight)
        : this(services, simulateTime, userServices) => _preflight = preflight;

    /// <summary>Production ctor including the suite's default degree of scenario parallelism
    /// (<c>0</c> = processor count, <c>1</c> = sequential); <c>--max-parallel-scenarios</c> overrides it per run.</summary>
    public RaunTestFramework(
        IServiceProvider services,
        bool simulateTime,
        IServiceProvider? userServices,
        Func<ScenarioContext, Task>? preflight,
        int maxParallelScenarios)
        : this(services, simulateTime, userServices, preflight) => _maxParallelScenarios = maxParallelScenarios;

    /// <summary>Production ctor including the platform's graceful-stop signal, so
    /// <c>--maximum-failed-tests</c> can stop the loop from admitting further scenarios.</summary>
    internal RaunTestFramework(
        IServiceProvider services,
        bool simulateTime,
        IServiceProvider? userServices,
        Func<ScenarioContext, Task>? preflight,
        int maxParallelScenarios,
        RunStopSignal? stopSignal)
        : this(services, simulateTime, userServices, preflight, maxParallelScenarios) => _stopSignal = stopSignal;

    /// <inheritdoc/>
    public string Uid => ExtensionUid;

    /// <inheritdoc/>
    public string Version => typeof(RaunTestFramework).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <inheritdoc/>
    public string DisplayName => "Raun";

    /// <inheritdoc/>
    public string Description => "Raun scenario test framework for Microsoft.Testing.Platform";

    /// <inheritdoc/>
    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1819:Properties should not return arrays", Justification = "Required by the Microsoft.Testing.Platform IDataProducer.DataTypesProduced contract.")]
    public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage)];

    Task<CreateTestSessionResult> ITestPlatformTestFramework.CreateTestSessionAsync(CreateTestSessionContext context)
        => CreateTestSession(context.SessionUid);

    Task<CloseTestSessionResult> ITestPlatformTestFramework.CloseTestSessionAsync(CloseTestSessionContext context)
        => CloseTestSession(context.SessionUid);

    async Task ITestPlatformTestFramework.ExecuteRequestAsync(ExecuteRequestContext context)
    {
        var sessionUid = context.Request.Session.SessionUid;

        if (context.Request is DiscoverTestExecutionRequest discoverRequest)
        {
            await OnDiscoverAsync(
                sessionUid,
                discoverRequest.Filter,
                context.MessageBus,
                context.Complete,
                context.CancellationToken).ConfigureAwait(false);
        }
        else if (context.Request is RunTestExecutionRequest runRequest)
        {
            await OnExecuteAsync(
                sessionUid,
                runRequest.Filter,
                context.MessageBus,
                context.Complete,
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Opens a session. Fails if the UID is already in progress.</summary>
    public Task<CreateTestSessionResult> CreateTestSession(SessionUid sessionUid)
    {
        if (!sessions.TryAdd(sessionUid.Value, 0))
        {
            return Task.FromResult(new CreateTestSessionResult
            {
                IsSuccess = false,
                ErrorMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    "Attempted to reuse session UID '{0}' already in progress",
                    sessionUid.Value),
            });
        }

        return Task.FromResult(new CreateTestSessionResult { IsSuccess = true });
    }

    /// <summary>Closes a previously opened session. Fails if the UID is unknown.</summary>
    public Task<CloseTestSessionResult> CloseTestSession(SessionUid sessionUid)
    {
        if (!sessions.TryRemove(sessionUid.Value, out _))
        {
            return Task.FromResult(new CloseTestSessionResult
            {
                IsSuccess = false,
                ErrorMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    "Attempted to close unknown session UID '{0}'",
                    sessionUid.Value),
            });
        }

        return Task.FromResult(new CloseTestSessionResult { IsSuccess = true });
    }

    /// <summary>
    /// Discover entry point keyed on <see cref="SessionUid"/> so it can be driven directly in
    /// tests. Guards that the session is active, then defers to <see cref="OnDiscoverAsync"/>.
    /// </summary>
    public ValueTask OnDiscover(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        EnsureSession(sessionUid, "discovery");
        return OnDiscoverAsync(sessionUid, filter, messageBus, operationComplete, cancellationToken);
    }

    /// <summary>
    /// Run entry point keyed on <see cref="SessionUid"/> so it can be driven directly in tests.
    /// Guards that the session is active, then defers to <see cref="OnExecuteAsync"/>.
    /// </summary>
    public ValueTask OnExecute(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        EnsureSession(sessionUid, "execution");
        return OnExecuteAsync(sessionUid, filter, messageBus, operationComplete, cancellationToken);
    }

    /// <summary>
    /// Performs discovery for the session: enumerates every scenario registered in
    /// <see cref="ScenarioRegistry"/>, lowers it via its factory, and publishes one
    /// <see cref="TestNodeUpdateMessage"/> per step node (see <see cref="RaunDiscoverer"/>).
    /// Step nodes only — no parent scenario node (design decision ①).
    /// </summary>
    protected virtual async ValueTask OnDiscoverAsync(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        // A null/no-op filter discovers everything (so --list-tests enumerates every step); a
        // TestNodeUidListFilter restricts discovery to the named nodes, matching xUnit's discovery
        // sink. The same parsing backs both discover and run so a single-step uid resolves identically.
        var selector = ReadSelector(filter, out var unsupported);
        await WarnUnsupportedFilterAsync(unsupported).ConfigureAwait(false);

        // Announced like any test, but never executed here: a discovery request must not start
        // containers or migrate databases.
        if (_preflight is not null)
        {
            foreach (var node in RaunDiscoverer.BuildNodes(Preflight.Definition(_preflight), selector))
            {
                NodeDiagnostics.Log("discover", node);
                await messageBus
                    .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                    .ConfigureAwait(false);
            }
        }

        foreach (var methodName in ScenarioRegistry.RegisteredMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ScenarioRegistry.TryGet(methodName, out var factory) || factory is null)
            {
                continue;
            }

            var definition = factory();
            foreach (var node in RaunDiscoverer.BuildNodes(definition, selector))
            {
                NodeDiagnostics.Log("discover", node);
                await messageBus
                    .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                    .ConfigureAwait(false);
            }
        }

        operationComplete();
    }

    /// <summary>
    /// Runs the requested tests for the session via the <see cref="RunLoop"/>: the filter is
    /// reduced to the distinct scenarios it selects, each is run once through a
    /// <see cref="ScenarioScheduler"/>, and every executed step is published (siblings light up).
    /// </summary>
    protected virtual async ValueTask OnExecuteAsync(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        var selector = ReadSelector(filter, out var unsupported);
        await WarnUnsupportedFilterAsync(unsupported).ConfigureAwait(false);

        var sinks = new List<IRunEventSink> { new MtpReportSink(sessionUid, messageBus, this) };
        if (HtmlReport.HtmlReportPath.Resolve(_services) is { } reportPath)
        {
            sinks.Add(new HtmlReportSink(reportPath, TimeProvider.System));
        }

        var bus = new RunEventBus(sinks);
        var loop = new RunLoop(
            EnumerateRegisteredScenarios,
            simulateTime: _simulateTime,
            services: _userServices,
            preflight: _preflight,
            maxParallelScenarios: ScenarioParallelism.Resolve(_services, _maxParallelScenarios),
            stopSignal: _stopSignal);
        await loop.RunAsync(selector, bus, cancellationToken).ConfigureAwait(false);

        if (bus.Failures.Count > 0)
        {
            // Surface each sink failure through MTP's logger so a broken --report-html write is
            // visible to the user via --diagnostic output (design §3.A/§3.E). The debug
            // NodeDiagnostics path is kept as a fallback for the unit-test code path where
            // _services is null (parameterless ctor).
            ILogger? logger = _services?.GetLoggerFactory().CreateLogger(typeof(RaunTestFramework).FullName!);
            foreach (var failure in bus.Failures)
            {
                if (logger is not null)
                {
                    await logger.LogWarningAsync($"report sink failure: {failure}").ConfigureAwait(false);
                }

                NodeDiagnostics.Log("report-sink-failure", failure.ToString());
            }
        }

        operationComplete();
    }

    /// <summary>
    /// Reduces an MTP execution filter to the <see cref="NodeSelector"/> that decides which steps the
    /// request selects, or <see langword="null"/> to select everything. A
    /// <see cref="TestNodeUidListFilter"/> carries an explicit uid list, a
    /// <see cref="TreeNodeFilter"/> a path expression, and a null or no-op filter means "run all".
    /// A filter type Raun does not recognize selects everything and names itself in
    /// <paramref name="unsupported"/>: over-selecting is recoverable and visible, whereas aborting
    /// would turn a future platform filter into a failed run the user cannot act on.
    /// </summary>
    private static NodeSelector? ReadSelector(ITestExecutionFilter? filter, out string? unsupported)
    {
        unsupported = null;
        switch (filter)
        {
            case null:
                return null;
#pragma warning disable TPEXP // NopFilter is an experimental Microsoft.Testing.Platform API.
            case NopFilter:
#pragma warning restore TPEXP
                return null;
            case TestNodeUidListFilter uidFilter:
                return new UidNodeSelector(
                    uidFilter.TestNodeUids.Select(u => u.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
#pragma warning disable TPEXP // TreeNodeFilter is an experimental Microsoft.Testing.Platform API.
            case TreeNodeFilter treeFilter:
                return new TreeNodeSelector(treeFilter);
#pragma warning restore TPEXP
            default:
                unsupported = filter.GetType().FullName;
                return null;
        }
    }

    /// <summary>Warns that a filter type was ignored, through the platform logger when there is one.</summary>
    private async Task WarnUnsupportedFilterAsync(string? unsupported)
    {
        if (unsupported is null)
        {
            return;
        }

        var message = $"ignoring unsupported execution filter type '{unsupported}'; selecting every test";
        ILogger? logger = _services?.GetLoggerFactory().CreateLogger(typeof(RaunTestFramework).FullName!);
        if (logger is not null)
        {
            await logger.LogWarningAsync(message).ConfigureAwait(false);
        }

        NodeDiagnostics.Log("unsupported-filter", message);
    }

    /// <summary>Lowers every scenario registered in <see cref="ScenarioRegistry"/> to its definition.</summary>
    private static IEnumerable<ScenarioDefinition> EnumerateRegisteredScenarios()
    {
        foreach (var methodName in ScenarioRegistry.RegisteredMethods)
        {
            if (ScenarioRegistry.TryGet(methodName, out var factory) && factory is not null)
            {
                yield return factory();
            }
        }
    }

    private void EnsureSession(SessionUid sessionUid, string requestKind)
    {
        if (!sessions.ContainsKey(sessionUid.Value))
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Attempted to run {0} request against unknown session UID '{1}'",
                    requestKind,
                    sessionUid.Value),
                nameof(sessionUid));
        }
    }
}
