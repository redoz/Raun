using Raun;
using Xunit;

namespace AppointmentTests;

// What booking can come back with. A step returns the abstract record; a scenario `switch` on it picks
// the arm by the concrete type the step actually produced.
public abstract record BookingOutcome;

public sealed record Booked(Appointment Appointment) : BookingOutcome;

public sealed record Waitlisted(int Position) : BookingOutcome;

public sealed record Refused(string Reason) : BookingOutcome;

public sealed partial class AppointmentWhen : When<NoWorld>
{
    // Deterministic demo rule: a patient is booked unless their name starts with 'W' (waitlisted) or 'R'
    // (refused). The slot is only consumed when the booking lands.
    [StepName("When trying to book {patient}")]
    public Task<BookingOutcome> TryToBook([Read] Patient patient, [Read] Slot slot)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(350));
        BookingOutcome outcome = char.ToUpperInvariant(patient.Name[0]) switch
        {
            'W' => new Waitlisted(3),
            'R' => new Refused("no clinicians available"),
            _ => new Booked(new Appointment(patient, slot)),
        };
        return Task.FromResult(outcome);
    }
}

public sealed partial class AppointmentThen : Then<NoWorld>
{
    [StepName("Then the patient is {position} on the waitlist")]
    public Task PatientIsOnWaitlist([Read] Patient patient, int position)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        Assert.True(position > 0);
        return Task.CompletedTask;
    }

    [StepName("Then the patient is told {reason}")]
    public Task PatientWasTold([Read] Patient patient, string reason)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        Assert.NotEmpty(reason);
        return Task.CompletedTask;
    }
}
