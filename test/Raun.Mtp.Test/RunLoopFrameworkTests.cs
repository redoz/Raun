using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Running;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The run loop reached through <see cref="RaunTestFramework"/>: a run request executes the
/// registered scenarios, an unknown filter over-selects rather than failing, and the simulate-time
/// flag and the consumer's service provider travel from the framework to the step context. The
/// loop's own behaviour is covered in <c>Raun.Test</c>.
/// </summary>
public class RunLoopFrameworkTests
{
    private static ScenarioNode Node(
        int index,
        string stepId,
        string template,
        int[]? dependsOn = null,
        Func<IStepInputs, ScenarioContext, Task<object?>>? invoke = null,
        Guard[]? guards = null,
        int[]? mergeSources = null,
        bool synthetic = false,
        Func<object?, bool>? evaluate = null) => new()
        {
            Index = index,
            StepId = stepId,
            Phase = "Given",
            OperationName = $"Op{index}",
            DisplayNameTemplate = template,
            SourceFile = @"C:\src\S.cs",
            SourceLine = index + 1,
            DependsOn = dependsOn ?? [],
            Guards = guards ?? [],
            MergeSources = mergeSources ?? [],
            IsSynthetic = synthetic,
            EvaluateCondition = evaluate,
            Invoke = invoke ?? ((_, _) => Task.FromResult<object?>(null)),
        };

    private static ScenarioDefinition Definition(string id, string display, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
    };

    private static string Uid(string scenarioId, string stepId) => scenarioId + ":" + stepId;

    /// <summary>A step body that advances its per-step clock by <paramref name="delta"/> via
    /// <see cref="ScenarioContext.SimulateElapsed"/> with no real waiting (an inert no-op in real mode).</summary>
    private static Func<IStepInputs, ScenarioContext, Task<object?>> Elapse(TimeSpan delta)
        => (_, ctx) => { ctx.SimulateElapsed(delta); return Task.FromResult<object?>(null); };

