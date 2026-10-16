# Raun

Raun is an integration testing framework for .NET. Write scenarios as C# methods with
`Given`, `When`, and `Then` steps. Each step gets its own test result. When a step fails,
steps that depend on it are skipped; independent branches can continue.

- A **scenario** describes one behavior you want to test.
- A **step** does setup, performs an action, or checks a result. It is a reusable C# method.
- A **world** holds the infrastructure and state shared by one scenario's steps.
- A **resource** is something steps create, read, change, or delete, identified by a stable key.

## Start with a scenario

Write one scenario for one behavior, such as booking an appointment or rejecting an unauthorized request.

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

Use a shared step when several scenarios perform the same operation or check.
Steps live in classes such as `AppointmentGiven : Given<NoWorld>`. A suite exposes them
through properties such as `public AppointmentGiven Given => Steps<AppointmentGiven>();`.
They return ordinary `Task` or `Task<T>` values and can use your preferred assertion library.

Use `[StepName]` to explain a call in the report, including the values that matter. This [assertion step](samples/AppointmentTests/AuthorizationScenarios.cs)
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

Use `[ScenarioCase]` when the same behavior needs checking with different roles, endpoints, or values.
Each named row runs separately. The [authorization sample](samples/AppointmentTests/AuthorizationScenarios.cs) checks endpoint access:

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

Use a world when steps share state that belongs to one scenario, such as a tenant or stub server. Define it with
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

Register cleanup as soon as you create something that must be released. Use `Cleanup.Required`
when leaving it behind would affect later tests. Set step or scenario time limits for work that may hang.
Cleanup runs in Teardown; disposable worlds are released there too.

Use dependency injection for application services and clients you already register. Resolve them from
`context.Services` in the world factory, or inject them into step classes as in the [service sample](samples/AppointmentTests/ServiceScenarios.cs).

## Run independent steps together and follow branches

Use parallel groups when operations do not depend on each other, such as creating a patient and a slot.
Steps otherwise run in source order. Await a tuple to run the group together:

```csharp
var (patient, slot) = await (
    Given.PatientExists("Jane"),
    Given.AvailableSlot());
```

Use a branch when later steps depend on an earlier result. An awaited condition step chooses the arm:

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

Use resources when you need to follow the same patient, booking, or other entity across steps.
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
alternate representations, and key-only parameters. Map alternate representations when an API response
and database row describe the same entity; use a key-only parameter when a step receives just its ID.
See the [resource reference](docs/reference/resources.md).

## Read the report when something fails

Use the HTML report to inspect a failure or share a run. It includes step results, timings, a resource matrix,
and errors. Add `.Note(...)` to explain why a step matters. Use logs for values and events observed during it:

```csharp
Context.Log($"{endpoint}: {(int)status} {status}");
```

Use `Context.AddAttachment` for a response body or other text you want to inspect in full, as in the
[reminder step](samples/AppointmentTests/LifecycleDsl.cs). See
[logging and attachments](docs/reference/scenario-authoring.md#notes-logs-and-attachments) and
[report options](docs/reference/running-tests.md#reports-and-test-selection).

## Bring up real infrastructure and share its capacity

Use `Raun.Aspire` when your tests need an Aspire application running. It starts the AppHost once
in Preflight and waits for named resources. The
[Aspire sample](samples/AspireAppointments/README.md) tests an API as different users;
its [entry point](samples/AspireAppointments/AspireAppointments.Tests/Program.cs) configures clients.

Use capacity limits when concurrent scenarios could interfere with shared infrastructure.
The [schedule resource](samples/AspireAppointments/AspireAppointments.Tests/Scenarios.cs)
allows concurrent bookings while clearing requests exclusive access:

```csharp
[SharedResource]
public sealed class Schedule : IContendedResource;

[Uses<Schedule>(LockMode.Exclusive)]
```

Use shared access when scenarios can safely use the service together, exclusive access when a scenario
needs it to itself, and a pool when only a fixed number may use it at once. Raun waits before Setup
and holds capacity through teardown. See [shared capacity](docs/reference/running-tests.md#concurrent-scenarios-and-shared-capacity).

## Check code coverage

Use code coverage to find paths your tests do not reach, then add scenarios for the behavior that matters.
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
