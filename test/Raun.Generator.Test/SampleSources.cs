namespace Raun.Generator.Test;

/// <summary>Reusable input source for generator tests: a small AppointmentDsl with real impls.</summary>
public static class SampleSources
{
    public const string Dsl =
        """
        using System.Linq;
        using System.Threading.Tasks;
        using Raun;

        namespace Demo;

        public sealed record Patient(string Name);
        public sealed record Slot(int Id);
        public sealed record Appointment(Patient Patient, Slot Slot);
        public sealed record User(string Name);
        public sealed record Import(int Count);

        public static class AppointmentDsl
        {
            extension(Given)
            {
                [StepName("patient {name} exists")]
                public static async Task<Patient> PatientExists(string name)
                {
                    await Task.Yield();
                    return new Patient(name);
                }

                [StepName("an available slot exists")]
                public static async Task<Slot> AvailableSlot()
                {
                    await Task.Yield();
                    return new Slot(1);
                }

                [StepName("database is clean")]
                public static Task DatabaseIsClean() => Task.CompletedTask;

                [StepName("user {name} exists")]
                public static async Task<User> UserExists(string name)
                {
                    await Task.Yield();
                    return new User(name);
                }
            }

            extension(When)
            {
                [StepName("creating an appointment")]
                public static async Task<Appointment> CreateAppointment(Patient patient, Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(patient, slot);
                }

                [StepName("importing users")]
                public static async Task<Import> ImportUsers(User[] users)
                {
                    await Task.Yield();
                    return new Import(users.Length);
                }
            }

            extension(Then)
            {
                [StepName("the appointment should exist")]
                public static Task AppointmentExists(Appointment appointment) => Task.CompletedTask;

                [StepName("the import should contain the users")]
                public static Task ImportShouldContainUsers(Import import, User[] users) => Task.CompletedTask;

                [StepName("greet {patient}")]
                public static Task Greet(Patient patient) => Task.CompletedTask;
            }
        }
        """;

    // Named arguments: the label (`patient:`) must be left alone; only the value is rewritten.
    public const string NamedArgScenario =
        """

        public static class NamedArgScenarios
        {
            [Scenario("named args")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists(name: "Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient: patient, slot: slot);
                await Then.AppointmentExists(appointment: appointment);
            }
        }
        """;

    public const string RuntimeNameScenario =
        """

        public static class GreetScenarios
        {
            [Scenario("greeting")]
            public static async Task Greeting()
            {
                var patient = await Given.PatientExists("Jane");
                await Then.Greet(patient);
            }
        }
        """;

    // Scenario snippets are appended to Dsl, continuing its file-scoped `namespace Demo;`.
    public const string LinearScenario =
        """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    public const string TupleScenario =
        """

