using Aspire.Hosting;
using Coverlet.MTP;
using Aspire.Hosting.Testing;
using AspireAppointments.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Testing.Extensions;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Raun;
using Raun.Mtp;

// The entry point is real, readable code — no generated Main, nothing hidden. This is where the
// AppHost is described, where you say what must be healthy before scenarios run, where the suite's
// own services are registered, and where its traces go.

// Tracing. Raun emits one span per scenario and one per step from the "Raun" ActivitySource, and
// never exports; the suite decides where they go. Exporting only when an OTLP endpoint is configured
// keeps a plain `dotnet run` quiet. With the endpoint set (a standalone Aspire dashboard is one
// `docker run` away — see docs/superpowers/specs/2026-09-06-tracing-design.md), each scenario is one
// trace: the step span, the HttpClient span under it, and the API's own spans under that, because the
// step span is Activity.Current while the step runs and HttpClient forwards its traceparent.
using var tracing = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is null
    ? null
    : Sdk.CreateTracerProviderBuilder()
        .ConfigureResource(resource => resource.AddService("AspireAppointments.Tests"))
        .AddSource(RaunTelemetry.SourceName)
        .AddHttpClientInstrumentation()
        .AddOtlpExporter()
        .Build();

return await RaunAspire.RunAsync<Projects.AspireAppointments_AppHost>(args, aspire =>
{
    // Declarative: scenarios do not start until these report healthy. Starting and waiting happen
    // inside the run as the "Preflight" node, so both are timed and reported.
    aspire.ConfigureTestApplication(b =>
    {
        b.UseClassifiedCoverage();
        b.AddCodeCoverageProvider();
        b.AddCoverletExtensionProvider();
    });

    aspire.WaitFor("api");
    aspire.StartupTimeout = TimeSpan.FromMinutes(2);

    aspire.Services(services =>
    {
        // IHttpClientFactory pools AND rotates the underlying handler, so each CreateClient is a
        // cheap, isolated wrapper over a shared connection pool. That is what makes a client per
        // identity — or per call — free, instead of something to cache and worry about.
        services.AddHttpClient("api", static (IServiceProvider sp, HttpClient client) =>
            client.BaseAddress = sp.GetRequiredService<DistributedApplication>().GetEndpoint("api"));

        services.AddSingleton<AppointmentsApi>();
    });
});
