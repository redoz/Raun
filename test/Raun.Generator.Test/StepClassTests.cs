using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Step classes and the scenario world (docs/superpowers/specs/2026-09-29-step-classes-and-scenario-world-design.md),
/// tested by running what the generator emits: grouped calls under every phase, the Setup node, the
/// world's per-scenario seed, its lifetime through failure and cancellation, parallel siblings on one
/// world, and the receiver diagnostics.
/// </summary>
public class StepClassTests
{
    /// <summary>
    /// A clinic suite shaped like the one that asked for this: a <c>TestIsolation</c> seeded per scenario
    /// is scenario infrastructure held by the world, and customers and appointments are step results
    /// passed explicitly. <c>Journal</c> is test instrumentation only: the order things happened in,
    /// read back by reflection.
    /// </summary>
    internal const string Clinic =
        """
        using System;
        using System.Collections.Concurrent;
        using System.Reflection;
        using System.Threading;
        using System.Threading.Tasks;
        using Raun;

        namespace Clinic;

        public static class Journal
        {
            public static readonly ConcurrentQueue<string> Entries = new();
            public static void Write(string entry) => Entries.Enqueue(entry);
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class IsolationSeedAttribute(int seed) : Attribute
        {
            public int Seed { get; } = seed;
        }

        public sealed class TestIsolation(int seed) : IAsyncDisposable
        {
            public int Seed { get; } = seed;
            public ConcurrentDictionary<string, string> Stubs { get; } = new();

            public ValueTask DisposeAsync()
            {
                Journal.Write($"isolation {Seed} released");
                return ValueTask.CompletedTask;
            }
        }

        public sealed record Customer(string Name)
        {
            public override string ToString() => Name;
        }

        public sealed record Appointment(string Id, Customer Customer, bool Cancelled = false)
        {
            public override string ToString() => Id;
        }

        public sealed class ClinicWorld(TestIsolation isolation) : IScenarioWorld<ClinicWorld>, IAsyncDisposable
        {
            public TestIsolation Isolation { get; } = isolation;

            public static async ValueTask<ClinicWorld> CreateAsync(ScenarioContext context)
            {
                var seed = context.Scenario.Method?.GetCustomAttribute<IsolationSeedAttribute>()?.Seed ?? 1;
                var isolation = new TestIsolation(seed);
                context.OnTeardown(Cleanup.Required, _ => isolation.DisposeAsync().AsTask());
                context.Log($"isolation seed {seed}");
                Journal.Write($"isolation {seed} created");
                await Task.Yield();
                if (seed < 0)
                {
                    throw new InvalidOperationException($"the clinic refused seed {seed}");
                }

                return new ClinicWorld(isolation);
            }

            public ValueTask DisposeAsync()
            {
                Journal.Write($"world {Isolation.Seed} disposed");
                return ValueTask.CompletedTask;
            }
        }

        public sealed class ClinicGiven : Given<ClinicWorld>
        {
            public CustomerSteps Customers => Steps<CustomerSteps>();
            public ExistingAppointments Appointments => Steps<ExistingAppointments>();
            public StubSteps Stubs => Steps<StubSteps>();
        }

        public sealed class CustomerSteps : Given<ClinicWorld>
        {
            [StepName("customer {name} exists")]
            public Task<Customer> Exists(string name)
            {
                Journal.Write($"customer {name} (seed {World.Isolation.Seed})");
                return Task.FromResult(new Customer(name));
            }
        }

        public sealed class ExistingAppointments : Given<ClinicWorld>
        {
            [StepName("{customer} has an appointment")]
            public Task<Appointment> Existing(Customer customer)
            {
                var appointment = new Appointment($"A-{World.Isolation.Seed}", customer);
                Context.OnTeardown(Cleanup.Required, _ =>
                {
                    Journal.Write($"appointment {appointment.Id} deleted");
                    return Task.CompletedTask;
                });
                Journal.Write($"appointment {appointment.Id}");
                return Task.FromResult(appointment);
            }
        }

        public sealed class StubSteps : Given<ClinicWorld>
        {
            [StepName("the {service} stub accepts")]
            public async Task Accepts(string service)
            {
                Context.Log($"stubbing {service}");
                await Task.Delay(50, Context.CancellationToken);
                World.Isolation.Stubs[service] = "accept";
                Context.Log($"stubbed {service}");
            }
        }

        public sealed class ClinicWhen : When<ClinicWorld>
        {
            public AppointmentActions Appointments => Steps<AppointmentActions>();
        }

        public sealed class AppointmentActions : When<ClinicWorld>
        {
            [StepName("{customer} cancels {appointment}")]
            public Task<Appointment> Cancel(Customer customer, Appointment appointment)
            {
                Journal.Write($"cancel {appointment.Id} for {customer.Name}");
                return Task.FromResult(appointment with { Cancelled = true });
            }

            [StepName("the cancellation is refused")]
            public Task<Appointment> Refused(Appointment appointment)
                => throw new InvalidOperationException($"{appointment.Id} cannot be cancelled");

            [StepName("the cancellation hangs")]
            public async Task<Appointment> Hangs(Appointment appointment)
            {
                Journal.Write("hanging");
                await Task.Delay(Timeout.Infinite, Context.CancellationToken);
                return appointment;
            }
        }

        public sealed class ClinicThen : Then<ClinicWorld>
        {
            [StepName("{appointment} is cancelled")]
            public Task IsCancelled(Appointment appointment)
            {
                Journal.Write($"checked {appointment.Id}");
                return appointment.Cancelled ? Task.CompletedTask : throw new InvalidOperationException("not cancelled");
            }

            [StepName("the {service} stub was set up")]
            public Task StubWasSetUp(string service)
                => World.Isolation.Stubs.ContainsKey(service) ? Task.CompletedTask : throw new InvalidOperationException(service);
        }

        [PhaseName("Eventually")]
        public sealed class ClinicEventually : Phase<ClinicWorld>
        {
            [StepName("the calendar is free for {customer}")]
            public Task CalendarIsFree(Customer customer)
            {
                Journal.Write($"calendar free for {customer.Name}");
                return Task.CompletedTask;
            }
        }

        public abstract class ClinicScenarios : Scenarios<ClinicWorld>
        {
            public ClinicGiven Given => Steps<ClinicGiven>();
            public ClinicWhen When => Steps<ClinicWhen>();
            public ClinicThen Then => Steps<ClinicThen>();
            public ClinicEventually Eventually => Steps<ClinicEventually>();
        }

        """;

