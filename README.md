# Raun

Scenario tests for .NET. Write `Given` / `When` / `Then` steps in C#. Get a test result for each step.
Raun generates a dependency graph at compile time and runs it on Microsoft.Testing.Platform (MTP).
A failed step skips its dependent steps. Independent branches continue.
Titles can use optional arguments, member paths, and completed return values. Resource objects and typed
string keys share tracking and report links; labels, snapshots, notes, and logs explain what happened.

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

For a complete runnable example with no `Program.cs`, see [GettingStarted](samples/GettingStarted).

For `dotnet test`, select MTP in the solution's `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

## Scenarios and steps

A scenario class derives from `Scenarios<TWorld>`. Its properties select step classes.
Use `NoWorld` when you need no shared scenario state.
Put the scenario, its step classes, and the domain models below in one `.cs` file to run this example.

```csharp
using System.Globalization;
using Raun;
using Xunit;

public sealed class BookingScenarios : Scenarios<NoWorld>
{
    public AppointmentGiven Given => Steps<AppointmentGiven>();
    public AppointmentWhen When => Steps<AppointmentWhen>();
    public AppointmentThen Then => Steps<AppointmentThen>();

    [Scenario("customer books an appointment")]
    public async Task Booking()
    {
        // Run setup steps in parallel. Keep their typed results.
        var (patient, slot) = await (Given.PatientExists("Jane", city: "Oslo"), Given.AvailableSlot());
        // Wait for both results. Explain why this step matters.
        var appointment = await When.CreateAppointment(patient, slot).Note($"Reserve {slot} for {patient:id}");
        await Then.AppointmentExists(appointment);
        // An ID parameter still refers to the same resource as the full object.
        await Then.AppointmentKeyMatches(Appointment.KeyFor(appointment).Value, appointment);
    }
}
```

Steps are public instance methods. Derive from `Given<TWorld>`, `When<TWorld>`, or `Then<TWorld>`.
Use any assertion library. Put retries and polling inside steps.

```csharp
public sealed class AppointmentGiven : Given<NoWorld>
{
    [StepName("Given patient {$return ?? name} exists")]
    [StepName("Given patient {$return ?? name} exists in {city}")]
    [return: Created]
    public Task<Patient> PatientExists(string name, string? city = null)
        => Task.FromResult(new Patient(Guid.NewGuid().ToString("N"), name, city));

    [StepName("Given an available slot exists")]
    [return: Created]
    public Task<Slot> AvailableSlot() => Task.FromResult(new Slot(1));
}

public sealed class AppointmentWhen : When<NoWorld>
{
    [StepName("When booking {$return ?? patient} in slot {slot.Id:D2}")]
    [return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
    public Task<Appointment> CreateAppointment(Patient patient, Slot slot)
    {
        var appointment = new Appointment(patient, slot);
        Context.Log($"Created {appointment} for {patient:id}");
        return Task.FromResult(appointment);
    }
}

public sealed class AppointmentThen : Then<NoWorld>
{
    [StepName("Then {appointment} is booked for {appointment.Patient.Name}")]
    public Task AppointmentExists([Read] Appointment appointment)
    {
        Assert.Equal("Jane", appointment.Patient.Name);
        return Task.CompletedTask;
    }

    [StepName("Then key {appointmentId:id} matches {appointment.Patient.Name}'s appointment")]
    public Task AppointmentKeyMatches(
        [Read, ResourceKey<Appointment>] string appointmentId, [Read] Appointment appointment)
    {
        Assert.Equal(Appointment.KeyFor(appointment).Value, appointmentId);
        return Task.CompletedTask;
    }
}
```

