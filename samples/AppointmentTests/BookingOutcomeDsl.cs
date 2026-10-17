using Raun;
using Xunit;

namespace AppointmentTests;

// What booking can come back with. A step returns the abstract record; a scenario `switch` on it picks
// the arm by the concrete type the step actually produced.
public abstract record BookingOutcome;

public sealed record Booked(Appointment Appointment) : BookingOutcome;

public sealed record Waitlisted(int Position) : BookingOutcome;

public sealed record Refused(string Reason) : BookingOutcome;

public sealed partial class AppointmentWhen : When
{
    // A held slot cannot be booked. The patient can join the waitlist or decline it.
    // Wrapped outcomes need imperative resource recording: only Booked contains a new resource.
    [StepName("When trying to book {patient}")]
    public async Task<BookingOutcome> TryToBook([Read] Patient patient, [Read] Slot slot, bool acceptsWaitlist = true)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(350));
        if (slot.Held)
        {
            return acceptsWaitlist ? new Waitlisted(3) : new Refused("the patient declined the waitlist");
        }

        var appointment = new Appointment(patient, slot);
        await Context.Resources.Apply(ResourceAction.For<BookedAttribute>(), appointment);
        await Context.Resources.Reference(patient, appointment);
        await Context.Resources.Relate(ResourceAction.For<ReservedAttribute>(), appointment, slot);
        return new Booked(appointment);
    }
}

public sealed partial class AppointmentThen : Then
{
    [StepName("Then {patient} is {position} on the waitlist")]
    public Task PatientIsOnWaitlist([Read] Patient patient, int position)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        Assert.True(position > 0);
        return Task.CompletedTask;
    }

    [StepName("Then {patient} is told {reason}")]
    public Task PatientWasTold([Read] Patient patient, string reason)
    {
        Context.SimulateElapsed(TimeSpan.FromMilliseconds(120));
        Assert.NotEmpty(reason);
        return Task.CompletedTask;
    }
}
