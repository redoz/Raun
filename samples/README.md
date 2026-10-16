# Raun samples

Start with **GettingStarted**: one booking, ordinary C# steps, resource effects, and assertions.
There is no Program.cs; Raun generates the MTP host. Project references require a few MSBuild
settings that package consumers get from Raun.Mtp automatically.

```sh
dotnet run --project samples/GettingStarted -- --report-html
dotnet run --project samples/AppointmentTests -- --report-html
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests -- --report-html
```

AppointmentTests uses a custom Main to simulate durations and register TRX. AspireAppointments
uses one to start a real AppHost, configure clients and services, and wire coverage and tracing.
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

The registry journey represents a realistic service boundary. Its external row has PatientId and
FullName; its domain patient has Id and Name. Both map to Patient + pat-42. Approval changes
registry state, booking requires that approval, and the final step reads back the stored booking.
The booking API takes only the patient ID. Typed relationship metadata still links it to the same
patient, and earlier evidence supplies Jane's readable name. The report browser checks verify the
identity, both original representation snapshots, the relationships, and the absence of a snapshot
on the key-only reference and audit.

The report checks also verify both optional-title choices, formatted members, and the absence of
booking effects/relationships on waitlisted and refused outcomes. They run against actual sample
output in CI; they do not infer coverage from an attribute merely being present in source.

## Configuration and failure examples

Runtime cancellation, failing timeouts, forced process termination and cleanup failure policies
belong in dedicated runtime tests, rather than intentionally breaking these happy-path suites.
The business cancellation scenarios cancel appointments; they do not cancel the test runner.
Configured deadlines in the lifecycle examples normally do not expire.

Concurrency caps and filtering are useful run settings, not extra domain steps:

```sh
dotnet run --project samples/GettingStarted -- --max-parallel-scenarios 1 --max-parallel-steps 2
dotnet run --project samples/AppointmentTests -- --treenode-filter "/*/*/*/customer books an appointment/*"
dotnet run --project samples/AppointmentTests -- --report-trx
```

Fluent identity mappings, named kinds, explicit label selectors, pool capacity, and alternate
relationship syntax are documented in the [main README](../README.md). The registry uses the
interface-first mapping path; a second mapping configuration for the same example would obscure
that default. Provider overloads and diagnostic errors are covered by dedicated tests. These
samples demonstrate user journeys, not every possible API permutation.