        public static class TupleScenarios
        {
            [Scenario("tuple booking")]
            public static async Task Booking()
            {
                await Given.DatabaseIsClean();

                var (patient, slot) = await (
                    Given.PatientExists("Jane"),
                    Given.AvailableSlot());

                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    public const string ArrayScenario =
        """

        public static class ArrayScenarios
        {
            [Scenario("array import")]
            public static async Task Import()
            {
                var users = await new[]
                {
                    Given.UserExists("alice"),
                    Given.UserExists("bob"),
                };

                var import = await When.ImportUsers(users);
                await Then.ImportShouldContainUsers(import, users);
            }
        }
        """;

    public const string LinqScenario =
        """

        public static class LinqScenarios
        {
            [Scenario("linq import")]
            public static async Task Import()
            {
                var users = await Enumerable.Range(1, 3)
                    .Select(i => Given.UserExists($"user-{i}"))
                    .ToArray();

                var import = await When.ImportUsers(users);
                await Then.ImportShouldContainUsers(import, users);
            }
        }
        """;

    // A resource-aware DSL: User/Slot/Appointment are CRTP IResource<> records with KeyFor, and the
    // steps carry role attributes ([Created]/[Edited]/[Read] on the return value or parameters) the
    // generator lowers into ctx.Resources.* calls.
    public const string ResourceDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace ResourceDemo;

        public sealed record User(string Email) : IResource<User>
        {
            public static ResourceKey KeyFor(User instance) => instance.Email;
        }

        public sealed record Slot(int Id) : IResource<Slot>
        {
            public static ResourceKey KeyFor(Slot instance) => instance.Id.ToString();
        }

        public sealed record Appointment(User User, Slot Slot) : IResource<Appointment>
        {
            public static ResourceKey KeyFor(Appointment instance) => instance.User.Email + "@" + instance.Slot.Id;
        }

        public static class ResourceDsl
        {
            extension(Given)
            {
                [StepName("user {email} exists")]
                [return: Created]
                public static async Task<User> UserExists(string email)
                {
                    await Task.Yield();
                    return new User(email);
                }

                [StepName("a slot exists")]
                [return: Created]
                public static async Task<Slot> SlotExists()
                {
                    await Task.Yield();
                    return new Slot(1);
                }
            }

            extension(When)
            {
                [StepName("suspending the user")]
                [return: Edited]
                public static async Task<User> Suspend([Edited] User user)
                {
                    await Task.Yield();
                    return user;
                }

                [StepName("booking a slot")]
                [return: Created]
                public static async Task<Appointment> Book([Read] User user, [Edited] Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(user, slot);
                }

                [StepName("booking with lineage")]
                [return: Created(References = [nameof(user)], Consumes = [nameof(slot)])]
                public static async Task<Appointment> BookWithLineage(User user, Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(user, slot);
                }
            }

            extension(Then)
            {
                [StepName("the user cannot sign in")]
                public static Task CannotSignIn([Read] User user) => Task.CompletedTask;
            }
        }
        """;

    // Scenario appended to ResourceDsl, continuing its file-scoped `namespace ResourceDemo;`.
    public const string ResourceScenario =
        """

        public static class ResourceScenarios
        {
            [Scenario("suspended user cannot sign in")]
            public static async Task SuspendedUserCannotSignIn()
            {
                var user = await Given.UserExists("jane@acme.com");
                var suspended = await When.Suspend(user);
                await Then.CannotSignIn(suspended);
            }
        }
        """;

    // Scenario appended to ResourceDsl: exercises When.Book's multi-role parameter list
    // ([Read] User, [Edited] Slot) plus [return: Created], locking in param-loop ordering
    // and param-before-return emit order.
    public const string BookingScenario =
        """

        public static class BookingResourceScenarios
        {
            [Scenario("booking a slot")]
            public static async Task BookSlot()
            {
                var user = await Given.UserExists("jane@acme.com");
                var slot = await Given.SlotExists();
                var appt = await When.Book(user, slot);
            }
        }
        """;

    // Scenario appended to ResourceDsl: exercises producer-side lineage — [return: Created(References =
    // [nameof(user)], Consumes = [nameof(slot)])] — proving the named targets lower to shared
    // Reference/Consume effects plus lineage relations from the created Appointment.
    public const string LineageScenario =
        """

        public static class LineageResourceScenarios
        {
            [Scenario("booking with lineage")]
            public static async Task BookWithLineage()
            {
                var user = await Given.UserExists("jane@acme.com");
                var slot = await Given.SlotExists();
                var appt = await When.BookWithLineage(user, slot);
            }
        }
        """;

    // A DSL with condition steps: an awaited phase-marker call whose result is usable as a C#
    // condition. `IsPriority` returns bool; `HasCapacity` returns a type with `operator true`, proving
    // the generator emits the coercion rather than the scheduler unboxing to bool.
    public const string ConditionalDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace CondDemo;

        public sealed record Patient(string Name);
        public sealed record Appointment(string Kind);

        public readonly struct Capacity
        {
            public Capacity(bool value) => Value = value;
            public bool Value { get; }
            public static bool operator true(Capacity c) => c.Value;
            public static bool operator false(Capacity c) => !c.Value;
        }

        public static class CondDsl
        {
            extension(Given)
            {
                [StepName("patient {name} exists")]
                public static async Task<Patient> PatientExists(string name)
                {
                    await Task.Yield();
                    return new Patient(name);
                }

                [StepName("the patient is priority")]
                public static async Task<bool> IsPriority()
                {
                    await Task.Yield();
                    return true;
                }

                [StepName("the clinic has capacity")]
                public static async Task<Capacity> HasCapacity()
                {
                    await Task.Yield();
                    return new Capacity(true);
                }
            }

            extension(When)
            {
                [StepName("creating an urgent appointment")]
                public static async Task<Appointment> CreateUrgent(Patient patient)
                {
                    await Task.Yield();
                    return new Appointment("urgent");
                }

                [StepName("creating a standard appointment")]
                public static async Task<Appointment> CreateStandard(Patient patient)
                {
                    await Task.Yield();
                    return new Appointment("standard");
                }

                [StepName("notifying the patient")]
                public static Task Notify(Patient patient) => Task.CompletedTask;
            }

            extension(Then)
            {
                [StepName("the appointment should exist")]
                public static Task AppointmentExists(Appointment appointment) => Task.CompletedTask;
            }
        }
        """;

    // if/else, both arms defining `appointment` => a phi at the closing brace.
    public const string IfElseScenario =
        """

        public static class IfElseScenarios
        {
            [Scenario("priority routing")]
            public static async Task Routing()
            {
                var patient = await Given.PatientExists("Jane");

                Appointment appointment;
                if (await Given.IsPriority())
                    appointment = await When.CreateUrgent(patient);
                else
                    appointment = await When.CreateStandard(patient);

                await Then.AppointmentExists(appointment);
            }
        }
        """;

    // A bare `if` with no else and no assignment: the arm's step is simply guarded.
    public const string BareIfScenario =
        """

        public static class BareIfScenarios
        {
            [Scenario("notify priority patients")]
            public static async Task Notify()
            {
                var patient = await Given.PatientExists("Jane");

                if (await Given.IsPriority())
                    await When.Notify(patient);
            }
        }
        """;

    // A bare `if` that conditionally OVERWRITES a local defined before the branch: the merge takes the
    // arm's definition and a synthetic pass-through of the parent definition.
    public const string ConditionalOverwriteScenario =
        """

        public static class OverwriteScenarios
        {
            [Scenario("upgrade to urgent when priority")]
            public static async Task Upgrade()
            {
                var patient = await Given.PatientExists("Jane");
                var appointment = await When.CreateStandard(patient);

                if (await Given.IsPriority())
                    appointment = await When.CreateUrgent(patient);

                await Then.AppointmentExists(appointment);
            }
        }
        """;

    // Nested ifs: the inner arm carries BOTH guards.
    public const string NestedIfScenario =
        """

        public static class NestedIfScenarios
        {
            [Scenario("nested routing")]
            public static async Task Routing()
            {
                var patient = await Given.PatientExists("Jane");

                if (await Given.IsPriority())
                {
                    if (await Given.HasCapacity())
                        await When.Notify(patient);
                }
            }
        }
        """;

    // A condition whose result type is not bool but defines `operator true`.
    public const string OperatorTrueScenario =
        """

        public static class OperatorTrueScenarios
        {
            [Scenario("capacity routing")]
            public static async Task Routing()
            {
                var patient = await Given.PatientExists("Jane");

                if (await Given.HasCapacity())
                    await When.Notify(patient);
            }
        }
        """;

    // An else-if chain: N-way routing today, without any switch support. Each `else if` is just a
    // nested `if` in the else arm, so the guards stack and the merges chain.
    public const string ElseIfChainScenario =
        """

        public static class ElseIfScenarios
        {
            [Scenario("three-way routing")]
            public static async Task Routing()
            {
                var patient = await Given.PatientExists("Jane");

                Appointment appointment;
                if (await Given.IsPriority())
                    appointment = await When.CreateUrgent(patient);
                else if (await Given.HasCapacity())
                    appointment = await When.CreateStandard(patient);
                else
                    appointment = await When.CreateStandard(patient);

                await Then.AppointmentExists(appointment);
            }
        }
        """;

    // A scenario with an explicit teardown policy.
    public const string TeardownOnSuccessScenario =
        """

        public static class TeardownPolicyScenarios
        {
            [Scenario("policy")]
            [Teardown(Run.OnSuccess)]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    // A DSL whose step registers a cleanup, proving the closure runs end to end.
    public const string TeardownDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace TeardownDemo;

        public sealed record Patient(string Name);

        public static class Probe
        {
            public static int Cleaned;
        }

        public static class TeardownDemoDsl
        {
            extension(Given)
            {
                [StepName("patient {name} exists")]
                public static Task<Patient> PatientExists(string name, ScenarioContext? ctx = null)
                {
                    ctx?.OnTeardown(() => { Probe.Cleaned++; return Task.CompletedTask; });
                    return Task.FromResult(new Patient(name));
                }
            }

            extension(Then)
            {
                [StepName("the patient should exist")]
                public static Task PatientIsThere(Patient patient) => Task.CompletedTask;
            }
        }
        """;

    public const string TeardownScenario =
        """

        public static class TeardownScenarios
        {
            [Scenario("cleanup runs")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                await Then.PatientIsThere(patient);
            }
        }
        """;

    // Contended resources: an assembly-level use, a class-level use, a scenario-level use, and uses
    // on DSL step methods. The expected reduction is Audit:Shared (assembly), Database:Exclusive
    // (the step's Exclusive beats the class's Shared), Smtp:Shared (method and step agree).
    public const string UsesDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        [assembly: Uses<UsesDemo.Audit>]

        namespace UsesDemo;

        [SharedResource]
        public sealed class Database : IContendedResource;

        [ExclusiveResource]
        public sealed class Smtp : IContendedResource;

        [SharedResource]
        public sealed class Audit : IContendedResource;

        public static class UsesDsl
        {
            extension(Given)
            {
                [StepName("the schedule is empty")]
                [Uses<Database>(LockMode.Exclusive)]
                public static Task ScheduleIsEmpty() => Task.CompletedTask;

                [StepName("a patient exists")]
                public static Task PatientExists() => Task.CompletedTask;
            }

            extension(When)
            {
                [StepName("a reminder is sent")]
                [Uses<Smtp>]
                public static Task ReminderIsSent() => Task.CompletedTask;
            }
        }
        """;

    // Scenario appended to UsesDsl, continuing its file-scoped `namespace UsesDemo;`.
    public const string UsesScenario =
        """

        [Uses<Database>]
        public static class UsesScenarios
        {
            [Scenario("clears and reminds")]
            [Uses<Smtp>]
            public static async Task ClearAndRemind()
            {
                await Given.ScheduleIsEmpty();
                await When.ReminderIsSent();
            }
        }
        """;

    // A scenario in the same DSL that touches no [Uses] site of its own: only the assembly and the
    // step methods it calls count.
    public const string UsesFreeScenario =
        """

        public static class PlainScenarios
        {
            [Scenario("just a patient")]
            public static async Task JustAPatient()
            {
                await Given.PatientExists();
            }
        }
        """;

    // Proves the scenario-method and containing-class sites on their own: Cache comes only from the
    // class, Queue only from the scenario method, and the one step called declares nothing. Cache and
    // Queue are declared here rather than in UsesDsl: UsesDsl is a shared prefix for other snapshotted
    // scenarios (e.g. Uses_scenario), and inserting lines there would shift the SourceLine values baked
    // into those already-accepted snapshots without changing any behavior.
    public const string UsesSitesScenario =
        """

        [SharedResource]
        public sealed class Cache : IContendedResource;

        [SharedResource]
        public sealed class Queue : IContendedResource;

        [Uses<Cache>]
        public static class SiteScenarios
        {
            [Scenario("class and method sites")]
            [Uses<Queue>(LockMode.Exclusive)]
            public static async Task ClassAndMethodSites()
            {
                await Given.PatientExists();
            }
        }
        """;

    // A DSL step invoked with an EXPLICIT type argument, which nothing infers: dropping the `<int>`
    // (as the old text-built call did — it re-spelled the member from its identifier alone) leaves
    // `Given.DefaultOf()`, which does not compile. Step and scenario live in one self-contained
    // snippet appended to Dsl, following UsesSitesScenario: adding the method to Dsl itself would
    // shift the source lines baked into every accepted snapshot.
    public const string GenericStepScenario =
        """

        public static class GenericDsl
        {
            extension(Given)
            {
                [StepName("a default value exists")]
                public static async Task<T> DefaultOf<T>()
                {
                    await Task.Yield();
                    return default!;
                }
            }

            extension(Then)
            {
                [StepName("the value should be {value}")]
                public static Task ValueShouldBe(int value) => Task.CompletedTask;
            }
        }

        public static class GenericScenarios
        {
            [Scenario("explicit type argument")]
            public static async Task ExplicitTypeArgument()
            {
                var zero = await Given.DefaultOf<int>();
                await Then.ValueShouldBe(zero);
            }
        }
        """;

    // Parallel groups whose steps all return plain Task: nothing to bind, so the awaited tuple or
    // array is a bare expression statement. Appended rather than added to Dsl for the snapshot
    // line-number reason above.
    public const string VoidTupleScenario =
        """

        public static class VoidTupleScenarios
        {
            [Scenario("parallel greetings")]
            public static async Task Greetings()
            {
                var jane = await Given.PatientExists("Jane");
                var bob = await Given.PatientExists("Bob");

                await (Then.Greet(jane), Then.Greet(bob));

                await Given.DatabaseIsClean();
            }
        }
        """;

    public const string VoidArrayScenario =
        """

        public static class VoidArrayScenarios
        {
            [Scenario("parallel greetings array")]
            public static async Task Greetings()
            {
                var jane = await Given.PatientExists("Jane");
                var bob = await Given.PatientExists("Bob");

                await new[] { Then.Greet(jane), Then.Greet(bob) };

                await Given.DatabaseIsClean();
            }
        }
        """;

    public const string VoidLinqScenario =
        """

        public static class VoidLinqScenarios
        {
            [Scenario("parallel greetings linq")]
            public static async Task Greetings()
            {
                var jane = await Given.PatientExists("Jane");

                await Enumerable.Range(1, 3).Select(i => Then.Greet(jane)).ToArray();

                await Given.DatabaseIsClean();
            }
        }
        """;
}
