using Raun;
using Xunit;

namespace GettingStarted;

// No Program.cs or startup configuration: Raun generates the test host.
public sealed class BookingScenarios : Scenarios<NoWorld>
{
    public BookingGiven Given => Steps<BookingGiven>();
    public BookingWhen When => Steps<BookingWhen>();
    public BookingThen Then => Steps<BookingThen>();

    [Scenario("a patient books an available slot")]
    public async Task BookAppointment()
    {
        var patient = await Given.PatientExists("pat-1", "Jane");
        var slot = await Given.AvailableSlot(9);
        var appointment = await When.Book(patient, slot);
        await Then.AppointmentIsConfirmed(appointment, patient, slot);
    }
}

public sealed record Patient(string Id, string Name) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;
}

public sealed record Slot(int Hour) : IResource<Slot>
{
    public static ResourceKey KeyFor(Slot slot) => slot.Hour.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string? LabelFor(Slot slot) => $"the {slot.Hour:D2}:00 slot";
}

public sealed record Appointment(Patient Patient, Slot Slot) : IResource<Appointment>
{
    public static ResourceKey KeyFor(Appointment appointment) => $"{appointment.Patient.Id}@{appointment.Slot.Hour}";
    public static string? LabelFor(Appointment appointment) => $"{appointment.Patient.Name} / {appointment.Slot.Hour:D2}:00";
}

public sealed class BookingGiven : Given<NoWorld>
{
    [StepName("Given patient {$return ?? name} exists")]
    [return: Seed]
    public Task<Patient> PatientExists(string id, string name) => Task.FromResult(new Patient(id, name));

    [StepName("Given {$return ?? hour} is available")]
    [return: Seed]
    public Task<Slot> AvailableSlot(int hour) => Task.FromResult(new Slot(hour));
}

public sealed class Reserved : UpdatedAttribute;
public sealed class Booked : CreatedAttribute
{
    public sealed class PatientAttribute : ResourceLinkAttribute<Patient, ReadAttribute>;
    public sealed class SlotAttribute : ResourceLinkAttribute<Slot, Reserved>;
}

public sealed class BookingWhen : When<NoWorld>
{
    [StepName("When {patient} books {slot}")]
    [return: Booked]
    public Task<Appointment> Book([Booked.Patient] Patient patient, [Booked.Slot] Slot slot) => Task.FromResult(new Appointment(patient, slot));
}

public sealed class BookingThen : Then<NoWorld>
{
    [StepName("Then {appointment} is confirmed for {appointment.Patient}")]
    public Task AppointmentIsConfirmed([Read] Appointment appointment, [Read] Patient patient, [Read] Slot slot)
    {
        Assert.Equal(patient, appointment.Patient);
        Assert.Equal(slot, appointment.Slot);
        return Task.CompletedTask;
    }
}
