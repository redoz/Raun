using System.ComponentModel;
using Raun;

namespace AspireAppointments.Tests;

/// <summary>
/// The one thing these scenarios contend for: the API's schedule. Booking scenarios share it —
/// each books its own patient — while clearing it needs it exclusively. Scenarios run concurrently
/// by default; this declaration is what keeps "clear" from running between another scenario's
/// "admin books" and "patient reads". It is a name, not a service: Raun never constructs it.
/// </summary>
[SharedResource]
public sealed class Schedule : IContendedResource;

/// <summary>
/// Scenarios driving a real Aspire application. The AppHost is started once for the run, as the
/// preflight node — so its startup is timed and reported, and a failure to come up is a failing test
/// rather than a process that exits before anything reports.
/// </summary>
[DisplayName("Appointments API")]
[Uses<Schedule>]
public static class Scenarios
{
    // Two actors in one scenario: booked as an admin, read back as a patient. The actor belongs to
    // the call, which is why nothing here mutates headers on a shared client.
    [Scenario("an admin books an appointment a patient can read")]
    public static async Task AdminBooksPatientReads()
    {
        await Given.ApiIsReachable();

        var appointment = await When.AdminBooks("Alice", "2026-09-05T09:00");

        await Then.PatientCanRead(appointment);
    }

    [Scenario("a patient may not book appointments")]
    public static async Task PatientCannotBook()
    {
        await Given.ApiIsReachable();

        var status = await When.PatientTriesToBook("Bob", "2026-09-05T10:00");

        await Then.AttemptWasRejected(status);
    }

    // Exclusive: waits until no booking scenario holds the schedule, and keeps new ones out until
    // it is done. Without this, "the patient can read" above could race against the clear.
    [Scenario("an admin clears the schedule")]
    [Uses<Schedule>(LockMode.Exclusive)]
    public static async Task AdminClearsSchedule()
    {
        await Given.ApiIsReachable();

        await When.AdminClearsSchedule();

        await Then.ScheduleIsEmpty();
    }
}
