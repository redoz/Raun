using System.Collections.Concurrent;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// A deliberately mock service: appointments live in a dictionary and "authentication" is an X-Role
// header. The point of the sample is the Raun/Aspire wiring, not this API — anything more would
// teach ASP.NET rather than Raun, and would drag in a database the sample does not need.

var builder = WebApplication.CreateBuilder(args);

// Export the API's own spans when an OTLP endpoint is configured (the AppHost forwards the test
// process's OTEL_EXPORTER_OTLP_ENDPOINT). Every request from a Raun step carries that step span's
// traceparent, so these server spans land under the step that made the call — one trace per scenario.
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("AspireAppointments.Api"))
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddOtlpExporter());
}

var app = builder.Build();

var appointments = new ConcurrentDictionary<int, Appointment>();
var nextId = 0;

// Aspire waits on this before scenarios run; see the suite's WaitFor("api").
app.MapGet("/health", () => Results.Ok("healthy"));

app.MapPost("/appointments", (CreateAppointment request, HttpContext http) =>
{
    if (RoleOf(http) is not "admin")
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    var id = Interlocked.Increment(ref nextId);
    var created = new Appointment(id, request.Patient, request.Slot, Urgent: request.Urgent);
    appointments[id] = created;
    return Results.Created($"/appointments/{id}", created);
});

app.MapGet("/appointments/{id:int}", (int id, HttpContext http) =>
    RoleOf(http) is null
        ? Results.StatusCode(StatusCodes.Status401Unauthorized)
        : appointments.TryGetValue(id, out var appointment)
            ? Results.Ok(appointment)
            : Results.NotFound());

app.MapGet("/appointments", (HttpContext http) =>
    RoleOf(http) is null
        ? Results.StatusCode(StatusCodes.Status401Unauthorized)
        : Results.Ok(appointments.Values.OrderBy(a => a.Id).ToArray()));

// Admin-only. The suite's "clear the schedule" scenario uses this exclusively: run alongside a
// booking scenario it would empty the schedule between "admin books" and "patient reads".
app.MapDelete("/appointments", (HttpContext http) =>
{
    if (RoleOf(http) is not "admin")
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    appointments.Clear();
    return Results.NoContent();
});

app.Run();

static string? RoleOf(HttpContext http) =>
    http.Request.Headers.TryGetValue("X-Role", out var role) && role.Count > 0 ? role[0] : null;

internal sealed record CreateAppointment(string Patient, string Slot, bool Urgent = false);

internal sealed record Appointment(int Id, string Patient, string Slot, bool Urgent);
