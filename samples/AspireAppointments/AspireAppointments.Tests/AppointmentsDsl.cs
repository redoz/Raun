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
    [StepName("Given patient {$return ?? name:id} can reach the API")]
    [return: Created]
    public async Task<Patient> PatientCanReachApi(string name)
    {
        await World.Api.As(Actor.Patient).ListAsync();
        return new Patient(name);
    }

    [StepName("Given the API is reachable")]
    [return: Read]
    public async Task<AppointmentDto[]> ApiIsReachable() =>
        await World.Api.As(Actor.Patient).ListAsync();
}

/// <summary>Actions against the API, each as the actor it names.</summary>
public sealed class AppointmentsWhen : When<AppointmentsWorld>
{
    [StepName("When an admin books {patient:id} into {slot}")]
    [return: Created]
    public async Task<AppointmentDto> AdminBooks([Read] Patient patient, string slot)
    {
        var response = await World.Api.As(Actor.Admin).CreateAsync(patient.Name, slot);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"booking failed: {response.StatusCode}");
        }

        var created = await response.Content.ReadFromJsonAsync<AppointmentDto>()
            ?? throw new InvalidOperationException("booking returned no body");

        Context.Log($"Booked {created}.");
        return created;
    }

    [StepName("When a patient tries to book {patient:id} into {slot}")]
    public async Task<HttpStatusCode> PatientTriesToBook([Read] Patient patient, string slot)
    {
        var response = await World.Api.As(Actor.Patient).CreateAsync(patient.Name, slot);
        return response.StatusCode;
    }

    [StepName("When an admin clears the schedule")]
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
    [StepName("Then the patient ({patient:id}) can read appointment {appointment:id}")]
    public async Task PatientCanRead([Read] Patient patient, [Read] AppointmentDto appointment)
    {
        Context.Log($"Using {appointment} to test access for {patient:id}.");
        // The SECOND identity in the same scenario: booked as an admin, read back as a patient.
        var read = await World.Api.As(Actor.Patient).GetAsync(appointment.Id);
        if (read is null || read.Patient != patient.Name)
        {
            throw new InvalidOperationException(
                $"appointment {appointment.Id} did not read back as booked");
        }
    }

    [StepName("Then the attempt was rejected as {status}")]
    public Task AttemptWasRejected(HttpStatusCode status)
        => status == HttpStatusCode.Forbidden
            ? Task.CompletedTask
            : throw new InvalidOperationException($"expected Forbidden, got {status}");

    [StepName("Then the schedule is empty")]
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
