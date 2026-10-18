using System.Collections.Concurrent;
using Raun;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AppointmentTests;

// The rest of an appointment's life: holds, rescheduling, cancellation, reminders. The same partial
// step classes as AppointmentDsl.cs; split out so the two files each read as one story.

/// <summary>Arrange steps for patients already on file and their slots.</summary>
public sealed partial class AppointmentGiven : Given
{
    // A lookup, not a creation: the patient already exists, so the role is Read and there is
    // nothing to tear down.
    [StepName("Given patient {$return ?? name} is on file")]
    [return: Read]
    public Task<Patient> PatientOnFile(string name)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(90));
        return Task.FromResult(new Patient($"patient-{name}", name));
    }

    [StepName("Given {$return ?? hour} is available")]
    [return: Created]
    public Task<Slot> AvailableSlotAt(int hour)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(210));
        return Task.FromResult(new Slot(hour));
    }

    // Edits the patient and returns the edited patient: [Updated] on the parameter, [return: Updated]
    // on the result. Both resolve to Patient:{id}, so the trace shows one Update, not two.
    [StepName("Given {patient} lives in {city}")]
    [return: Updated]
    public Task<Patient> PatientLivesIn([Updated] Patient patient, string city)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        return Task.FromResult(patient with { City = city });
    }

    [StepName("Given {patient} lives far away")]
    public Task<bool> PatientLivesFarAway([Read] Patient patient)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(60));
        return Task.FromResult(patient.City is { } city && city != "Stockholm");
    }
}

public sealed partial class AppointmentWhen : When
{
    // A hold is a lease on real capacity. Leaking one blocks real bookings, so releasing it is
    // Cleanup.Required: it runs whatever the scenario's [Teardown(Run.…)] policy says.
    [StepName("When {slot} is put on hold")]
    [return: Held]
    public Task<Slot> HoldSlot([Held] Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(140));
        Context.OnTeardown(Cleanup.Required, teardown =>
        {
            teardown.Log($"released hold on slot {slot.Id}");
            return Task.CompletedTask;
        });

        return Task.FromResult(slot with { Held = true });
    }

    // Rescheduling removes the old booking and creates a new one in the target slot. The old
    // appointment is [Deleted]; the new one consumes the slot it moves into.
    [StepName("When rescheduling {appointment} into {slot}")]
    [return: Rescheduled]
    public Task<Appointment> Reschedule([Cancelled] Appointment appointment, [Rescheduled.Slot] Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(480));
        return Task.FromResult(new Appointment(appointment.Patient, slot));
    }

    // Context.GetLogger<T>() is an ILogger whose lines land in this step's log — the same channel
    // Context.Log writes to — so production-style logging (here a LoggerMessage delegate) needs no
    // test-specific sink.
    [StepName("When cancelling {appointment}")]
    public Task CancelAppointment([Cancelled] Appointment appointment)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(260));
        var logger = Context.GetLogger("Clinic.Bookings");
        ClinicLog.Cancelled(logger, appointment.Patient.Name, appointment.Slot.Id);
        return Task.CompletedTask;
    }

    // Talks to an external service, so it carries a per-step timeout. The reminder text is kept
    // as an attachment on the step, where the report shows it.
    [StepName("When a travel reminder is sent for {appointment}", TimeoutMs = 5000)]
    public async Task SendTravelReminder([Read] Appointment appointment)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(700));
        var text = $"Hi {appointment.Patient.Name}, your appointment is at {appointment.Slot.Id}:00. "
            + $"Plan your trip from {appointment.Patient.City}.";
        await Outbox.SendAsync(appointment.Patient.Name, text);
        Context.AddAttachment("reminder", text);
    }
}

public sealed partial class AppointmentThen : Then
{
    [StepName("Then {appointment} sits in {slot}")]
    public Task AppointmentIsIn([Read] Appointment appointment, [Read] Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(80));
        Assert.Equal(slot, appointment.Slot);
        return Task.CompletedTask;
    }

    [StepName("Then {slot} is available again")]
    public Task SlotIsFree([Read] Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(70));
        Assert.False(slot.Held);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A custom phase: a step class with its own <see cref="PhaseNameAttribute"/>, whose name is the phase
/// label the runner shows — so a clinic can say <c>Eventually.ReminderIsDelivered(...)</c> for checks
/// that become true once an asynchronous process (an outbox, a projection) has caught up.
/// </summary>
[PhaseName("Eventually")]
public sealed class AppointmentEventually : Phase
{
    [StepName("Eventually the reminder for {appointment} is delivered")]
    public Task ReminderIsDelivered([Read] Appointment appointment)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(150));
        Assert.True(Outbox.WasDelivered(appointment.Patient.Name), "no reminder reached the patient");
        return Task.CompletedTask;
    }
}

/// <summary>
/// A stand-in for the clinic's notification service. It sits below the DSL and is handed no
/// <see cref="ScenarioContext"/>, yet its lines still land on the running step:
/// <see cref="ScenarioContext.Current"/> is ambient per step, so domain code can report without
/// a context threaded through every signature.
/// </summary>
internal static class Outbox
{
    private static readonly ConcurrentDictionary<string, string> Sent = new(StringComparer.Ordinal);

    public static Task SendAsync(string recipient, string body)
    {
        Sent[recipient] = body;
        ScenarioContext.Current?.Log($"outbox: delivered reminder to {recipient}");
        return Task.CompletedTask;
    }

    public static bool WasDelivered(string recipient) => Sent.ContainsKey(recipient);
}

/// <summary>Production-style structured logging: a LoggerMessage delegate, no string formatting at the call site.</summary>
internal static partial class ClinicLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "cancelled appointment for {Patient} at {Hour}:00")]
    public static partial void Cancelled(ILogger logger, string patient, int hour);
}
