# Raun

Raun is an integration testing framework for .NET. Write scenarios as C# methods with
`Given`, `When`, and `Then` steps. Each step gets its own test result. When a step fails,
steps that depend on it are skipped; independent branches can continue.

- A **scenario** describes one behavior you want to test.
- A **step** does setup, performs an action, or checks a result. It is a reusable C# method.
- A **world** holds the infrastructure and state shared by one scenario's steps.
- A **resource** is something steps create, read, change, or delete, identified by a stable key.

## Start with a scenario

```csharp
[Scenario("customer books an appointment")]
public async Task Booking()
{
    var patient = await Given.PatientExists("Jane");
    var slot = await Given.AvailableSlot();

    var appointment = await When.CreateAppointment(patient, slot);

    await Then.AppointmentExists(appointment);
}
```

The snippets on this page are excerpts from runnable samples, rather than a complete project.
See the [scenario class](samples/AppointmentTests/Scenarios.cs) and [step definitions and suite](samples/AppointmentTests/AppointmentDsl.cs).
Run the sample from this repository with **.NET SDK 10.0.300 or later**:

```bash
dotnet run --project samples/AppointmentTests -- --report-html --results-directory "$PWD/out"
```

For your own project:
```bash
dotnet new console -n MyScenarios
cd MyScenarios
dotnet add package Raun.Mtp --prerelease
dotnet add package xunit.v3.assert
```

Delete the template's `Program.cs`; Raun generates the entry point. Add a scenario class
derived from `Scenarios<NoWorld>`, its step classes, and any domain types. `NoWorld` means
the scenario needs no shared state. Run with `dotnet run -- --report-html`.
The [authoring reference](docs/reference/scenario-authoring.md) covers the class wiring;
[running tests](docs/reference/running-tests.md) covers `dotnet test`, selection, and runner options.

## Reuse steps and give them readable titles

Steps live in classes such as `AppointmentGiven : Given<NoWorld>`. A suite exposes them
through properties such as `public AppointmentGiven Given => Steps<AppointmentGiven>();`.
They return ordinary `Task` or `Task<T>` values and can use your preferred assertion library.

`[StepName]` gives each call a title. This [assertion step](samples/AppointmentTests/AuthorizationScenarios.cs)
uses the expected status in its title:

```csharp
[StepName("Then the status is {expected}")]
public Task StatusIs(HttpStatusCode actual, HttpStatusCode expected)
{
    Assert.Equal(expected, actual);
    return Task.CompletedTask;
}
```

Titles can also show member values, optional arguments, and completed results; see the [resource tracking sample](samples/AppointmentTests/ResourceTrackingScenarios.cs).

## Run the same scenario with different inputs

Named `[ScenarioCase]` rows give a parameterized scenario separate runs. The [authorization sample](samples/AppointmentTests/AuthorizationScenarios.cs) checks endpoint access:

```csharp
[Scenario("endpoint access")]
[ScenarioCase("anonymous", "/appointments", EndpointRole.Anonymous, HttpStatusCode.Unauthorized)]
[ScenarioCase("reader cannot administer", "/admin/appointments", EndpointRole.Reader, HttpStatusCode.Forbidden)]
public async Task EndpointAccess(string endpoint, EndpointRole role, HttpStatusCode expected)
{
    var status = await When.Request(endpoint, role);
    await Then.StatusIs(status, expected);
}
```

Each case has its own world, service scope, setup, and teardown. This example uses a local
handler; the [Aspire sample](samples/AspireAppointments/README.md) makes real HTTP calls.

## Add state, services, and cleanup

When steps need a shared client, tenant, or stub server, define a world with
`IScenarioWorld<TWorld>.CreateAsync(ScenarioContext)`. Raun creates it during Setup.
Steps access it through `World`; `Context` provides services, cancellation, logs, and cleanup.

The [cancellation sample](samples/AppointmentTests/CancellationScenarios.cs) registers cleanup immediately:

