# Raun

Scenario tests for .NET. Write `Given` / `When` / `Then` steps in C#. Get a test result for each step.
Raun generates a dependency graph at compile time and runs it on Microsoft.Testing.Platform (MTP).
A failed step skips its dependent steps. Independent branches continue.

## Install and run

Use **.NET SDK 10.0.300 or later**. Create a console project and add Raun:

```bash
dotnet new console -n MyScenarios
cd MyScenarios
dotnet add package Raun.Mtp --prerelease
dotnet add package xunit.v3.assert
```

Delete the template's `Program.cs`; Raun generates the entry point. Add your scenario and step classes.
Keep scenarios in the executable project. Referenced libraries can contain shared steps and domain types.

```bash
dotnet run -- --report-html --results-directory out
```

For `dotnet test`, select MTP in the solution's `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

## Scenarios and steps

A scenario class derives from `Scenarios<TWorld>`. Its properties select step classes.
Use `NoWorld` when you need no shared scenario state.

```csharp
using Raun;

public sealed class BookingScenarios : Scenarios<NoWorld>
{
    public AppointmentGiven Given => Steps<AppointmentGiven>();
    public AppointmentWhen When => Steps<AppointmentWhen>();
    public AppointmentThen Then => Steps<AppointmentThen>();

    [Scenario("customer books an appointment")]
    public async Task Booking()
    {
        // Run setup steps in parallel. Keep their typed results.
        var (patient, slot) = await (Given.PatientExists("Jane"), Given.AvailableSlot());
        // Wait for both results. Explain why this step matters.
        var appointment = await When.CreateAppointment(patient, slot).Note("Reserve the selected slot");
        await Then.AppointmentExists(appointment);
    }
}
```

Steps are public instance methods. Derive from `Given<TWorld>`, `When<TWorld>`, or `Then<TWorld>`.
Use any assertion library. Put retries and polling inside steps.

```csharp
public sealed class AppointmentGiven : Given<NoWorld>
{
    [StepName("Given patient {$return ?? name} exists")]
    [return: Created]
    public Task<Patient> PatientExists(string name)
        => Task.FromResult(new Patient(name));
}
```

`[StepName]` sets the title. `{$return ?? name}` shows the input until the result is available, then links the returned resource.
For the remaining methods, see the [complete step definitions](samples/AppointmentTests/AppointmentDsl.cs).

Use `Steps<T>()` properties inside step classes to group methods, such as `Given.Patients.Exists(...)`.
For a custom phase, derive from `Phase<TWorld>` and add `[PhaseName("Eventually")]`.
Scenario bodies are generator input; do not call them directly. Compiler diagnostics reject unsupported syntax.

## Parallel groups, arguments, and branches

Steps run in source order. Await a tuple, array, or constant LINQ range for parallel work:

```csharp
await (Then.PatientIsRegistered(patient), Then.SlotIsHeld(slot));
var users = await new[] { Given.UserExists("alice"), Given.UserExists("bob") };
var more = await Enumerable.Range(1, 3).Select(i => Given.UserExists($"u{i}")).ToArray();
```

Tuples support 2–8 steps. Groups support `Task` and `Task<T>`, but not `ValueTask`.
A group returns values only when every step returns `Task<T>`.
Arguments can construct objects or read earlier results, such as `Then.AppointmentExists(booked.Appointment)`.
Raun records those dependencies and evaluates arguments when the consuming step starts.

Use `if` with an awaited condition step, or `switch` with an awaited result:

```csharp
switch (await When.TryToBook(patient, slot))
{
    case Booked booked:
        await Then.AppointmentExists(booked.Appointment);
        break;
    case Waitlisted waitlisted:
        await Then.PatientIsOnWaitlist(patient, waitlisted.Position);
        break;
    case Refused refused:
        await Then.PatientWasTold(patient, refused.Reason);
        break;
}
```

Only the selected branch runs. Other branches report `not taken`. Values assigned in both `if` branches merge after the condition.
See the [branch examples](samples/AppointmentTests/BookingOutcomeScenarios.cs).

## Worlds, Setup, and services

