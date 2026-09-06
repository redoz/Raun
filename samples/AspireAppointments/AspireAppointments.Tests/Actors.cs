using System.Net.Http.Json;

namespace AspireAppointments.Tests;

/// <summary>The identities a scenario can act as. Mocked: the API trusts an <c>X-Role</c> header.</summary>
public enum Actor
{
    /// <summary>May create appointments.</summary>
    Admin,

    /// <summary>May read appointments, but not create them.</summary>
    Patient,
}

/// <summary>
/// Hands out an API client bound to one identity.
/// </summary>
/// <remarks>
/// Actor is a property of the CALL, not of the scenario or even of the step — one scenario
/// routinely acts as an admin and then as a patient. That is why the identity is an argument here
/// rather than ambient state, and why nothing mutates headers on a shared client: Raun runs a
/// scenario's steps concurrently, so a shared client's <c>DefaultRequestHeaders</c> would race.
/// <para>
/// Clients come from <see cref="IHttpClientFactory"/>, which pools and rotates the underlying
/// handler. Each <c>CreateClient</c> is a cheap wrapper over that shared pool, so per-identity —
/// even per-call — clients cost nothing and are isolated by construction.
/// </para>
/// </remarks>
public sealed class AppointmentsApi(IHttpClientFactory factory)
{
    /// <summary>An API surface acting as <paramref name="actor"/>.</summary>
    public AppointmentsClient As(Actor actor)
    {
        var http = factory.CreateClient("api");
        http.DefaultRequestHeaders.Add("X-Role", actor switch
        {
            Actor.Admin => "admin",
            Actor.Patient => "patient",
            _ => throw new ArgumentOutOfRangeException(nameof(actor)),
        });
        return new AppointmentsClient(http);
    }
}

/// <summary>
/// A hand-written typed client. Deliberately not Kiota: the dependency-injection shape is identical,
/// and generating it would drag an OpenAPI document and a codegen step into a sample that is meant to
/// teach Raun.
/// </summary>
public sealed class AppointmentsClient(HttpClient http)
{
    /// <summary>Creates an appointment. The API allows this for <see cref="Actor.Admin"/> only.</summary>
    public async Task<HttpResponseMessage> CreateAsync(string patient, string slot, bool urgent = false) =>
        await http.PostAsJsonAsync("/appointments", new { patient, slot, urgent });

    /// <summary>Reads one appointment by id.</summary>
    public async Task<AppointmentDto?> GetAsync(int id)
    {
        var response = await http.GetAsync(new Uri($"/appointments/{id}", UriKind.Relative));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AppointmentDto>()
            : null;
    }

    /// <summary>Reads every appointment.</summary>
    public async Task<AppointmentDto[]> ListAsync() =>
        await http.GetFromJsonAsync<AppointmentDto[]>("/appointments") ?? [];

    /// <summary>Removes every appointment. The API allows this for <see cref="Actor.Admin"/> only.</summary>
    public async Task<HttpResponseMessage> ClearAsync() =>
        await http.DeleteAsync(new Uri("/appointments", UriKind.Relative));
}

/// <summary>An appointment as the API returns it.</summary>
public sealed record AppointmentDto(int Id, string Patient, string Slot, bool Urgent);
