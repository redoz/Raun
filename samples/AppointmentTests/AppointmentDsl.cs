using System.Globalization;
using Raun;
using Xunit;

namespace AppointmentTests;

// Domain entities flow between steps as ordinary types — each `await` unwraps Task<T> into T. The
// shared identities (Patient/Slot/Appointment/User) are CRTP resources so steps can declare what they
// do to them; ImportResult is a pure count DTO, not a shared identity, so it stays a plain record.
// IDs are stable; names and cities are presentation/state. RegistryPatient is an external row
// shape: its mapping belongs here, and the row itself needs no Raun contract.
public sealed record Patient(string Id, string Name, string? City = null) :
    IResource<Patient>, IResourceAlternate<RegistryPatient>, IResourceAlternate<PatientRow>
{
    public static ResourceKey KeyFor(Patient instance) => instance.Id;
    public static string? LabelFor(Patient instance) => instance.Name;
    static ResourceKey IResourceAlternate<RegistryPatient>.KeyFor(RegistryPatient row) => row.PatientId;
    static string? IResourceAlternate<RegistryPatient>.LabelFor(RegistryPatient row) => row.FullName;
    static ResourceKey IResourceAlternate<PatientRow>.KeyFor(PatientRow row) => row.Id;
    static string? IResourceAlternate<PatientRow>.LabelFor(PatientRow row) => row.Name;
}

// Same idea for a slot: Id is the identity, Held is state a hold step flips.
public sealed record Slot(int Id, bool Held = false) : IResource<Slot>
{
    public static ResourceKey KeyFor(Slot instance) => instance.Id.ToString(CultureInfo.InvariantCulture);
    public static string? LabelFor(Slot instance) => $"slot {instance.Id:D2}";
}

public sealed record Appointment(Patient Patient, Slot Slot) : IResource<Appointment>
{
    public static ResourceKey KeyFor(Appointment instance) => $"{instance.Patient.Id}@{instance.Slot.Id}";
    public static string? LabelFor(Appointment instance) => $"{instance.Patient.Name} / slot {instance.Slot.Id:D2}";
}

public sealed record User(string Name) : IResource<User>
{
    public static ResourceKey KeyFor(User instance) => instance.Name;
    public static string? LabelFor(User instance) => instance.Name;
}

public sealed record ImportResult(int Count);

// The application's domain DSL. Steps are plain instance methods on step classes deriving from
// Given/When/Then — real implementations returning ordinary Task<T> domain results, no Step<T>
// handles, no stubs. [StepName] sets how a step reads in the test runner; {placeholder}s bind to
// parameters. The classes are partial: LifecycleDsl.cs adds the rest of an appointment's life.
//
// This suite needs no world. CancellationScenarios.cs shows a suite with shared state.
// Inside a step, Context is the running step's ScenarioContext, never null.
//
// Each step authors how long it "took" via Context.SimulateElapsed — under the showcase's
// simulated-time scheduler (Program.cs opts in with simulateTime: true) this advances the step's own
// clock with no real waiting, so the HTML report's Gantt shows realistic, overlapping bars. On a real
// run the same calls are inert no-ops. The durations are deliberately VARIED and easy to tweak;
// parallel siblings (independent arrange steps, bulk imports) overlap, and the booking scenarios run
// past 1s so the report's auto-scaling time ruler switches from ms to s units.

/// <summary>Arrange steps: patients, slots and users.</summary>
public sealed partial class AppointmentGiven : Given
{
    [StepName("Given the database is clean")]
    public Task DatabaseIsClean()
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(250));
        return Task.CompletedTask;
    }

    [StepName("Given patient {$return ?? name} exists")]
    [StepName("Given patient {$return ?? name} exists in {city}")]
    [return: Registered]
    public Task<Patient> PatientExists(string name, string? city = null)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(320));

        // Registered where the value is in scope: a real suite would delete the row here, on the
        // connection this step used. The cleanup runs inside the scenario's Teardown step, long
        // after this step has been reported, so it reports through the teardown context it is
        // handed — `Context` read inside the cleanup is that teardown context too.
        Context.OnTeardown(teardown =>
        {
            teardown.Log($"cleaned up patient {name}");
            return Task.CompletedTask;
        });

        return Task.FromResult(new Patient($"patient-{name}", name, city));
    }

    [StepName("Given {$return} is available")]
    [return: Created]
    public Task<Slot> AvailableSlot()
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(460));
        return Task.FromResult(new Slot(1));
    }

    [StepName("Given {patient} is a priority case")]
    public Task<bool> PatientIsPriority([Read] Patient patient)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(180));
        // A deterministic demo rule: names starting with a letter before 'M' are priority.
        return Task.FromResult(patient.Name.Length > 0 && char.ToUpperInvariant(patient.Name[0]) < 'M');
    }

    [StepName("Given user {$return ?? name} exists")]
    [return: Created]
    public Task<User> UserExists(string name)
    {
        // A small, deterministic per-name jitter so the bulk-import lanes show different bar
        // lengths instead of identical clones (still no real waiting).
        var ms = 140 + (name.Sum(c => (int)c) % 5) * 45;
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(ms));
        return Task.FromResult(new User(name));
    }
}

/// <summary>Actions: booking and importing.</summary>
public sealed partial class AppointmentWhen : When
{
    [StepName("When booking {patient} in {slot}")]
    [return: Booked(Patient = nameof(patient), Slot = nameof(slot))]
    public Task<Appointment> CreateAppointment(Patient patient, Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(600));
        return Task.FromResult(new Appointment(patient, slot));
    }

    [StepName("When urgently booking {patient} in {slot2}")]
    [return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]
    public Task<Appointment> CreateUrgentAppointment(Patient patient, Slot slot2)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(410));
        return Task.FromResult(new Appointment(patient, slot2));
    }

    [StepName("When importing the users")]
    public Task<ImportResult> ImportUsers(User[] users)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(300));
        return Task.FromResult(new ImportResult(users.Length));
    }
}

/// <summary>Assertions on bookings and imports.</summary>
public sealed partial class AppointmentThen : Then
{
    [StepName("Then {appointment.Patient} has an appointment in {appointment.Slot}")]
    public Task AppointmentExists([Read] Appointment appointment)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(170));
        Assert.NotNull(appointment.Patient);
        Assert.NotNull(appointment.Slot);
        return Task.CompletedTask;
    }

    [StepName("Then the import should contain {expected} users (actual {import.Count:D2})")]
    public Task ImportShouldContainUsers(ImportResult import, int expected)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(110));
        Assert.Equal(expected, import.Count);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The phases every appointment scenario class reaches its steps through, declared once. Go-to-definition
/// on <c>Given</c> in a scenario lands here.
/// </summary>
public abstract class AppointmentSuite : Raun.Scenarios
{
    public AppointmentGiven Given => Steps<AppointmentGiven>();

    public AppointmentWhen When => Steps<AppointmentWhen>();

    public AppointmentThen Then => Steps<AppointmentThen>();

    public AppointmentEventually Eventually => Steps<AppointmentEventually>();
}