The example uses named optional arguments, two title variants, return-aware titles, formatted member paths,
resource lineage, typed notes and logs, and both object and string-key parameters. The domain models are
shown [below](#resource-keys-labels-and-snapshots). For a larger runnable suite, see
[AppointmentTests](samples/AppointmentTests).

`[StepName]` sets the title. `{$return ?? name}` shows the input until a non-null result is available,
then shows and links that result. A failed or skipped step, or a null result, keeps the available fallback.
`{$return}` without a fallback shows `…` until completion. Input references keep their captured data.

Parameter placeholders can read public or internal instance properties and fields, such as
`{appointment.Patient.Name}`. Member paths are null-safe in titles. `IFormattable` values accept formats
such as `{slot.Id:D2}` or `{booking.When:yyyy-MM-dd}`, using invariant culture.

For optional parameters, declare multiple `[StepName]` titles. At each call, Raun chooses the title
whose placeholder parameters are explicitly supplied and which uses the most distinct parameters.
Named arguments count; omitted defaults do not. Ties use declaration order. Member paths count
against their root parameter, and a return placeholder's fallback must also be supplied.
If no declared title matches, `RAUN023` reports an error at the call. Steps without `[StepName]`
use the method name without an error.

```csharp
[StepName("create {name}")]
[StepName("create {name} at {time}")]
public Task Create(string name, string? time = null) => Task.CompletedTask;
```

In the example, `PatientExists("Jane")` selects the shorter title;
`PatientExists("Jane", city: "Oslo")` selects the title that also mentions the city.
Explicitly supplied null values count as supplied arguments for title selection.

See the [sample step definitions](samples/AppointmentTests/AppointmentDsl.cs) for more operations.

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
public sealed record Patient(string Id, string Name, string? City = null) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;
}

public sealed record Slot(int Id) : IResource<Slot>
{
    public static ResourceKey KeyFor(Slot slot) => slot.Id.ToString(CultureInfo.InvariantCulture);
}

public sealed record Appointment(Patient Patient, Slot Slot) : IResource<Appointment>
{
    public static ResourceKey KeyFor(Appointment appointment) => $"{appointment.Patient.Id}@{appointment.Slot.Id}";
    public static string? LabelFor(Appointment appointment) => $"{appointment.Patient.Name} / slot {appointment.Slot.Id}";
}
```

Keys contain strings (`ResourceKey` wraps a string); the patient example keeps its stable ID separate from its display name.

`LabelFor` is optional. Labels never change resource identity. Explicit `Label<T>` selectors take
priority, followed by an alternate's label contract, the source type's own `LabelFor`, and finally
`DisplayName:key`. Null or blank labels use the next option. `[DisplayName("Patient")]` names the
resource kind. Raun does not call `ToString()` to choose a label.

| Format in a title, interpolated log, or note | Text |
| --- | --- |
| `{patient}` | Label |
| `{patient:id}` | Key |
| `{patient:data}` | Inline data preview |

Object references keep their resource link and captured JSON when serialization succeeds.
Key-only references keep the identity and link, with no object snapshot. Hover over an object reference
to inspect or copy its snapshot.
Labels and snapshots are captured at the reference; later changes do not rewrite them.
Use synthetic test data: Raun does not redact reports or artifacts.

## Alternate resource representations

A domain object, EF row, and API response can represent the same patient. Declare their keys on the
resource owner; the external types need no changes, mapping attributes, or startup configuration:

```csharp
public sealed record Patient(string Id, string Name) :
    IResource<Patient>,
    IResourceAlternate<PatientRow>,
    IResourceAlternate<PatientResponse>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;

    static ResourceKey IResourceAlternate<PatientRow>.KeyFor(PatientRow row) => row.Id;
    static string? IResourceAlternate<PatientRow>.LabelFor(PatientRow row) => row.Name;
    static ResourceKey IResourceAlternate<PatientResponse>.KeyFor(PatientResponse response) => response.PatientId;
    static string? IResourceAlternate<PatientResponse>.LabelFor(PatientResponse response) => response.DisplayName;
}
```

The [registry booking scenario](samples/AppointmentTests/RegistryBookingScenarios.cs) demonstrates
this end to end: import a registry row, approve its domain patient, then book and audit using only
`pat-42`. The report keeps one patient identity, remembers the name, and keeps key-only evidence
free of borrowed snapshots.

All three resolve to **Patient + key**. Their resource roles share conflict detection, lineage, and
report links. `[Read, ResourceKey<Patient>] string patientId` and `Context.Resources.Load<PatientRow>(key)`
refer to that same identity. Keys returned by each extractor must agree; key-only references already
contain the canonical key and cannot transform it without an object. Distinct resource kinds can
use the same key without colliding.

If there is no domain object, use an empty owner with only alternate contracts:

```csharp
public sealed class PatientResource : IResourceAlternate<PatientRow>
{
    public static ResourceKey KeyFor(PatientRow row) => row.Id;
    public static string? LabelFor(PatientRow row) => row.Name; // optional
}
```

The owner is never instantiated and needs no `KeyFor(PatientResource)`. Its key-only parameters use
`ResourceKey<PatientResource>`. A type implementing only the non-generic `IResource` marker can also
name a key-only kind.

Raun's generator discovers closed, accessible contracts, including owners in referenced libraries
built with the Raun generator. A mapping library can contain no scenarios, and its owner can be
internal. Conflicting owners for the same representation are compile errors. Labels use the declared
source representation; snapshots serialize the original runtime object. A row's label method never
receives a domain object, and later response data never rewrites earlier title or log snapshots.

For a custom host, fluent mappings provide the configuration escape hatch:

```csharp
var identities = new ResourceIdentityResolver();
identities.Identify<Patient>()
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(response => response.PatientId);
identities.Label<PatientRow>(row => row.Name);

