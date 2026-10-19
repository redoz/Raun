# Appointment tests

Start here to see how Raun tests fit together. The clinic runs in memory, so this sample needs no server or database.

From the repository root, with .NET SDK 10.0.300 or later:

```bash
dotnet run --project samples/AppointmentTests -- --report-html --results-directory "$PWD/out"
```

Open `out/raun-report.html`. Expand a scenario to see its steps and resource matrix.
Expand a step for its logs; use attachment buttons to inspect saved text.
The sample supplies elapsed times without waiting, so the report includes useful timing examples.
Scenarios run concurrently. A branch that is not taken appears as skipped.

## Read the examples in order

| Add this to your suite | Read this example |
| --- | --- |
| Steps without shared state | [AppointmentSuite and step bases](AppointmentDsl.cs): use Scenarios, Given, When, and Then without a world type |
| Your first scenario | [Scenarios.Booking](Scenarios.cs) and the [step classes](AppointmentDsl.cs) |
| Checks with different inputs | [AuthorizationScenarios.EndpointAccess](AuthorizationScenarios.cs): four named cases check anonymous, reader, and admin access |
| A world for each scenario | [ClinicWorld.CreateAsync](CancellationScenarios.cs): create a tenant and stub server, then register cleanup |
| Injected services | [ServiceScenarios](ServiceScenarios.cs): two scenarios each resolve their own scoped session |
| Parallel steps | [BookingWithParallelArrange, ImportUsers, ImportUsersViaLinq](Scenarios.cs): tuples, arrays, and a constant LINQ range |
| Conditional steps | [BookingWithPriorityRouting](Scenarios.cs): choose a branch and use its result |
| Different result types | [BookingOutcomeScenarios](BookingOutcomeScenarios.cs): switch on a booking result |
| Groups of related steps | [ClinicGiven and ClinicThen](CancellationScenarios.cs): nested step classes |
| A custom phase | [AppointmentEventually](LifecycleDsl.cs): check that a reminder arrived |
| Cleanup and time limits | [LifecycleScenarios](LifecycleScenarios.cs) and [LifecycleDsl](LifecycleDsl.cs): required cleanup, cleanup on success, scenario and step limits |
| Resource keys and labels | [Patient, Slot, and Appointment](AppointmentDsl.cs): identify objects and give them readable names |
| Resource changes and links | [AppointmentWhen](AppointmentDsl.cs) and [lifecycle steps](LifecycleDsl.cs): create, load, read, edit, delete, reference, and consume |
| IDs and alternate representations | [ResourceTrackingScenarios.ImportedBooking](ResourceTrackingScenarios.cs): treat a row, an object, and its key as the same patient |
| Reserve a slot | [BookingAttributes](BookingAttributes.cs): `[Reserved]` names a Consume action; the slot event and booking lineage use the same shared-access semantics |
| Domain action names | [ResourceActionScenarios](ResourceActionScenarios.cs): custom action attributes, method shorthand, and manual Apply |
| Names carried into later steps | [ResourceActionScenarios](ResourceActionScenarios.cs): a key-only audit reuses the patient name within each scenario |
| Resource setup in code | [Program](Program.cs): typed mappings, a named resource kind, and a label selector |
| Resource tracking inside a step | [ResourceTrackingScenarios](ResourceTrackingScenarios.cs): record effects and links directly through Context.Resources |
| Optional and formatted titles | [ResourceTrackingScenarios.QueueTitles](ResourceTrackingScenarios.cs): title variants, result fallback, member values, alignment, and formats |
| Notes and resource logs | [ResourceTrackingScenarios](ResourceTrackingScenarios.cs): link resource references to the matrix and capture their data |
| Application logs and attachments | [ServiceScenarios](ServiceScenarios.cs) and [Program](Program.cs): capture application ILogger output; [LifecycleDsl](LifecycleDsl.cs): ambient context and a saved reminder |
| Limits on shared infrastructure | [CapacityScenarios](CapacityScenarios.cs): a pool of two stations and exclusive maintenance |
| Runner extensions | [Program](Program.cs): register the TRX report provider |
| Classified coverage | [Program](Program.cs) and [testconfig.json](testconfig.json): collect coverage on every run and limit it to this sample's own code, which fills the report's Coverage view |

These are executable examples. Assertions check the results; they do not just produce a report.
The authorization handler is local. For real HTTP requests, AppHost startup, tracing, shared access,
and coverage of a separate application, continue with [AspireAppointments](../AspireAppointments/README.md).

## Try the runner options

Run one scenario:

```bash
dotnet run --project samples/AppointmentTests -- --treenode-filter '/*/*/*/customer books an appointment/*'
```

Run scenarios one at a time and write HTML and TRX reports:

```bash
dotnet run --project samples/AppointmentTests -- --max-parallel-scenarios 1 --report-html --report-trx --results-directory "$PWD/out"
```

The sample sets time limits but does not deliberately exceed them or leave work running after cancellation.
Those failure paths are checked in the [runtime tests](../../test/Raun.Test) and [host tests](../../test/Raun.Mtp.Test).

For exact rules, see [writing scenarios](../../docs/reference/scenario-authoring.md),
[resources](../../docs/reference/resources.md), and [running tests](../../docs/reference/running-tests.md).
