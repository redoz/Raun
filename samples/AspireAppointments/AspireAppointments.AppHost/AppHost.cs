// The smallest AppHost that still exercises the integration: one project resource, no containers.
// A container resource (Postgres, say) would be more realistic but would make the sample require a
// container runtime to run at all — the Raun wiring is identical either way.

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddDotnetProject("api", "../AspireAppointments.Api/AspireAppointments.Api.csproj")
    // Declared here rather than in a launchSettings.json profile: one fewer file, and the sample's
    // topology is visible in one place. Aspire injects ASPNETCORE_URLS for the allocated port.
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health");

// The testing builder disables the dashboard, and with it the OTLP endpoint Aspire would otherwise
// hand to every resource. Forward the test process's endpoint instead, so the API's spans and Raun's
// step spans reach the same collector and join into one trace per scenario.
if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { } otlpEndpoint)
{
    api.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
}

builder.Build().Run();
