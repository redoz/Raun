using Raun;
using System.ComponentModel;

namespace AppointmentTests;

/// <summary>
/// What happens to an appointment after it is booked. Same authoring model as <see cref="Scenarios"/>;
/// these lean on the parts of the framework the booking scenarios do not need: teardown policy and
/// required cleanups, the Loaded/Edited/Deleted roles, a bare <c>if</c>, a step timeout, attachments,
/// ILogger, and a custom phase marker.
/// </summary>
[DisplayName("Appointment lifecycle")]
public sealed class LifecycleScenarios : AppointmentSuite
{
    // Run.OnSuccess: a failed reschedule leaves its rows behind for inspection instead of cleaning up.
    // The hold on the target slot is the exception — HoldSlot registers its release as
    // Cleanup.Required, because a leaked hold blocks real bookings — so it is released either way.
    [Scenario("patient reschedules to a later slot")]
    [Teardown(Run.OnSuccess)]
    public async Task Reschedule()
    {
        var patient = await Given.PatientExists("Erik");
        var (morning, afternoon) = await (
            Given.AvailableSlotAt(9),
            Given.AvailableSlotAt(14));

        var appointment = await When.CreateAppointment(patient, morning);

        // Sequential on purpose. `await (When.HoldSlot(afternoon), When.Reschedule(appointment, afternoon))`
        // would be rejected at compile time (RAUN013): both steps touch `afternoon` in one parallel
        // group and HoldSlot mutates it, so nothing would order the hold before the move.
        var held = await When.HoldSlot(afternoon);
        var moved = await When.Reschedule(appointment, held);

        await Then.AppointmentIsIn(moved, held);
    }

    // The patient already exists (Loaded, not Created), so there is nothing of theirs to tear down;
    // the appointment is Created and then Deleted in order, which the trace shows as two effects on
    // one identity.
    [Scenario("patient cancels an existing appointment")]
    public async Task Cancel()
    {
        var patient = await Given.PatientOnFile("Maja");
        var slot = await Given.AvailableSlotAt(11);
        var appointment = await When.CreateAppointment(patient, slot);

        await When.CancelAppointment(appointment);

        await Then.SlotIsFree(slot);
    }

    // A bare `if` — no else, nothing merges — guards a two-step arm. The condition is an ordinary
    // awaited step, so it is discovered and reported like any other; the arm's steps run only when
    // it holds. The scenario also carries a whole-scenario timeout alongside SendTravelReminder's
    // per-step one.
    [Scenario("out-of-town patients get a travel reminder", Timeout = 30_000)]
    public async Task TravelReminder()
    {
        var patient = await Given.PatientExists("Sven");
        var traveller = await Given.PatientLivesIn(patient, "Kiruna");
        var slot = await Given.AvailableSlotAt(10);

        var appointment = await When.CreateAppointment(traveller, slot);

        if (await Given.PatientLivesFarAway(traveller))
        {
            await When.SendTravelReminder(appointment);
            await Eventually.ReminderIsDelivered(appointment);
        }

        await Then.AppointmentExists(appointment);
    }
}
