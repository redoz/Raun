using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Extensions.OutputDevice;
using Microsoft.Testing.Platform.Extensions.TestFramework;
using Microsoft.Testing.Platform.Logging;
using Microsoft.Testing.Platform.OutputDevice;
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
/// The interface methods are thin wrappers over the <see cref="SessionUid"/>-keyed worker methods
/// so they can be unit-tested without constructing MTP's internal session-context types.
/// </para>
/// </remarks>
internal sealed class RaunTestFramework :
    IExtension, ITestPlatformTestFramework, IDataProducer, IOutputDeviceDataProducer
{
    /// <summary>Stable extension UID for Raun's MTP test framework.</summary>
    internal const string ExtensionUid = "raun.mtp.testframework";

    private readonly ConcurrentDictionary<string, byte> sessions = new(StringComparer.Ordinal);
    private readonly IServiceProvider _services;
    private readonly IServiceProvider? _userServices;
    private readonly Func<ScenarioContext, Task>? _preflight;
    private readonly bool _simulateTime;
    private readonly int _maxParallelScenarios;
    private readonly int _maxParallelSteps;
    private readonly RunStopRelay? _stopRelay;
    private readonly IRaunCoverageIntegration? _coverage;

    private readonly TestGranularity _granularity;

    /// <summary>
    /// <paramref name="services"/> is MTP's provider (framework-internal: command-line options,
    /// configuration, logger factory); <paramref name="userServices"/> is what reaches step bodies as
    /// <c>ctx.Services</c>. Keeping them apart is deliberate: steps must not be able to resolve
    /// platform internals. <paramref name="preflight"/> runs once before any scenario and is reported
    /// as its own node. The parallelism limits are the suite's defaults (<c>0</c> = processor count
    /// for scenarios, unlimited for steps), which the command line overrides per run.
    /// <paramref name="stopRelay"/> lets <c>--maximum-failed-tests</c> stop the run in progress from
    /// admitting further scenarios. <paramref name="environment"/> stands in for the process environment.
    /// </summary>
    internal RaunTestFramework(
        IServiceProvider services,
        bool simulateTime = false,
        IServiceProvider? userServices = null,
        Func<ScenarioContext, Task>? preflight = null,
        int maxParallelScenarios = 0,
        RunStopRelay? stopRelay = null,
        IRaunCoverageIntegration? coverage = null,
        int maxParallelSteps = 0,
        TestGranularity? testGranularity = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelSteps);
        _services = services;
        _simulateTime = simulateTime;
        _userServices = userServices;
        _preflight = preflight;
        _maxParallelScenarios = maxParallelScenarios;
        _stopRelay = stopRelay;
        _coverage = coverage;
        _maxParallelSteps = maxParallelSteps;
        _granularity = TestGranularityResolver.Resolve(testGranularity, environment ?? Environment.GetEnvironmentVariable);
    }

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
    public Type[] DataTypesProduced => [typeof(TestNodeUpdateMessage), typeof(SessionFileArtifact)];

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

        // A session that attached nothing must not leave an empty folder behind in the results
        // directory; anything it did write stays, because that is what the user asked for.
        RaunAttachments.CleanUp(RaunAttachments.Resolve(_services, sessionUid));

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
    /// Step nodes only — no parent scenario node.
    /// </summary>
    private async ValueTask OnDiscoverAsync(
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
            foreach (var node in DiscoverNodes(Preflight.Definition(_preflight), selector))
            {
                NodeDiagnostics.Log("discover", node);
                await messageBus
                    .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                    .ConfigureAwait(false);
            }
        }

        foreach (var definition in ScenarioRegistry.Definitions())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var node in DiscoverNodes(definition, selector))
            {
                NodeDiagnostics.Log("discover", node);
                await messageBus
                    .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                    .ConfigureAwait(false);
            }
        }

        operationComplete();
    }

    private IReadOnlyList<TestNode> DiscoverNodes(ScenarioDefinition definition, NodeSelector? selector)
    {
        if (_granularity == TestGranularity.Step)
        {
            return RaunDiscoverer.BuildNodes(definition, selector);
        }

        return RaunDiscoverer.BuildScenarioNode(definition, selector) is { } node ? [node] : [];
    }

    /// <summary>
    /// Runs the requested tests for the session via the <see cref="RunLoop"/>: the filter is
    /// reduced to the distinct scenarios it selects, each is run once through a
    /// <see cref="ScenarioScheduler"/>, and every executed step is published (siblings light up).
    /// </summary>
    private async ValueTask OnExecuteAsync(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        var selector = ReadSelector(filter, out var unsupported);
        await WarnUnsupportedFilterAsync(unsupported).ConfigureAwait(false);

        var attachmentRoot = RaunAttachments.Resolve(_services, sessionUid);
        var sinks = new List<IRunEventSink>
        {
            _granularity == TestGranularity.Step
                ? new MtpReportSink(sessionUid, messageBus, this, attachmentRoot)
                : new ScenarioMtpReportSink(sessionUid, messageBus, this, attachmentRoot),
        };
        var reportPath = HtmlReport.HtmlReportPath.Resolve(_services);
        HtmlReportSink? report = null;
        if (reportPath is not null)
        {
            var coverageRequested = _coverage is not null
                && (_services.GetCommandLineOptions().IsOptionSet("coverage") || _services.GetCommandLineOptions().IsOptionSet("coverlet"));
            report = new HtmlReportSink(reportPath, TimeProvider.System, coverageRequested, HtmlReport.HtmlReportPath.ResolveSourceOptions(_services));
            sinks.Add(report);
            if (coverageRequested)
            {
                await _coverage!.AttachReportAsync(sessionUid.Value, report).ConfigureAwait(false);
            }
        }

        var bus = new RunEventBus(sinks);

        // A stop signal of this run's own: a stop asked for in an earlier session of the same host
        // (--maximum-failed-tests crossed) must not keep this one from admitting anything.
        using var stop = _stopRelay?.Begin();
        var loop = new RunLoop(
            EnumerateRegisteredScenarios,
            simulateTime: _simulateTime,
            services: _userServices,
            preflight: _preflight,
            maxParallelScenarios: ScenarioParallelism.Resolve(_services, _maxParallelScenarios),
            stopSignal: stop?.Signal,
            cancellationGracePeriod: CancellationGrace.Resolve(_services),
            cleanupTimeout: CleanupTimeout.Resolve(_services),
            maxParallelSteps: StepParallelism.Resolve(_services, _maxParallelSteps));
        var runFailures = new List<Exception>();
        try
        {
            await loop.RunAsync(selector, bus, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Something failed outside any step, after every scenario that could run did. Letting it
            // escape would end the host with an unhandled exception and no summary; a failed node of
            // its own makes the run fail and says why.
            runFailures.Add(ex);
        }

        // The reporter to the platform is the one sink that is not best effort. The bus keeps a
        // broken sink from stopping the run, which is right for a report file; a result that never
        // reached the platform leaves a node in progress and the run looking greener than it was.
        if (bus.FailureOf(sinks[0]) is { } unreported)
        {
            runFailures.Add(new InvalidOperationException(
                $"A result could not be reported to the test platform, so this run's results are incomplete: {unreported.GetType().Name}: {unreported.Message}",
                unreported));
        }

        if (runFailures.Count > 0)
        {
            await PublishRunFailureAsync(
                sessionUid, messageBus, runFailures.Count == 1 ? runFailures[0] : new AggregateException(runFailures)).ConfigureAwait(false);
        }

        if (bus.Failures.Count > 0)
        {
            // Through MTP's logger, so a broken --report-html write shows in --diagnostic output.
            var logger = _services.GetLoggerFactory().CreateLogger(typeof(RaunTestFramework).FullName!);
            foreach (var failure in bus.Failures)
            {
                await logger.LogWarningAsync($"report sink failure: {failure}").ConfigureAwait(false);
                NodeDiagnostics.Log("report-sink-failure", failure.ToString());
            }
        }

        if (report is not null)
        {
            await AnnounceReportAsync(sessionUid, messageBus, reportPath!, bus.FailureOf(report), cancellationToken).ConfigureAwait(false);
        }

        operationComplete();
    }

    /// <summary>
    /// Tells the user where the report is, or that there is none. A report that could not be written
    /// is said on the console: an older file under the same name may still be there, and without a
    /// word it reads as this run's.
    /// </summary>
    private async Task AnnounceReportAsync(
        SessionUid sessionUid, IMessageBus messageBus, string reportPath, Exception? failure, CancellationToken cancellationToken)
    {
        if (failure is null)
        {
            await messageBus.PublishAsync(
                this,
                new SessionFileArtifact(sessionUid, new FileInfo(reportPath), "Raun report", "The HTML report of this run."))
                .ConfigureAwait(false);
            return;
        }

        if (_services.GetService(typeof(IOutputDevice)) is IOutputDevice output)
        {
            await output.DisplayAsync(
                this,
                new WarningMessageOutputDeviceData(
                    $"Raun: the HTML report could not be written to '{reportPath}': {failure.GetType().Name}: {failure.Message}"),
                cancellationToken).ConfigureAwait(false);
        }
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
    private NodeSelector? ReadSelector(ITestExecutionFilter? filter, out string? unsupported)
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
                var uids = uidFilter.TestNodeUids.Select(u => u.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return _granularity == TestGranularity.Step
                    ? new UidNodeSelector(uids)
                    : WholeScenarioSelector.ForScenarioIds(uids);
#pragma warning disable TPEXP // TreeNodeFilter is an experimental Microsoft.Testing.Platform API.
            case TreeNodeFilter treeFilter:
                var tree = new TreeNodeSelector(treeFilter);
                return _granularity == TestGranularity.Step ? tree : WholeScenarioSelector.Widening(tree);
#pragma warning restore TPEXP
            default:
                unsupported = filter.GetType().FullName;
                return null;
        }
    }

    /// <summary>The uid of the node that reports a failure outside every step.</summary>
    internal const string RunFailureUid = "raun:run";

    private Task PublishRunFailureAsync(SessionUid sessionUid, IMessageBus messageBus, Exception failure)
    {
        var node = new TestNode { Uid = RunFailureUid, DisplayName = "Raun run" };
        node.Properties.Add(new ErrorTestNodeStateProperty(failure, failure.Message));
        NodeDiagnostics.Log("run-failure", failure.ToString());
        return messageBus.PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node));
    }

    /// <summary>Warns through the platform logger that a filter type was ignored.</summary>
    private async Task WarnUnsupportedFilterAsync(string? unsupported)
    {
        if (unsupported is null)
        {
            return;
        }

        var message = $"ignoring unsupported execution filter type '{unsupported}'; selecting every test";
        var logger = _services.GetLoggerFactory().CreateLogger(typeof(RaunTestFramework).FullName!);
        await logger.LogWarningAsync(message).ConfigureAwait(false);

        NodeDiagnostics.Log("unsupported-filter", message);
    }

    /// <summary>Lowers every scenario registered in <see cref="ScenarioRegistry"/> to its definition.</summary>
    private static IEnumerable<ScenarioDefinition> EnumerateRegisteredScenarios() => ScenarioRegistry.Definitions();

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