// Or a named kind without a CLR owner:
// identities.Identify("patient").With<Patient>().With<PatientRow>(row => row.Id);

await using var services = new ServiceCollection().AddSingleton(identities).BuildServiceProvider();
return await RaunTestApplication.RunAsync(args, services: services);
```

Put this in your `Program.cs` with `<RaunGenerateProgram>false</RaunGenerateProgram>` and
`using Microsoft.Extensions.DependencyInjection;`. In an Aspire host, register the configured resolver
inside `aspire.Services(services => services.AddSingleton(identities))` in the `RaunAspire.RunAsync`
configuration callback. The generated host needs neither change.

`With<T>()` reuses `IResource<T>.KeyFor`; `With<T>(selector)` supplies an explicit mapped extractor.
Matching generated defaults preserve explicit fluent extractors and label selectors. The older
`Identify<T>(selector)` registers an unmapped selector below the type's own `IResource<T>.KeyFor`.
It remains available through `Context.Resources`, alongside `Label<T>`.

Configure groups and mapped extractors before the first successful identity resolution; they then
freeze so stored claims and references stay consistent. Reapplying identical defaults is harmless.
Typed groups merge, named kinds win over typed kinds, and distinct named groups cannot merge.
Named kinds use ordinal, case-sensitive names. `ResourceIdentity.Kind` covers both forms;
its legacy `Type` property is null for named kinds. Rebuild consumers when upgrading to this API.

## Resource roles and conflicts

Declare each step's resource effects. The report shows them and the links between resources.
Resource keys dispatch through `IResource<T>.KeyFor`, including explicit static interface implementations,
so object and key-only references share the same identity.

```csharp
[StepName("When booking for {patient}")]
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public Task<Appointment> CreateAppointment(Patient patient, Slot slot) { ... }
```

Use `[Created]`, `[Loaded]`, `[Read]`, `[Edited]`, or `[Deleted]` on parameters, return values, or methods.
A method attribute describes its return value. An explicit return attribute replaces it, including lineage.
`References` records retained links. `Consumes` records inputs used to produce the result.
Missing resource roles cause compiler diagnostics.
For effects inside a step, use `await Context.Resources.Create(value)` or `Load`, `Read`, `Edit`, `Delete`, `Reference`, and `Consume`.

Parallel steps must not change the same resource. Raun detects conflicts at compile time where possible, and at run time by key.
A run-time conflict fails the step; it does not wait for a lock.

When a step takes a resource's key instead of the object, annotate the parameter with
`[ResourceKey<TResource>]`. Combine it with the same access role:

```csharp
[StepName("delete {patientId}")]
public Task Delete([Deleted, ResourceKey<Patient>] string patientId)
    => DeletePatientById(patientId);
