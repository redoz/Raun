# Appointments API tests

This sample starts an ASP.NET API with Aspire, then checks it through HTTP.
It uses one project resource and needs no container runtime. Start with
[AppointmentTests](../AppointmentTests/README.md) for the basic authoring examples.

From the repository root, with .NET SDK 10.0.300 or later:

```bash
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests -- --report-html --results-directory "$PWD/out"
```

Open `out/raun-report.html`. Preflight starts the AppHost and waits for the API's health check.

| What to look for | Where to read |
| --- | --- |
| AppHost resources and health checks | [AppHost.cs](AspireAppointments.AppHost/AppHost.cs) |
| Startup, service registration, HTTP clients, and runner extensions | [Test entry point](AspireAppointments.Tests/Program.cs) |
| A world that resolves services | [AppointmentsWorld and step classes](AspireAppointments.Tests/AppointmentsDsl.cs) |
| Admin and patient requests | [Scenarios](AspireAppointments.Tests/Scenarios.cs) and [clients](AspireAppointments.Tests/Actors.cs) |
| Shared and exclusive access | [Schedule](AspireAppointments.Tests/Scenarios.cs): bookings share the schedule; clearing it requires exclusive access |
| Notes and result-based titles | [Scenarios](AspireAppointments.Tests/Scenarios.cs) and [step classes](AspireAppointments.Tests/AppointmentsDsl.cs) |
| Trace export | [Test entry point](AspireAppointments.Tests/Program.cs) and [API](AspireAppointments.Api/Program.cs) |
| Coverage collectors and branch classification | [Test entry point](AspireAppointments.Tests/Program.cs) |

The API uses an in-memory store and an X-Role header to keep the example small.
Use your application's authentication when adapting the tests.

## Coverage and traces

Choose either registered coverage collector:

```bash
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests -- --report-html --coverage --coverage-output-format xml --results-directory "$PWD/out"
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests -- --report-html --coverlet --coverlet-output-format json --results-directory "$PWD/out"
```

Raun.Coverage classifies the recorded branches. The report shows both logical-branch coverage and total branch coverage.
To export traces, set `OTEL_EXPORTER_OTLP_ENDPOINT` to your collector before running the sample.
The test process and API then export spans to that collector. Without the variable, neither enables export.

See [running tests](../../docs/reference/running-tests.md) for runner options and provider setup.