    [Fact]
    public async Task Through_the_framework_run_request_executes_the_registered_scenario()
    {
        // End-to-end through RaunTestFramework.OnExecute (registry-backed), proving the framework
        // wires the run loop into the run request path.
        var method = $"Raun.Mtp.Test.RunLoop.{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => Definition("fw-scn", "fw scenario",
            Node(0, "a", "a"),
            Node(1, "b", "b", dependsOn: [0])));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("fw-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnExecute(uid, filter: null, bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);
        var passed = bus.Nodes
            .Where(n => n.Properties.OfType<PassedTestNodeStateProperty>().Length != 0)
            .Select(n => n.Uid.Value).ToList();
        Assert.Contains("fw-scn:a", passed);
        Assert.Contains("fw-scn:b", passed);
    }

    /// <summary>A filter type Raun has never seen — what a future platform version could hand over.</summary>
    private sealed class UnknownFilter : ITestExecutionFilter
    {
    }

    [Fact]
    public async Task An_unrecognized_filter_runs_everything_instead_of_failing_the_run()
    {
        // A filter Raun cannot honour is a reason to over-select, never to abort: aborting turns a
        // future platform filter into a hard run failure for something the user did not do wrong.
        var method = $"Raun.Mtp.Test.UnknownFilter.{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => Definition("uf-scn", "unknown filter scenario",
            Node(0, "a", "a"),
            Node(1, "b", "b", dependsOn: [0])));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("uf-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnExecute(uid, new UnknownFilter(), bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);
        Assert.Contains(bus.Nodes, n => n.Uid.Value == Uid("uf-scn", "a"));
        Assert.Contains(bus.Nodes, n => n.Uid.Value == Uid("uf-scn", "b"));
    }

    [Fact]
    public async Task Framework_built_with_simulateTime_threads_the_flag_to_the_scheduler()
    {
        // The (IServiceProvider, bool) overload carries the opt-in flag from RunAsync down through the run
        // loop to the scheduler: a body that SimulateElapsed(800ms) reports exactly that on the published
        // node's TimingProperty — only reachable if simulated mode arrived at the scheduler.
        var method = $"Raun.Mtp.Test.SimRun.{Guid.NewGuid():N}";
        var work = TimeSpan.FromMilliseconds(800);
        ScenarioRegistry.Register(method, () => Definition("sim-fw", "sim scenario",
            Node(0, "a", "a", invoke: Elapse(work))));

        var framework = new RaunTestFramework(services: null!, simulateTime: true);
        var uid = new SessionUid("sim-fw-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        await framework.OnExecute(uid, filter: null, bus, () => { }, CancellationToken.None);

        var node = bus.Nodes.Single(n => n.Uid.Value == "sim-fw:a"
            && n.Properties.OfType<PassedTestNodeStateProperty>().Length != 0);
        var timing = node.Properties.OfType<TimingProperty>().Single();
        Assert.Equal(work, timing.GlobalTiming.Duration);
    }

    [Fact]
    public async Task Framework_built_with_a_provider_threads_it_to_the_step_context()
    {
        // End-to-end: the CONSUMER's provider -> run loop -> scheduler -> ctx.Services.
        //
        // MTP's own provider is deliberately NOT what steps see. It carries platform internals
        // (command-line options, the logger factory), and letting a step resolve those would couple
        // user code to the platform. The framework keeps it for its own use and threads the
        // consumer's provider — the one built in their Program.cs — to step bodies instead.
        var method = $"Raun.Mtp.Test.DiRun.{Guid.NewGuid():N}";
        IServiceProvider? seen = null;
        ScenarioRegistry.Register(method, () => Definition("di-fw", "di scenario",
            Node(0, "a", "a", invoke: (_, ctx) => { seen = ctx.Services; return Task.FromResult<object?>(null); })));

        var mtpProvider = new StubServiceProvider();
        var userProvider = new StubServiceProvider();
        var framework = new RaunTestFramework(mtpProvider, simulateTime: false, userProvider);
        var uid = new SessionUid("di-fw-run");
        await framework.CreateTestSession(uid);

        await framework.OnExecute(uid, filter: null, new RecordingMessageBus(), () => { }, CancellationToken.None);

        Assert.Same(userProvider, seen);
        Assert.NotSame(mtpProvider, seen);
    }

    [Fact]
    public async Task Without_a_consumer_provider_the_step_context_has_no_services()
    {
        // MTP's provider must not leak in as a fallback.
        var method = $"Raun.Mtp.Test.DiRun.{Guid.NewGuid():N}";
        IServiceProvider? seen = null;
        var ran = false;
        ScenarioRegistry.Register(method, () => Definition("di-none", "no di scenario",
            Node(0, "a", "a", invoke: (_, ctx) => { seen = ctx.Services; ran = true; return Task.FromResult<object?>(null); })));

        var framework = new RaunTestFramework(new StubServiceProvider());
        var uid = new SessionUid("di-none-run");
        await framework.CreateTestSession(uid);

        await framework.OnExecute(uid, filter: null, new RecordingMessageBus(), () => { }, CancellationToken.None);

        Assert.True(ran);
        Assert.Null(seen);
    }

    /// <summary>
    /// A provider standing in for MTP's own. Identity is what these tests assert on; it only has to
    /// answer <see cref="ICommandLineOptions"/> because the framework consults it (for --report-html)
    /// before the run loop starts, and MTP's accessor extension throws on a missing service.
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandLineOptions) ? new NoOptions() : null;

        private sealed class NoOptions : ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => false;

            public bool TryGetOptionArgumentList(
                string optionName,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments)
            {
                arguments = null;
                return false;
            }
        }
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        private readonly List<TestNodeUpdateMessage> updates = [];

        public IReadOnlyList<TestNode> Nodes
        {
            get { lock (updates) { return updates.Select(u => u.TestNode).ToList(); } }
        }

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            if (data is TestNodeUpdateMessage update)
            {
                lock (updates) { updates.Add(update); }
            }

            return Task.CompletedTask;
        }
    }
}