Implement `IScenarioWorld<TWorld>.CreateAsync(ScenarioContext)` to create scenario state.
Raun calls it in each scenario's `Setup` node. Steps access that state through `World` and runtime services through `Context`.
A failed Setup skips the scenario's steps. Disposable worlds are released during teardown.

Resolve services from `context.Services` in the factory. Use `context.Scenario` for scenario metadata.
Parallel steps share one world and step-class instance, so keep references stable and services safe for concurrent use.
See the [world example](samples/AppointmentTests/CancellationScenarios.cs).

## Resource keys, labels, and snapshots

A **key** identifies a resource. A **label** supplies readable text. A **snapshot** captures data at a reference.

```csharp
public sealed record Patient(string Name) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Name;
    public static string? LabelFor(Patient patient) => patient.Name;
}
```

`LabelFor` is optional. Register selectors for external types through `Context.Resources` or a DI-supplied `ResourceIdentityResolver`:

```csharp
Context.Resources.Identify<PatientDto>(patient => patient.Id);
Context.Resources.Label<PatientDto>(patient => patient.Name);
```

Label selection uses the registered selector, then `LabelFor`, then `DisplayName:key`.
Null or blank labels use the next option. `[DisplayName("Patient")]` names the type, not an instance.
Raun does not call `ToString()` to choose a label. Labels never change resource identity.

| Format in a title, interpolated log, or note | Text |
| --- | --- |
| `{patient}` | Label |
| `{patient:id}` | Key |
| `{patient:data}` | Inline data preview |

Each reference keeps its resource link and captured JSON. Hover over a reference to inspect or copy the snapshot.
Labels and snapshots are captured at the reference; later changes do not rewrite them.
Use synthetic test data: Raun does not redact reports or artifacts.

## Resource roles and conflicts

Declare each step's resource effects. The report shows them and the links between resources.

```csharp
[StepName("When booking for {patient}")]
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public Task<Appointment> CreateAppointment(Patient patient, Slot slot) { ... }
```

Use `[Created]`, `[Loaded]`, or `[Edited]` on returns; use `[Read]`, `[Edited]`, or `[Deleted]` on parameters.
`References` records retained links. `Consumes` records inputs used to produce the result.
Missing resource roles cause compiler diagnostics.
For effects inside a step, use `await Context.Resources.Create(value)` or `Load`, `Read`, `Edit`, `Delete`, `Reference`, and `Consume`.

Parallel steps must not change the same resource. Raun detects conflicts at compile time where possible, and at run time by key.
A run-time conflict fails the step; it does not wait for a lock.

## Notes, logs, and attachments

Attach explanations to `Task` or `Task<T>` with `.Note("why")` or `.Note($"why {patient}")`.
Notes remain on skipped steps when their inputs are available. Inside a step:

```csharp
Context.Log($"Booked {appointment} for {patient:id}");
Context.GetLogger<AppointmentWhen>().LogInformation("Created {Count} bookings", count);
Context.AddAttachment("response.json", responseJson);
```

Logs belong to the active step and include elapsed time. Text attachments become run artifacts.
Add `new RaunLoggerProvider()` to application logging to capture its logs under the active step.
Called code can use `ScenarioContext.Current`.

## Cleanup and timeouts

Register cleanup when you create a resource. Use the callback's context for cleanup output:

```csharp
Context.OnTeardown(async teardown =>
{
    await Db.DeletePatient(patient.Id);
    teardown.Log("Patient deleted");
});
Context.OnTeardown(Cleanup.Required, () => container.StopAsync());
```

Every scenario has a `Teardown` node. Cleanup runs in reverse dependency order; one error does not stop later callbacks.
`[Teardown(Run.OnSuccess)]` keeps data after failure. `Run.Never` disables optional cleanup.
Required cleanup still runs after failure, cancellation, or timeout.

Set limits in milliseconds: `[Scenario("booking", Timeout = 30000)]` or `[StepName("When booking", TimeoutMs = 5000)]`.
Pass `Context.CancellationToken` to asynchronous work so it can stop.

## Concurrent scenarios and shared capacity

