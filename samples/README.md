# Raun samples

Start with GettingStarted. It has one booking, ordinary C# steps, resource effects, and assertions.
There is no Program.cs, because Raun generates the Microsoft Testing Platform host. A sample that
references Raun by project reference needs a few MSBuild settings. Package consumers get those
settings from Raun.Mtp.

```sh
dotnet run --project samples/GettingStarted -- --report-html
dotnet run --project samples/AppointmentTests -- --report-html
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests -- --report-html
dotnet tool restore
(cd samples/MutationTesting && dotnet stryker -f stryker-config.json -O ../../artifacts/stryker/scenario)
```

AppointmentTests uses a custom Main to simulate durations and register the TRX report provider.
AspireAppointments uses a custom Main to start a real AppHost, configure clients and services, and
set up coverage and tracing.
The projects are also included in `dotnet test Raun.slnx`.

| Capability | Example |
| --- | --- |
| Generated host, simple steps, typed results, assertions | [GettingStarted](GettingStarted/BookingScenarios.cs) |
| Sequential and parallel tuple/array/LINQ groups, conditional merge | [Booking scenarios](AppointmentTests/Scenarios.cs) |
| Conditional results: booked, waitlisted, refused | [Outcome scenarios](AppointmentTests/BookingOutcomeScenarios.cs) |
| Imperative effects for a resource inside a wrapped result | [TryToBook](AppointmentTests/BookingOutcomeDsl.cs): only the successful branch applies the Booked domain action and records its relationships |
| Optional title selection and formatted member paths | [PatientExists, AppointmentExists and ImportShouldContainUsers](AppointmentTests/AppointmentDsl.cs); return-aware creation titles and resource-valued member paths retain links, while the ordinary import count shows scalar formatting |
| Five lifecycle effects, custom actions, typed relationship properties | [Lifecycle](AppointmentTests/LifecycleScenarios.cs) and [booking attributes](AppointmentTests/BookingAttributes.cs) |
| Alternate representations, opaque string keys, remembered labels | [Registry booking](AppointmentTests/RegistryBookingScenarios.cs); [Patient](AppointmentTests/AppointmentDsl.cs) owns the row mapping |
| A per-scenario world, metadata, setup and required cleanup | [Clinic cancellation](AppointmentTests/CancellationScenarios.cs) |
| Nested groups, custom phases, logs, ILogger, text attachments | [Lifecycle steps](AppointmentTests/LifecycleDsl.cs) and [clinic cancellation](AppointmentTests/CancellationScenarios.cs) |
| Real AppHost preflight, DI, HTTP clients, shared/exclusive admission, notes | [Aspire scenarios](AspireAppointments/AspireAppointments.Tests/Scenarios.cs) and [host](AspireAppointments/AspireAppointments.Tests/Program.cs) |
| Coverage leaf and optional trace export | [Aspire host](AspireAppointments/AspireAppointments.Tests/Program.cs); coverage collection/export requires the documented options/environment |
| Mutation testing: one test per scenario under Stryker.NET, a sentinel mutant | [MutationTesting](MutationTesting/README.md) |

The registry journey models a service boundary. Its external row has PatientId and
FullName; its domain patient has Id and Name. Both map to the Patient resource with key pat-42. Approval changes
registry state, booking requires that approval, and the final step reads back the stored booking.
The booking API takes only the patient ID. Typed relationship metadata still links it to the same
patient, and earlier evidence supplies Jane's readable name. The report browser checks verify the
identity, both original representation snapshots, the relationships, and the absence of a snapshot
on the key-only reference and audit.

The report checks also verify both optional-title choices, formatted members, and the absence of
booking effects/relationships on waitlisted and refused outcomes. CI runs them against real sample
output. They do not count an attribute that is present in source as coverage.

## Configuration and failure examples

Dedicated runtime tests cover runtime cancellation, failing timeouts, forced process termination
and cleanup failure policies. These samples are happy-path suites and do not fail on purpose.
The business cancellation scenarios cancel appointments; they do not cancel the test runner.
The deadlines configured in the lifecycle examples do not expire in a normal run.

Concurrency caps and filtering are run settings, not domain steps. These commands limit concurrency,
run one scenario, and write a TRX report:

```sh
dotnet run --project samples/GettingStarted -- --max-parallel-scenarios 1 --max-parallel-steps 2
dotnet run --project samples/AppointmentTests -- --treenode-filter "/*/*/*/customer books an appointment/*"
dotnet run --project samples/AppointmentTests -- --report-trx
```

The [main README](../README.md) documents fluent identity mappings, named kinds, explicit label
selectors, pool capacity, and alternate relationship syntax. The registry uses the interface-first
mapping, which is the default. A second mapping configuration for the same example would hide that
default. Dedicated tests cover provider overloads and diagnostic errors. The samples show user
journeys and do not cover every API combination.

The report keeps crowded resource lanes in a compact activity column. Click a symbol to inspect the full lanes in a scrollable popover. Hover over or focus a heading to show its italic type or method name. To try source navigation, add `--report-source-base-url "https://github.com/redoz/Raun/blob/main/"` to an example command above. The headings then link to the matching source lines.