```csharp
var isolation = await TestIsolation.CreateAsync(seed, context.CancellationToken);
context.OnTeardown(Cleanup.Required, teardown =>
{
    teardown.Log($"released {isolation.Tenant}");
    return isolation.ReleaseAsync();
});
```

Cleanup runs in Teardown. Disposable worlds are released there too. Resolve services from
`context.Services` in the factory; the [service sample](samples/AppointmentTests/ServiceScenarios.cs) shows scoped dependency injection.

## Run independent steps together and follow branches

Steps normally run in source order. Await a tuple to run independent work together:

```csharp
var (patient, slot) = await (
    Given.PatientExists("Jane"),
    Given.AvailableSlot());
```

The next step can use both results. An awaited condition step chooses a branch:

```csharp
Appointment appointment;
if (await Given.PatientIsPriority(patient))
    appointment = await When.CreateUrgentAppointment(patient, slot);
else
    appointment = await When.CreateAppointment(patient, slot);
```

The other arm is reported as skipped. See [conditional scenarios](samples/AppointmentTests/Scenarios.cs)
and [`switch` branches](samples/AppointmentTests/BookingOutcomeScenarios.cs).

## Track what steps touch

A resource's `IResource<T>.KeyFor` supplies its identity. Step attributes describe its use:

```csharp
[StepName("When creating an appointment")]
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public Task<Appointment> CreateAppointment(Patient patient, Slot slot)
```

This [booking step](samples/AppointmentTests/AppointmentDsl.cs) creates an appointment linked to
the patient and slot. `[Read]`, `[Edited]`, and `[Deleted]` describe access to existing resources.
Raun detects conflicting parallel access and links resource uses in the report. The
[tracking sample](samples/AppointmentTests/ResourceTrackingScenarios.cs) shows labels, captured data,
alternate representations, and key-only parameters. See the [resource reference](docs/reference/resources.md).

## Read the report when something fails

`--report-html` writes a self-contained report with step results, timelines, a resource matrix,
and errors. Add explanations with `.Note(...)` and write output from steps:

```csharp
Context.Log($"{endpoint}: {(int)status} {status}");
```

Keep useful response bodies or other text with `Context.AddAttachment`, as in the
[reminder step](samples/AppointmentTests/LifecycleDsl.cs). See
[logging and attachments](docs/reference/scenario-authoring.md#notes-logs-and-attachments) and
[report options](docs/reference/running-tests.md#reports-and-test-selection).

## Bring up real infrastructure and share its capacity

`Raun.Aspire` starts an AppHost once in Preflight and waits for named resources. The
[Aspire sample](samples/AspireAppointments/README.md) tests an API as different users;
its [entry point](samples/AspireAppointments/AspireAppointments.Tests/Program.cs) configures clients.

Scenarios run concurrently by default. The [schedule resource](samples/AspireAppointments/AspireAppointments.Tests/Scenarios.cs)
allows concurrent bookings while clearing requests exclusive access:

```csharp
[SharedResource]
public sealed class Schedule : IContendedResource;

[Uses<Schedule>(LockMode.Exclusive)]
```

Raun waits before Setup and holds capacity through teardown. Pools can limit concurrent
holders. See [shared capacity](docs/reference/running-tests.md#concurrent-scenarios-and-shared-capacity).

## Check code coverage

Add `Raun.Coverage` and a coverage collector to see which branches your tests exercise.
The report shows total branch coverage and coverage of your logic. Argument checks,
error handling, null propagation, and logging are classified separately.

With Microsoft Code Coverage registered, run:

```bash
dotnet run -- --report-html --coverage --coverage-output-format xml
```

See the sample's [collector setup](samples/AspireAppointments/AspireAppointments.Tests/Program.cs)
and [coverage commands](samples/AspireAppointments/README.md#coverage-and-traces). Coverlet is also supported.

Browse the [sample guide](samples/AppointmentTests/README.md) and [design notes](docs/superpowers/specs).
Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
