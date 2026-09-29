using System.Net;
using System.Net.Http.Json;
using Raun;
using Microsoft.Extensions.DependencyInjection;

namespace AspireAppointments.Tests;

/// <summary>
/// The scenario's world: the API the steps drive, resolved once per scenario from the services
/// Program.cs registers. Raun.Aspire ships no steps — it is plumbing only — so this, like the steps
/// below, is the suite's own code. The AppHost is already running when a world is created: it started
/// as the run's preflight, before any scenario.
/// </summary>
public sealed class AppointmentsWorld : IScenarioWorld<AppointmentsWorld>
{
    private AppointmentsWorld(AppointmentsApi api) => Api = api;

    /// <summary>Hands out a client bound to one identity; see <see cref="AppointmentsApi"/>.</summary>
    public AppointmentsApi Api { get; }

    public static ValueTask<AppointmentsWorld> CreateAsync(ScenarioContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.Services
            ?? throw new InvalidOperationException("The appointments suite runs through RaunAspire, which provides its services.");
        return ValueTask.FromResult(new AppointmentsWorld(services.GetRequiredService<AppointmentsApi>()));
    }
}

/// <summary>Arrange steps.</summary>
public sealed class AppointmentsGiven : Given<AppointmentsWorld>
{
    [StepName("the API is reachable")]
    public async Task<AppointmentDto[]> ApiIsReachable() =>
        await World.Api.As(Actor.Patient).ListAsync();
}

/// <summary>Actions against the API, each as the actor it names.</summary>
public sealed class AppointmentsWhen : When<AppointmentsWorld>
{
    [StepName("an admin books {patient} into {slot}")]
    public async Task<AppointmentDto> AdminBooks(string patient, string slot)
    {
        var response = await World.Api.As(Actor.Admin).CreateAsync(patient, slot);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"booking failed: {response.StatusCode}");
        }

        var created = await response.Content.ReadFromJsonAsync<AppointmentDto>()
            ?? throw new InvalidOperationException("booking returned no body");

        Context.Log($"booked appointment {created.Id}");
        return created;
    }

    [StepName("a patient tries to book {patient} into {slot}")]
    public async Task<HttpStatusCode> PatientTriesToBook(string patient, string slot)
    {
        var response = await World.Api.As(Actor.Patient).CreateAsync(patient, slot);
        return response.StatusCode;
    }

    [StepName("an admin clears the schedule")]
    public async Task AdminClearsSchedule()
    {
        var response = await World.Api.As(Actor.Admin).ClearAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"clear failed: {response.StatusCode}");
        }
    }
}

/// <summary>Assertions, read back through the API.</summary>
public sealed class AppointmentsThen : Then<AppointmentsWorld>
{
    [StepName("the patient can read appointment {appointment}")]
    public async Task PatientCanRead(AppointmentDto appointment)
    {
        // The SECOND identity in the same scenario: booked as an admin, read back as a patient.
        var read = await World.Api.As(Actor.Patient).GetAsync(appointment.Id);
        if (read is null || read.Patient != appointment.Patient)
        {
            throw new InvalidOperationException(
                $"appointment {appointment.Id} did not read back as booked");
        }
    }

    [StepName("the attempt was rejected as {status}")]
    public Task AttemptWasRejected(HttpStatusCode status)
        => status == HttpStatusCode.Forbidden
            ? Task.CompletedTask
            : throw new InvalidOperationException($"expected Forbidden, got {status}");

    [StepName("the schedule is empty")]
    public async Task ScheduleIsEmpty()
    {
        var remaining = await World.Api.As(Actor.Patient).ListAsync();
        if (remaining.Length != 0)
        {
            throw new InvalidOperationException($"expected an empty schedule, found {remaining.Length} appointment(s)");
        }
    }
}

/// <summary>The phases the scenarios reach their steps through, declared once.</summary>
public abstract class AppointmentsSuite : Scenarios<AppointmentsWorld>
{
    public AppointmentsGiven Given => Steps<AppointmentsGiven>();

    public AppointmentsWhen When => Steps<AppointmentsWhen>();

    public AppointmentsThen Then => Steps<AppointmentsThen>();
}
