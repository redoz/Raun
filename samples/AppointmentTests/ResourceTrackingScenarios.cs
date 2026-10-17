using Raun;
using Xunit;

namespace AppointmentTests;

// A database row can represent a Patient without implementing a Raun interface itself.
public sealed record PatientRow(string Id, string Name, string City);
public sealed record SlotRow(int Hour);
public sealed record DeliveryReceipt(string Id, string Recipient);

public sealed class ResourceTrackingScenarios : AppointmentSuite
{
    [Scenario("an imported booking keeps its resource identities")]
    public async Task ImportedBooking()
    {
        var patient = await Given.ImportedPatient("Ingrid");
        await Then.ReadPatientByKey(patient.Id);
        var slot = await Given.ClinicSlot();
        var appointment = await When.ImportBooking(patient, slot);
        await Then.AppointmentExists(appointment)
            .Note($"The imported booking belongs to {patient} and uses {slot}.");
        await When.RemoveImportedBooking(appointment);
    }

    [Scenario("queue titles reflect optional arguments and returned positions")]
    public async Task QueueTitles()
    {
        var first = await Given.QueuePosition(expected: 1);
        await Then.QueuePositionIs(first, 1);
        var second = await Given.QueuePosition(expected: 2, desk: "Reception");
        await Then.QueuePositionIs(second, 2);
    }
}

public sealed partial class AppointmentGiven : Given
{
    [StepName("Given imported patient {$return ?? name} is on file")]
    [return: Loaded]
    public Task<PatientRow> ImportedPatient(string name)
        => Task.FromResult(new PatientRow("imported-" + name, name, "Uppsala"));

    // Program.cs maps this external row to Slot and gives it a display label.
    [StepName("Given the clinic returns slot {$return}")]
    [return: Loaded]
    public Task<SlotRow> ClinicSlot() => Task.FromResult(new SlotRow(15));

    // Only the title that mentions desk is eligible when desk was explicitly supplied.
    [StepName("Given queue position {$return ?? expected,3:D2}")]
    [StepName("Given desk {desk} assigns queue position {$return ?? expected,3:D2}")]
    public Task<int> QueuePosition(int expected = 1, string? desk = null)
    {
        Context.Log($"Queue desk {desk ?? "Main"} assigned position {expected:D2}.");
        return Task.FromResult(expected);
    }
}

public sealed partial class AppointmentWhen : When
{
    [StepName("When importing the clinic booking for {patient.Name} {{external}}")]
    [return: Created]
    public async Task<Appointment> ImportBooking(PatientRow patient, [Reserved] SlotRow slot)
    {
        var appointment = new Appointment(new Patient(patient.Id, patient.Name, patient.City), new Slot(slot.Hour));
        var receipt = new DeliveryReceipt($"booking-{patient.Name}", patient.Name);
        await Context.Resources.Create(appointment);
        await Context.Resources.Reference(patient, appointment);
        await Context.Resources.Relate(ResourceAction.For<ReservedAttribute>(), appointment, slot);
        await Context.Resources.Create(receipt);
        Context.Log($"Imported {appointment}; clinic row {patient:data}; confirmation {receipt}.");
        Assert.Contains(Context.Resources.Effects, effect => effect.Identity == new ResourceIdentity("clinic-confirmation", receipt.Id));
        Assert.Contains(Context.Resources.Effects, effect => effect.Identity == new ResourceIdentity(typeof(Patient), patient.Id));
        Assert.Contains(Context.Resources.Effects, effect => effect.Identity == new ResourceIdentity(typeof(Slot), Slot.KeyFor(new Slot(slot.Hour))) && effect.Label == "Clinic slot at 15:00");
        Assert.Contains(Context.Resources.Effects, effect => effect.Label == $"Confirmation for {patient.Name}");
        Assert.Equal(2, Context.Resources.Lineage.Count);
        return appointment;
    }

    [StepName("When removing the imported booking")]
    public async Task RemoveImportedBooking([Deleted] Appointment appointment)
    {
        await Context.Resources.Read(appointment.Patient);
        await Context.Resources.Edit(appointment.Slot);
        await Context.Resources.Delete(appointment);
        Assert.Contains(Context.Resources.Effects, effect => effect.Verb == LifecycleVerb.Delete);
        Context.Log($"Removed booking {appointment:id} and released {appointment.Slot}.");
    }
}

public sealed partial class AppointmentThen : Then
{
    [StepName("Then patient {name:id} can be read by key")]
    public Task ReadPatientByKey([Read, ResourceKey<Patient>] string name)
    {
        var effect = Assert.Single(Context.Resources.Effects);
        Assert.Equal(new ResourceIdentity(typeof(Patient), name), effect.Identity);
        return Task.CompletedTask;
    }

    [StepName("Then queue position is {expected:D2}")]
    public Task QueuePositionIs(int actual, int expected)
    {
        Assert.Equal(expected, actual);
        return Task.CompletedTask;
    }
}