```

The string is the key of `Patient`, so its effects, conflicts, lineage, and title links refer to the
same resource as a `Patient` instance with that key. Key-only references reuse an observed resource label,
or display `Patient:key` when no label is available, and carry no object snapshot. `ResourceKey` parameters are also supported. The annotation requires an
access role (or a producer's `References`/`Consumes` declaration); other parameter types report `RAUN024`.
The step method still receives its original string or `ResourceKey`; Raun changes only the tracking and title reference.
Nullable string parameters work when their supplied key is non-null. A supplied null key throws
`ArgumentNullException` when its claim is evaluated; no claim is recorded for that invalid key.
For a key-only reference in a manual log or note, format `new ResourceIdentity(typeof(Patient), patientId)`.

### Reusable resource actions

Derive a sealed attribute from one of the five resource roles to name a domain action once:

```csharp
[DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;

public sealed class AuditedAttribute : ReadAttribute; // Displays "Audited".

[StepName("approve {patientId}")]
public Task Approve([Approved, ResourceKey<Patient>] string patientId) { ... }

[Audited] // Method shorthand: records the returned resource after the call.
public Task<Patient> Audit(Patient patient) { ... }

await Context.Resources.Apply(ResourceAction.For<ApprovedAttribute>(), patient);
```

Use `System.ComponentModel.DisplayName` for a custom label. Otherwise Raun removes the final
`Attribute` suffix from the concrete attribute's name. The nearest inherited `DisplayName` wins;
a blank name is an error. Raun reads metadata without constructing the attribute.

An action keeps its base role's behavior: Created, Edited, and Deleted are exclusive;
Loaded and Read are shared. Input effects are recorded before the call; return effects after it.
Created, Loaded, and Edited also support inherited `References` and `Consumes` declarations.
Distinct custom attribute types retain separate report entries, even when their labels match.
The step code performs the operation; the attribute records its effect.

### Domain attributes and relationship bindings

A domain attribute can describe its effect and the roles its resources play:

```csharp
[ResourceActionDefinition(ResourceActionKind.Create)]
public sealed class BookedAttribute : ResourceAttribute
{
    [Relationship<Patient>(ResourceRelation.Reference)]
    public string Patient { get; set; } = "";

    [Relationship<Slot>(ResourceRelation.Consume)]
    public string Slot { get; set; } = "";
}

[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]
public Task<Appointment> Book(Patient patient, Slot slot2) { ... }
```

The property names belong to the domain attribute. Their supplied values name this step's parameters;
`Slot` can bind `slot`, `slot2`, or another parameter name. Raun reads this metadata without running
attribute code. Property initializers and constructor assignments do not supply missing bindings.

`Relationship<Patient>` expects the **canonical Patient identity**, so mapped EF rows, DTOs, domain
objects, and `[ResourceKey<T>]` parameters in that group also work. CLR inheritance is unnecessary.
Canonical group checks that depend on host mappings run before that relationship is recorded.

Marked properties accept a string or string array and require an explicit nonempty binding by default.
Use `Required = false` for optional properties; supplied null or blank targets remain errors.
The non-generic `[Relationship(ResourceRelation.Reference)]` permits targets of any resource kind.
Every action exposes its immutable `Relationships` property schema; bindings are resolved per usage.

Existing `[Created(References = [...], Consumes = [...])]` syntax remains supported. Those optional
array properties use the same metadata and validation. Separate declarations also work:

```csharp
[return: Created]
[return: RelatesTo(ResourceRelation.Reference, nameof(patient))]
[return: RelatesTo(ResourceRelation.Consume, nameof(slot2))]
public Task<Appointment> Book(Patient patient, Slot slot2) { ... }
```

A relationship source needs Create, Load, or Edit. Both relationship kinds use shared target access;
Consume does not delete its target. Duplicate, conflicting, missing, or self-targeting declarations
are errors. A return action replaces a method action and all its property bindings. Explicit return
`RelatesTo` declarations replace the method's standalone set; otherwise that set still applies.

The appointment sample uses `Registered`, `Held`, `Booked`, `Rescheduled`, and `Cancelled` in its real
steps. Both regular and urgent booking use `Booked`, including the `slot2` binding above. Imperative
`Apply` records an action; explicit `Reference`/`Consume` calls describe imperative relationships.

### Names across steps

A label from `Label<T>`, an alternate representation's `LabelFor`, or `IResource<T>.LabelFor`
is remembered for that canonical resource identity during the scenario. Later key-only references
reuse it in titles, notes, logs, and resource effects. A blank label cannot erase a good name.
`:id` still shows the key; key-only references never borrow a previous object's snapshot.

A step can see its own earlier observations and its graph predecessors. A causally newer name wins.
When independent predecessor branches meet, the later node in scenario order wins, regardless of
completion order. Unrelated parallel steps and separate scenario runs do not share observations.
Cleanup sees the drained scenario's names. Captured titles, logs, and snapshots stay frozen.

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
A timeout requests cancellation and allows **60 seconds of grace** for the operation to stop. During grace,
Raun retains its services and resource ownership and waits before cleanup. Work that stops during grace
still reports the original timeout failure. Host cancellation uses the same grace period, with cooperative
cancellation reported as skipped.

Each selected cleanup also has its own **60-second deadline**, independent of the scenario timeout.
Its `ScenarioContext.CancellationToken` is fresh for that callback. When the deadline expires, Raun
requests cancellation and applies the same grace period. Cleanup that stops during grace records a
callback-specific timeout failure; later callbacks still receive fresh tokens and run in the existing
reverse dependency/registration order. Scope disposal and resource release wait until cleanup stops.
Override the positive callback deadline with `--cleanup-timeout-ms 60000`, or pass `cleanupTimeout`
to `ScenarioScheduler` / `RunLoop`. A callback that survives grace terminates the host before later
cleanup can run. These are per-callback limits, not one shared budget for the whole teardown.

Override grace per run with `--cancellation-grace-ms 60000` (`0` allows no grace), or pass
`cancellationGracePeriod` to `ScenarioScheduler` / `RunLoop` when hosting the runtime directly.
If work or its cancellation callbacks remain alive after grace, Raun **terminates the entire test-host
process**, including other scenarios. This bypasses managed shutdown handlers: required cleanup may not
run, external resources may remain, and HTML/TRX reports or buffered logs may be incomplete. Grace is
measured in real time, including when step timing is simulated.
Pass `Context.CancellationToken` to asynchronous work so it can stop.

## Concurrent scenarios and shared capacity

Scenarios run concurrently, up to the processor count by default. Each gets its own DI scope and trace.
Scenario scopes are disposed asynchronously after teardown, including async-only scoped services.
Resource admission waits for disposal to finish. Disposal failures surface as framework errors after
already-running scenarios drain; they do not change an already-reported teardown result.
Use isolated test data. Start an existing suite with `--max-parallel-scenarios 1`.
Clamp independent steps with `--max-parallel-steps 8`. This limit applies **per scenario**, not to the
whole run; four active scenarios with eight steps each can run up to 32 steps concurrently.
`0` (default) leaves steps unbounded; `1` runs one step at a time within each scenario. Dependencies
and resource-conflict rules still apply. Work started inside a step is outside this scheduler limit.
Set the code default with `RaunTestApplication.RunAsync(args, maxParallelSteps: 8)`,
`RunLoop(..., maxParallelSteps: 8)`, or `AspireRunOptions.MaxParallelSteps`. The CLI value overrides it.
Rebuild previously compiled hosts when upgrading to this beta; the bootstrap/runtime APIs gained a parameter.
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

Coverlet or Microsoft Code Coverage records which code ran.
Raun.Coverage maps those execution hits to source branches and classifies their purpose.
The report shows coverage for logical branches and for all branches.

Classified coverage is optional. Add a `Raun.Coverage` package reference alongside a compatible collector.
`Raun.Mtp` and `Raun.Aspire` do not depend on coverage or its Roslyn/MSBuild analysis assets.
Generated entry points enable the integration when the leaf package is referenced. For a custom `Main`,
register `builder.UseClassifiedCoverage()` in `RaunTestApplication.RunAsync`'s `configure` callback
(or Aspire's `ConfigureTestApplication`), using namespace `Raun.Mtp`.
Middleware registration also enables the integration. The middleware API retains its source namespace
but now ships in `Raun.Coverage`; rebuild consumers when upgrading this beta.

Register a compatible collector for the report's Coverage view.
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

[GettingStarted](samples/GettingStarted) is the smallest runnable example and uses the generated host.
[AppointmentTests](samples/AppointmentTests) demonstrates the larger authoring and report surface.
See the [sample guide](samples/README.md) for the feature map, run commands, and deliberate limits.
See [design notes](docs/superpowers/specs) for decisions and [releasing](docs/RELEASING.md) for package publishing.
Run `dotnet build Raun.slnx` and `dotnet test Raun.slnx` to verify the repository.

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).