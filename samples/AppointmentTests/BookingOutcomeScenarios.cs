using Raun;
using System.ComponentModel;

namespace AppointmentTests;

/// <summary>
/// Pattern branching: a <c>switch</c> over a step's awaited result. Exactly one section runs, picked by
/// the type of the value the step produced; the others are reported not-taken, and a pattern variable
/// such as <c>booked</c> is the arm's own step output. The demo rule is deterministic, so each
/// scenario takes a known arm: Ada is booked, Wren is waitlisted.
/// </summary>
[DisplayName("Booking outcome")]
public sealed class BookingOutcomeScenarios : AppointmentSuite
{
    [Scenario("a booking either lands, waits, or is refused")]
    public async Task BookingOutcome()
    {
        var patient = await Given.PatientExists("Ada");
        var slot = await Given.AvailableSlot();

        switch (await When.TryToBook(patient, slot))
        {
            case Booked booked:
                await Then.AppointmentExists(booked.Appointment);
                break;
            case Waitlisted waitlisted:
                await Then.PatientIsOnWaitlist(patient, waitlisted.Position);
                break;
            case Refused refused:
                await Then.PatientWasTold(patient, refused.Reason);
                break;
        }
    }

    [Scenario("a patient whose booking waits is put on the waitlist")]
    public async Task BookingWaits()
    {
        var patient = await Given.PatientExists("Wren");
        var slot = await Given.AvailableSlot();

        switch (await When.TryToBook(patient, slot))
        {
            case Booked booked:
                await Then.AppointmentExists(booked.Appointment);
                break;
            case Waitlisted waitlisted:
                await Then.PatientIsOnWaitlist(patient, waitlisted.Position);
                break;
            case Refused refused:
                await Then.PatientWasTold(patient, refused.Reason);
                break;
        }
    }
}
