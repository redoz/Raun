using System.Net;
using System.Net.Http.Json;
using Raun;
using Microsoft.Extensions.DependencyInjection;

namespace AspireAppointments.Tests;

/// <summary>
/// The suite's own DSL. Raun.Aspire ships no steps — it is plumbing only — so these are ordinary
/// phase-marker extension members that reach the running application through <c>ctx.Services</c>.
/// </summary>
public static class AppointmentsDsl
{
    private static AppointmentsApi Api(ScenarioContext? ctx) =>
        ctx!.Services!.GetRequiredService<AppointmentsApi>();

    extension(Given)
    {
        [StepName("the API is reachable")]
        public static async Task<AppointmentDto[]> ApiIsReachable(ScenarioContext? ctx = null) =>
            await Api(ctx).As(Actor.Patient).ListAsync();
    }

    extension(When)
    {
        [StepName("an admin books {patient} into {slot}")]
        public static async Task<AppointmentDto> AdminBooks(
            string patient, string slot, ScenarioContext? ctx = null)
        {
            var response = await Api(ctx).As(Actor.Admin).CreateAsync(patient, slot);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"booking failed: {response.StatusCode}");
            }

            var created = await response.Content.ReadFromJsonAsync<AppointmentDto>()
                ?? throw new InvalidOperationException("booking returned no body");

            ctx?.Log($"booked appointment {created.Id}");
            return created;
        }

        [StepName("a patient tries to book {patient} into {slot}")]
        public static async Task<HttpStatusCode> PatientTriesToBook(
            string patient, string slot, ScenarioContext? ctx = null)
        {
            var response = await Api(ctx).As(Actor.Patient).CreateAsync(patient, slot);
            return response.StatusCode;
        }

        [StepName("an admin clears the schedule")]
        public static async Task AdminClearsSchedule(ScenarioContext? ctx = null)
        {
            var response = await Api(ctx).As(Actor.Admin).ClearAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"clear failed: {response.StatusCode}");
            }
        }
    }

    extension(Then)
    {
        [StepName("the patient can read appointment {appointment}")]
        public static async Task PatientCanRead(AppointmentDto appointment, ScenarioContext? ctx = null)
        {
            // The SECOND identity in the same scenario: booked as an admin, read back as a patient.
            var read = await Api(ctx).As(Actor.Patient).GetAsync(appointment.Id);
            if (read is null || read.Patient != appointment.Patient)
            {
                throw new InvalidOperationException(
                    $"appointment {appointment.Id} did not read back as booked");
            }
        }

        [StepName("the attempt was rejected as {status}")]
        public static Task AttemptWasRejected(HttpStatusCode status)
            => status == HttpStatusCode.Forbidden
                ? Task.CompletedTask
                : throw new InvalidOperationException($"expected Forbidden, got {status}");

        [StepName("the schedule is empty")]
        public static async Task ScheduleIsEmpty(ScenarioContext? ctx = null)
        {
            var remaining = await Api(ctx).As(Actor.Patient).ListAsync();
            if (remaining.Length != 0)
            {
                throw new InvalidOperationException($"expected an empty schedule, found {remaining.Length} appointment(s)");
            }
        }
    }
}
