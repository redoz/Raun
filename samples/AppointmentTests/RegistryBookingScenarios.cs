using System.ComponentModel;
using Raun;
using Xunit;

namespace AppointmentTests;

// A registry or API model we do not control. Patient declares its alternate identity contract.
public sealed record RegistryPatient(string PatientId, string FullName);

/// <summary>The step approves a registered patient for booking.</summary>
[DisplayName("Approve")]
public sealed class ApprovedAttribute : UpdatedAttribute;
/// <summary>The step reads patient booking evidence. It does not change the patient.</summary>
public sealed class AuditedAttribute : ReadAttribute;

/// <summary>An isolated registry and booking store for each scenario.</summary>
public sealed class PatientRegistry : IScenarioWorld<PatientRegistry>
{
    private readonly Dictionary<string, RegistryPatient> _patients = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Appointment> _appointments = new(StringComparer.Ordinal);

    public static ValueTask<PatientRegistry> CreateAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Log($"Opened an isolated patient registry");
        return ValueTask.FromResult(new PatientRegistry());
    }

    public void Register(RegistryPatient row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _patients.Add(row.PatientId, row);
    }

    public Patient Approve(RegistryPatient row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var registered = _patients[row.PatientId];
        _approved.Add(registered.PatientId);
        return new Patient(registered.PatientId, registered.FullName);
    }

    public Appointment Book(string patientId, Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!_approved.Contains(patientId)) { throw new InvalidOperationException("The patient must be approved before booking."); }
        var row = _patients[patientId];
        var appointment = new Appointment(new Patient(row.PatientId, row.FullName), slot);
        _appointments.Add(Appointment.KeyFor(appointment).Value, appointment);
        return appointment;
    }

    public Appointment FindBooking(string patientId, Slot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _appointments[$"{patientId}@{slot.Id}"];
    }
}

public sealed class RegistryGiven : Given<PatientRegistry>
{
    [StepName("Given {$return ?? name} is registered as {$return ?? patientId:id}")]
    [return: Registered]
    public Task<RegistryPatient> RegisteredPatient(string patientId, string name)
    {
        var row = new RegistryPatient(patientId, name);
        World.Register(row);
        return Task.FromResult(row);
    }

    [StepName("Given {$return} is available")]
    [return: Created]
    public Task<Slot> AvailableSlot() => Task.FromResult(new Slot(1));
}

public sealed class RegistryWhen : When<PatientRegistry>
{
    [StepName("When approving {patient}")]
    [return: Read]
    public Task<Patient> ApprovePatient([Approved] RegistryPatient patient)
        => Task.FromResult(World.Approve(patient));

    // The service accepts an ID, not a patient object. The typed property on Booked still
    // links the booking to Patient, and the preceding row/domain observations supply its name.
    [StepName("When booking {patientId} in {slot}")]
    [return: Actions.Booked]
    public Task<Appointment> BookById([Actions.Booked.Patient, ResourceKey<Patient>] string patientId, [Actions.Booked.Slot] Slot slot)
        => Task.FromResult(World.Book(patientId, slot));
}

public sealed class RegistryThen : Then<PatientRegistry>
{
    [StepName("Then {appointment} is recorded for {patientId}")]
    public Task AuditBooking([Audited, ResourceKey<Patient>] string patientId, [Read] Appointment appointment)
    {
        var stored = World.FindBooking(patientId, appointment.Slot);
        Assert.Equal(patientId, stored.Patient.Id);
        Assert.Equal(appointment, stored);
        Context.Log($"Audited {new ResourceIdentity(typeof(Patient), patientId)}");
        return Task.CompletedTask;
    }
}

public sealed class RegistryBookingScenarios : Scenarios<PatientRegistry>
{
    public RegistryGiven Given => Steps<RegistryGiven>();
    public RegistryWhen When => Steps<RegistryWhen>();
    public RegistryThen Then => Steps<RegistryThen>();

    [Scenario("registered patient books by ID")]
    public async Task BookRegisteredPatient()
    {
        var row = await Given.RegisteredPatient("pat-42", "Jane");
        var patient = await When.ApprovePatient(row);
        var slot = await Given.AvailableSlot();
        var appointment = await When.BookById(patient.Id, slot);
        await Then.AuditBooking(patient.Id, appointment);
    }
}