    internal const string Cancellation =
        """
        public sealed class CancellationScenarios : ClinicScenarios
        {
            [Scenario("customer cancels an appointment")]
            [IsolationSeed(2102)]
            public async Task CustomerCancels()
            {
                var customer = await Given.Customers.Exists("Jane");
                var appointment = await Given.Appointments.Existing(customer);
                await (Given.Stubs.Accepts("notification"), Given.Stubs.Accepts("calendar"));
                var cancelled = await When.Appointments.Cancel(customer, appointment);
                await (Then.IsCancelled(cancelled), Then.StubWasSetUp("notification"), Then.StubWasSetUp("calendar"));
                await Eventually.CalendarIsFree(customer);
            }
        }
        """;

    private static (ScenarioDefinition Definition, Assembly Assembly) Lower(string scenarios, string name)
    {
        var result = GeneratorHarness.Run(Clinic + scenarios);
        result.AssertCompiles();
        return (Assert.Single(result.Definitions(), d => d.DisplayName == name), result.Assembly!);
    }

    private static string[] Journal(Assembly assembly)
    {
        var entries = (ConcurrentQueue<string>)assembly.GetType("Clinic.Journal")!
            .GetField("Entries", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        return [.. entries];
    }

    private static StepResult Step(IReadOnlyList<StepResult> results, string displayName)
        => Assert.Single(results, r => r.DisplayName == displayName);

    [Fact]
    public async Task Grouped_calls_under_every_phase_run_in_order_with_prior_results()
    {
        var (definition, assembly) = Lower(Cancellation, "customer cancels an appointment");

        var results = await definition.RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal(
            [
                "isolation 2102 created",
                "customer Jane (seed 2102)",
                "appointment A-2102",
                "cancel A-2102 for Jane",
                "checked A-2102",
                "calendar free for Jane",
                "appointment A-2102 deleted",
                "world 2102 disposed",
                "isolation 2102 released",
            ],
            Journal(assembly));
    }

    [Fact]
    public async Task Display_names_and_phases_come_from_the_step_classes()
    {
        var (definition, _) = Lower(Cancellation, "customer cancels an appointment");

        var results = await definition.RunAsync();

        Assert.Equal(
            [
                ("Setup", "Setup"),
                ("Given", "customer Jane exists"),
                ("Given", "Jane has an appointment"),
                ("Given", "the notification stub accepts"),
                ("Given", "the calendar stub accepts"),
                ("When", "Jane cancels A-2102"),
                ("Then", "A-2102 is cancelled"),
                ("Then", "the notification stub was set up"),
                ("Then", "the calendar stub was set up"),
                ("Eventually", "the calendar is free for Jane"),
                ("Then", "Teardown"),
            ],
            results.Select(r => (r.Node.Phase, r.DisplayName)));
    }

    [Fact]
    public void Setup_is_the_first_node_every_step_depends_on_and_it_is_unnumbered()
    {
        var (definition, _) = Lower(Cancellation, "customer cancels an appointment");

        var setup = definition.Nodes[0];
        Assert.True(setup.IsSetup);
        Assert.All(
            definition.Nodes.Where(n => !n.IsSetup && !n.IsTeardown),
            n => Assert.Contains(0, n.DependsOn));

        var labels = StepNumbering.Compute(definition);
        Assert.Equal("Setup", StepNumbering.Format(labels, setup, "Setup"));
        Assert.Equal("1. customer Jane exists", StepNumbering.Format(labels, definition.Nodes[1], "customer Jane exists"));
    }

    [Fact]
    public async Task The_world_reads_its_seed_from_the_scenario_method()
    {
        var (definition, _) = Lower(Cancellation, "customer cancels an appointment");

        var results = await definition.RunAsync();

        Assert.Equal("CustomerCancels", definition.Method?.Name);
        Assert.Contains("isolation seed 2102", Step(results, "Setup").Logs);
    }

    [Fact]
    public async Task Parallel_siblings_share_the_world_and_each_see_their_own_context()
    {
        var (definition, _) = Lower(Cancellation, "customer cancels an appointment");

        var results = await definition.RunAsync();

        // Both stubs landed on the one world (the Then steps check it), and each step's log holds its
        // own lines only, though the two ran at the same time on the same step-class instance.
        Assert.Equal(["stubbing notification", "stubbed notification"], Step(results, "the notification stub accepts").Logs);
        Assert.Equal(["stubbing calendar", "stubbed calendar"], Step(results, "the calendar stub accepts").Logs);
        var notification = Step(results, "the notification stub accepts");
        var calendar = Step(results, "the calendar stub accepts");
        Assert.True(notification.StartedAt < calendar.StartedAt + calendar.Duration
            && calendar.StartedAt < notification.StartedAt + notification.Duration,
            "the two stub steps should have overlapped");
    }

    [Fact]
    public async Task Concurrent_scenarios_each_get_their_own_world()
    {
        var result = GeneratorHarness.Run(Clinic +
            """
            public sealed class Seeds : ClinicScenarios
            {
                [Scenario("first")]
                [IsolationSeed(1)]
                public async Task First()
                {
                    await Given.Stubs.Accepts("one");
                    await Then.StubWasSetUp("one");
                }

                [Scenario("second")]
                [IsolationSeed(2)]
                public async Task Second()
                {
                    await Given.Stubs.Accepts("two");
                    await Then.StubWasSetUp("two");
                }
            }
            """);

        result.AssertCompiles();
        var definitions = result.Definitions();

        var runs = await Task.WhenAll(definitions.Select(d => d.RunAsync()));

        Assert.All(runs.SelectMany(r => r), r => Assert.Equal(StepStatus.Passed, r.Status));
        var journal = Journal(result.Assembly!);
        Assert.Contains("world 1 disposed", journal);
        Assert.Contains("world 2 disposed", journal);
    }

    [Fact]
    public async Task A_failing_step_keeps_its_name_and_exception_and_the_world_is_still_disposed()
    {
        var (definition, assembly) = Lower(
            """
            public sealed class Refusals : ClinicScenarios
            {
                [Scenario("the clinic refuses a cancellation")]
                [IsolationSeed(7)]
                public async Task Refused()
                {
                    var customer = await Given.Customers.Exists("Jane");
                    var appointment = await Given.Appointments.Existing(customer);
                    var cancelled = await When.Appointments.Refused(appointment);
                    await Then.IsCancelled(cancelled);
                }
            }
            """,
            "the clinic refuses a cancellation");

        var results = await definition.RunAsync();

        var refused = Step(results, "the cancellation is refused");
        Assert.Equal(StepStatus.Failed, refused.Status);
        Assert.Equal("A-7 cannot be cancelled", Assert.IsType<InvalidOperationException>(refused.Exception).Message);
        var check = Assert.Single(results, r => r.Node.OperationName == "IsCancelled");
        Assert.Equal(StepStatus.Skipped, check.Status);
        Assert.Equal("dependency failed: Refused", check.SkipReason);
        Assert.Equal(StepStatus.Passed, Step(results, "Teardown").Status);
        Assert.Equal(
            ["appointment A-7 deleted", "world 7 disposed", "isolation 7 released"],
            Journal(assembly).SkipWhile(e => !e.StartsWith("appointment A-7 deleted", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_setup_that_fails_part_way_skips_every_step_and_still_releases_what_it_made()
    {
        var (definition, assembly) = Lower(
            """
            public sealed class BrokenScenarios : ClinicScenarios
            {
                [Scenario("the isolation cannot be set up")]
                [IsolationSeed(-1)]
                public async Task Broken()
                {
                    var customer = await Given.Customers.Exists("Jane");
                    await Eventually.CalendarIsFree(customer);
                }
            }
            """,
            "the isolation cannot be set up");

        var results = await definition.RunAsync();

        var setup = Step(results, "Setup");
        Assert.Equal(StepStatus.Failed, setup.Status);
        Assert.Equal("the clinic refused seed -1", Assert.IsType<InvalidOperationException>(setup.Exception).Message);
        Assert.All(
            results.Where(r => !r.Node.IsSetup && !r.Node.IsTeardown),
            r =>
            {
                Assert.Equal(StepStatus.Skipped, r.Status);
                Assert.StartsWith("dependency failed: Setup", r.SkipReason, StringComparison.Ordinal);
            });

        // No world was made, so there is none to dispose; the isolation made before the failure is
        // released by the cleanup CreateAsync registered the moment it existed.
        Assert.Equal(["isolation -1 created", "isolation -1 released"], Journal(assembly));
    }

    [Fact]
    public async Task A_cancelled_scenario_still_disposes_its_world()
    {
        var (definition, assembly) = Lower(
            """
            public sealed class Hanging : ClinicScenarios
            {
                [Scenario("the cancellation hangs")]
                [IsolationSeed(9)]
                public async Task Hangs()
                {
                    var customer = await Given.Customers.Exists("Jane");
                    var appointment = await Given.Appointments.Existing(customer);
                    var cancelled = await When.Appointments.Hangs(appointment);
                    await Then.IsCancelled(cancelled);
                }
            }
            """,
            "the cancellation hangs");

        using var cts = new CancellationTokenSource();
        var run = new ScenarioScheduler().RunAsync(definition, cancellationToken: cts.Token);
        while (!Journal(assembly).Contains("hanging"))
        {
            await Task.Delay(10);
        }

        await cts.CancelAsync();
        var results = await run;

        Assert.Equal(StepStatus.Skipped, Step(results, "the cancellation hangs").Status);
        Assert.Equal(StepStatus.Passed, Step(results, "Teardown").Status);
        Assert.Equal(
            ["appointment A-9 deleted", "world 9 disposed", "isolation 9 released"],
            Journal(assembly).SkipWhile(e => e != "appointment A-9 deleted"));
    }

    [Fact]
    public async Task A_filtered_run_of_one_step_keeps_setup()
    {
        var (definition, _) = Lower(Cancellation, "customer cancels an appointment");
        var target = definition.Nodes.Single(n => n.OperationName == "Existing").Index;

        var results = await new ScenarioScheduler().RunAsync(definition, targets: new HashSet<int> { target });

        Assert.Equal(StepStatus.Passed, results[0].Status);
        Assert.Equal(StepStatus.Passed, results[target].Status);
        Assert.Equal(
            ScenarioScheduler.NotSelectedSkipReason,
            Assert.Single(results, r => r.Node.OperationName == "Cancel").SkipReason);
    }

    [Fact]
    public void Grouped_calls_have_their_own_step_ids()
    {
        var (definition, _) = Lower(
            """
            public sealed class TwinScenarios : ClinicScenarios
            {
                [Scenario("twins")]
                public async Task Twins()
                {
                    await Given.Customers.Exists("Jane");
                    await Given.Stubs.Accepts("Jane");
                }
            }
            """,
            "twins");

        var ids = definition.Nodes.Select(n => n.StepId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void A_step_class_exposes_nothing_but_its_steps_and_groups()
    {
        // What IntelliSense lists after `Given.` is the class's own public members plus object's:
        // Raun's World, Context and Steps<T>() are protected, so a call site never sees them.
        var inherited = typeof(Phase<NoWorld>)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType != typeof(object))
            .Select(m => m.Name);

        Assert.Empty(inherited);
    }

    private static async Task<ImmutableArray<Diagnostic>> Diagnose(string scenarios)
        => await GeneratorHarness.DiagnoseAsync(Clinic + scenarios);

    private static Diagnostic Single(ImmutableArray<Diagnostic> diagnostics, string id)
        => Assert.Single(diagnostics, d => d.Id == id);

    [Theory]
    [InlineData("var given = Given;\n        await given.Customers.Exists(\"Jane\");", "given", "a local")]
    [InlineData("await ((ClinicGiven)Given).Customers.Exists(\"Jane\");", "(ClinicGiven)Given", "not a phase or group property")]
    public async Task RAUN019_names_the_link_it_cannot_follow(string statements, string link, string reason)
    {
        var diagnostics = await Diagnose(
            $$"""
            public sealed class S : ClinicScenarios
            {
                [Scenario]
                public async Task Bad()
                {
                    {{statements}}
                }
            }
            """);

        var diagnostic = Single(diagnostics, "RAUN019");
        Assert.Contains(reason, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal(link, diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN004");
    }

    [Fact]
    public async Task RAUN019_for_a_group_property_that_does_not_return_Steps()
    {
        var diagnostics = await Diagnose(
            """
            public sealed class LooseGiven : Given<ClinicWorld>
            {
                public CustomerSteps Customers => new CustomerSteps();
            }

            public sealed class S : Scenarios<ClinicWorld>
            {
                public LooseGiven Given => Steps<LooseGiven>();

                [Scenario]
                public async Task Bad()
                {
                    await Given.Customers.Exists("Jane");
                }
            }
            """);

        var diagnostic = Single(diagnostics, "RAUN019");
        Assert.Contains("'Customers' must be declared '=> Steps<CustomerSteps>()'", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Equal("Given.Customers", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Fact]
    public async Task RAUN020_for_a_scenario_outside_a_scenarios_class()
    {
        var diagnostics = await Diagnose(
            """
            public sealed class S
            {
                [Scenario]
                public async Task Bad()
                {
                    await Steps.Customers.Exists("Jane");
                }

                private static ClinicGiven Steps => null!;
            }
            """);

        // One diagnostic for the scenario, not one per call that cannot be followed without a world.
        Assert.Contains("does not derive from Scenarios<TWorld>", Single(diagnostics, "RAUN020").GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN019");
    }

    [Fact]
    public async Task RAUN020_for_a_static_scenario_in_a_scenarios_class()
    {
        var diagnostics = await Diagnose(
            """
            public sealed class S : ClinicScenarios
            {
                [Scenario]
                public static async Task Bad()
                {
                    await Task.Yield();
                }
            }
            """);

        Assert.Contains("make it an instance method", Single(diagnostics, "RAUN020").GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RAUN021_for_a_step_class_of_another_world()
    {
        var diagnostics = await Diagnose(
            """
            public sealed class OtherWorld : IScenarioWorld<OtherWorld>
            {
                public static ValueTask<OtherWorld> CreateAsync(ScenarioContext context) => ValueTask.FromResult(new OtherWorld());
            }

            public sealed class S : Scenarios<OtherWorld>
            {
                public ClinicGiven Given => Steps<ClinicGiven>();

                [Scenario]
                public async Task Bad()
                {
                    await Given.Customers.Exists("Jane");
                }
            }
            """);

        Assert.Contains(diagnostics, d => d.Id == "RAUN021");
    }

    [Theory]
    [InlineData("private int _calls;", "_calls", "every step of a scenario")]
    [InlineData("public string? LastName { get; set; }", "LastName", "every step of a scenario")]
    [InlineData("public string? Note { get; private set; }", "Note", "every step of a scenario")]
    [InlineData("private static int s_total;", "s_total", "every scenario")]
    [InlineData("public static string? Shared { get; set; }", "Shared", "every scenario")]
    public async Task RAUN022_flags_mutable_state_on_a_step_class(string member, string name, string sharedBy)
    {
        var diagnostics = await Diagnose(
            $$"""
            public sealed class StatefulSteps : Given<ClinicWorld>
            {
                {{member}}

                [StepName("a stateful step")]
                public Task Step() => Task.CompletedTask;
            }
            """);

        var diagnostic = Single(diagnostics, "RAUN022");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(name, diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        Assert.Contains("'StatefulSteps'", message, StringComparison.Ordinal);
        Assert.Contains(sharedBy, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RAUN022_leaves_immutable_members_and_other_classes_alone()
    {
        var diagnostics = await GeneratorHarness.DiagnoseAsync(
            Clinic +
            """
            public sealed class SteadySteps : Given<ClinicWorld>
            {
                private const int Limit = 3;
                private static readonly string Prefix = "p";
                private readonly object _gate = new();
                public string Name { get; } = "steady";
                public string? Label { get; init; }
                public int Doubled => Limit * 2;
                public CustomerSteps Customers => Steps<CustomerSteps>();

                [StepName("a steady step")]
                public Task Step() => Task.CompletedTask;
            }

            public sealed class NotSteps
            {
                private int _calls;
                public string? Name { get; set; }
                public int Calls => _calls++;
            }
            """,
            requireCompilable: true);

        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN022");
    }

    [Fact]
    public async Task The_cancellation_scenario_has_no_diagnostics()
        => Assert.Empty(await GeneratorHarness.DiagnoseAsync(Clinic + Cancellation, requireCompilable: true));
}