Scenarios run concurrently, up to the processor count by default. Each gets its own DI scope and trace.
Use isolated test data. Start an existing suite with `--max-parallel-scenarios 1`.
Declare shared infrastructure with resource tokens:

```csharp
[SharedResource] public sealed class Database : IContendedResource;
[ExclusiveResource] public sealed class SerialPort : IContendedResource;
[PooledResource(3)] public sealed class Smtp : IContendedResource;

// Put this on a scenario that needs the database to itself:
[Uses<Database>(LockMode.Exclusive)]
```

Put `[Uses<T>]` on steps, scenarios, classes, or assemblies. Shared use permits concurrent holders; a pool limits their number.
Exclusive use waits for all holders to finish. Raun acquires the full set before Setup and releases it after teardown.
The report shows time spent waiting.

## Reports and test selection

`--report-html` writes a self-contained report. Its Tests view shows results, timelines, graphs, logs, resource events, and errors.
Select a resource reference to highlight its uses; press Esc to clear the selection.
The IDE reports each step as a test. Selecting a step runs its dependencies, branch conditions, merge sources, and cleanup, but no later steps.

```bash
dotnet run -- --treenode-filter '/*/*/*/customer books an appointment/*'
dotnet run -- --treenode-filter '/*/*/*/*/*[Phase=When]'
dotnet run -- --max-parallel-scenarios 4 --maximum-failed-tests 1
```

Paths are `/{assembly}/{namespace}/{class}/{scenario}/{step}`.
Remove numbering prefixes from `--list-tests` step names when filtering. Get scenario names from `[Scenario]` or the report.
Escape `%` as `%25`, `/` as `%2F`, and literal filter operators with a backslash.
IDEs use `--filter-uid`. MTP does not accept `--filter` or `--nologo` here.
`--maximum-failed-tests` stops new scenarios; running scenarios finish.

## Preflight, extensions, and coverage

For a custom `Program.cs`, set `<RaunGenerateProgram>false</RaunGenerateProgram>` in the project file.
Build and dispose your service provider there. Pass it to Raun and register MTP extensions:

```csharp
return await RaunTestApplication.RunAsync(args,
    services: services,
    preflight: ctx => StartEnvironment(ctx.CancellationToken),
    configure: builder => builder.AddTrxReportProvider());
```

Preflight runs once and reports as a test node. Failure skips all scenarios.
The TRX example needs `Microsoft.Testing.Extensions.TrxReport` and `using Microsoft.Testing.Extensions;`; enable it with `--report-trx`.
The generated entry point does not register optional extensions.

Register a compatible Microsoft coverage or Coverlet provider for the report's Coverage view.
Use `--report-html --coverage --coverage-output-format xml`, or `--report-html --coverlet --coverlet-output-format json`.
Raun classifies branches as argument checks, error handling, null propagation, logging, or logic.
Add project rules with `builder.UseBranchClassificationMiddleware(() => new ProjectRule())` and `IBranchClassificationMiddleware` from `Raun.Coverage`.
See the [provider setup](samples/AspireAppointments/AspireAppointments.Tests/Program.cs).

## Tracing, simulated time, and Aspire

Subscribe to `RaunTelemetry.SourceName` with OpenTelemetry. Raun emits scenario and step spans, with logs and resource events.
Instrumented HTTP calls link application spans to the active step. Your exporter sends the spans; Raun does not export them.

For report samples, set `simulateTime: true` on `RunAsync` and call `Context.SimulateElapsed(TimeSpan.FromSeconds(1))` in steps.
This advances the simulated clock without a real delay. It has no effect in normal runs.

`Raun.Aspire` starts your AppHost in preflight, waits for named resources, and registers the running application:

```csharp
return await RaunAspire.RunAsync<Projects.MyAppHost>(args, aspire => aspire.WaitFor("api"));
```

See [AspireAppointments](samples/AspireAppointments) for clients, service registration, and trace export.

## Repository

[AppointmentTests](samples/AppointmentTests) contains working feature examples.
See [design notes](docs/superpowers/specs) for decisions and [releasing](docs/RELEASING.md) for package publishing.
Run `dotnet build Raun.slnx` and `dotnet test Raun.slnx` to verify the repository.

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).