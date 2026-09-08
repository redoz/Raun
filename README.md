# Raun

> Scenario / integration tests for .NET — Given/When/Then steps, each reported as its own test, wired into a fork/join dependency graph.

Raun lets you write integration-style scenario tests as readable `Given` / `When` / `Then` C# and have each business step show up as its own test: sequential by default, explicitly parallel where you ask for it, with typed state flowing between steps and dependent steps auto-skipped after a failure. *Raun* is Old Norse for a trial: proof by experience.

```csharp
using Raun;

[Scenario("customer books an appointment")]
public static async Task Booking()
{
    var patient = await Given.PatientExists("Jane");
    var slot = await Given.AvailableSlot();

    var appointment = await When.CreateAppointment(patient, slot);

    await Then.AppointmentExists(appointment);
}
```

A Roslyn source generator lowers the scenario method into a manifest + executor that the runtime runs as a step graph on Microsoft.Testing.Platform.

## Install

Packages are published to GitHub Packages for now: `Raun.Mtp` (the test framework, with the source
generator inside), `Raun` (the core it depends on), and `Raun.Aspire`. Every push to `main` publishes
a `0.x.y-preview.0.N` build; tags publish real versions. GitHub Packages requires authentication even
for public packages, so a consumer needs a personal access token with `read:packages`:

```xml
<!-- nuget.config next to your solution -->
<configuration>
  <packageSources>
    <add key="raun" value="https://nuget.pkg.github.com/redoz/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <raun>
      <add key="Username" value="GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
    </raun>
  </packageSourceCredentials>
</configuration>
```

```bash
dotnet new console -n MyScenarios       # a test project is an executable
cd MyScenarios
dotnet add package Raun.Mtp --prerelease
dotnet add package xunit.v3.assert      # or any assertion library you like
```

Raun generates the `Main` that boots Microsoft.Testing.Platform. To write your own entry point (the
Aspire sample does, to build an AppHost first), set `<RaunGenerateProgram>false</RaunGenerateProgram>`
and call `RaunTestApplication.RunAsync(args)` yourself. Baseline: the .NET 10 SDK; the generator ships
for Roslyn 5.3 and newer.

## How it works

1. You define a domain DSL as C# 14 static extension members on `Given` / `When` / `Then` (or any
   marker type implementing `IPhase`), each annotated with `[StepName("...")]`. These are real
   methods returning ordinary `Task<T>` — each `await` in the scenario unwraps to `T`.
2. You write `[Scenario]` methods using that DSL. The body is **source for the generator** — it is
   never executed directly.
3. The generator lowers each body into a dependency graph (`ScenarioDefinition`): one node per
   step, with **source-order + dataflow** edges, and tuple/array forms lowered to parallel
   sibling groups. An analyzer (`RAUN000`–`RAUN015`) rejects anything outside the supported subset
   and catches authoring mistakes at compile time.
4. At run time, the MTP test framework discovers each `[Scenario]`, runs the graph through a DAG
   scheduler, and reports **every step as its own test** — passed, failed, skipped, or not taken.

### Parallelism is explicit

```csharp
// sequential by default — runs in written order
await Given.DatabaseIsClean();

// fork/join with an awaited tuple — both run in parallel, the next step waits for both
var (patient, slot) = await (Given.PatientExists("Jane"), Given.AvailableSlot());

// homogeneous bulk work — explicit array or a constant LINQ .ToArray()
var users = await new[] { Given.UserExists("alice"), Given.UserExists("bob") };
var more  = await Enumerable.Range(1, 10).Select(i => Given.UserExists($"u{i}")).ToArray();
```

When a step fails, its transitive dependents are skipped (`dependency failed: <op>`) while
independent ready branches keep running.

## Run it

```bash
dotnet test                                                   # whole solution
dotnet run --project MyScenarios -- --report-html             # plus a self-contained HTML report
dotnet run --project MyScenarios -- --report-html --results-directory out
```

The HTML report shows every step on a timeline (a Gantt of the actual concurrency), its log with a
timer from scenario start, its resource effects, and a resource lane across the scenario. In an IDE's
test explorer each step is a test node; selecting one step runs everything up to and including it —
its dependencies, merge sources, guard conditions, and teardown — and nothing after it.

### Selecting what to run

Raun registers the platform's own tree filter, so the syntax is the one every Microsoft.Testing.Platform
framework uses. A step is a test node in Raun, so paths have five segments:

```
/{assembly}/{namespace}/{class}/{scenario}/{step}
```

```bash
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/customer books an appointment/*'  # one scenario
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/*/*reminder*'                      # steps by name
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/*/*[Phase=When]'                   # by phase
```

A segment is matched literally unless it contains a wildcard, so copy a name straight out of
`--list-tests` — spaces and all — rather than percent-encoding it. Selecting a step runs everything
it needs — its dependencies, merge sources, guard conditions, and teardown — and nothing after it,
exactly as selecting one step in an IDE does. `--filter-uid` takes node uids and is what an IDE sends
when you run a single test.

There is no `--filter`: that option belongs to the VSTest bridge that xUnit and MSTest use for
VSTest compatibility, not to the platform itself.

`--maximum-failed-tests <n>` stops Raun admitting new scenarios once failures cross the threshold;
scenarios already running finish and report, so the final failure count can exceed `<n>`.

### Scenarios run in parallel

Scenarios are independent by contract: each gets its own DI scope, context, and trace, and you own
the isolation of the data it touches — unique patients per scenario, not a shared `"Jane"`. The same
goes for anything registered `AddSingleton` and for process-wide state: several scenarios now touch
it at once. Raun runs them concurrently, up to the processor count by default. Steps inside a
scenario still follow the graph; the degree counts scenarios.

```bash
dotnet run --project MyScenarios -- --max-parallel-scenarios 1   # one scenario at a time
dotnet run --project MyScenarios -- --max-parallel-scenarios 4
```

The code default is the `maxParallelScenarios` argument of `RaunTestApplication.RunAsync` (or
`MaxParallelScenarios` on the Aspire options); the command line overrides it per run.

When scenarios genuinely contend for something — one database a few scenarios need to themselves,
three SMTP servers, a serial port — declare it with a token type and `[Uses<T>]`:

```csharp
[SharedResource]     public sealed class Database : IContendedResource;   // any number; Exclusive serializes
[ExclusiveResource]  public sealed class SerialPort : IContendedResource; // one at a time
[PooledResource(3)]  public sealed class Smtp : IContendedResource;       // up to three

[Uses<Database>]                        // shared use; put it on a step, a scenario, a class, or the assembly
[Uses<Database>(LockMode.Exclusive)]    // waits for every holder to finish, then keeps them out
[Scenario("the schema migrates cleanly")]
public static async Task SchemaMigrates() { … }
```

| | Shared use (default) | Exclusive use |
|---|---|---|
| `[ExclusiveResource]` | the one slot | the one slot |
| `[SharedResource]` | free | waits for zero holders, blocks new ones |
| `[PooledResource(N)]` | one of N | all N |

A scenario's whole set is acquired before it starts and released after its teardown, so nothing ever
waits while holding: no deadlocks. Time spent waiting shows on the scenario's span
(`raun.scenario.waited_ms`) and in the HTML report's scenario header. The token is a name, not a
service — register it in DI yourself if it is also one.

## Supported scenario subset

- `[Scenario]` methods are `async Task` / `async ValueTask`.
- Steps are awaited `Given`/`When`/`Then` calls, awaited tuples of them (arity 2–8), awaited
  `new[] { ... }` arrays, or a constant `Enumerable.Range(a, b).Select(...).ToArray()`.
- DSL methods return `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` and may take an optional trailing
  `ScenarioContext` parameter.
- `if`/`else` shapes the graph when the condition is an awaited `Given`/`When`/`Then` call whose
  result is usable as a C# condition (`bool`, an implicit conversion to `bool`, or `operator true`).
  The condition is an ordinary step — discovered, timed, and reported like any other. Exactly one arm
  runs; steps in the other are reported **not taken** (skipped with the reason), never green. A local
  assigned in both arms is merged automatically, and the statement after the `if` waits for the arm
  to finish before it runs.
- Loops (`for`/`foreach`/`while`/`do`), `switch`, `try`/`catch`, and `goto` are rejected with a
  diagnostic: put the loop, retry, or polling **inside a step**. A step is an ordinary
  `async Task<T>` method, so it can loop, retry, or poll internally; what a step cannot do is stop a
  later step from running, which is why `if`/`else` is the one construct the graph models.

### Conditionals

```csharp
var patient = await Given.PatientExists("Alice");
var slot = await Given.AvailableSlot();

Appointment appointment;
if (await Given.PatientIsPriority(patient))
    appointment = await When.CreateUrgentAppointment(patient, slot);
else
    appointment = await When.CreateAppointment(patient, slot);

await Then.AppointmentExists(appointment);
```

### Resources

A domain type becomes a resource by naming its identity; each step then declares what it does to the
resources it touches. There is no default — a resource-typed parameter or return without a role is a
compile error (`RAUN009`).

```csharp
public sealed record Patient(string Name) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient p) => p.Name;
}

[StepName("Given patient {name} exists")]
[return: Created]
public static Task<Patient> PatientExists(string name) { ... }

[StepName("When creating an appointment")]
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public static Task<Appointment> CreateAppointment(Patient patient, Slot slot) { ... }

[StepName("When cancelling the appointment")]
public static Task Cancel([Deleted] Appointment appointment) { ... }
```

Roles are `[Created]`, `[Loaded]`, `[Edited]` on a return and `[Read]`, `[Edited]`, `[Deleted]` on a
parameter; `References`/`Consumes` name the inputs a produced resource is built from, which the
report draws as lineage. Every effect appears in the step's log (`[resource] Create Patient:Jane`) and
in the report's resource lane.

Roles also catch a real class of bug. Two parallel steps that both pass the same local to a mutating
role are rejected at compile time (`RAUN013`), and at run time two steps that nothing orders and that
touch the same identity with at least one mutating role fail the later one with a
`ResourceConflictException` naming both. Nothing is locked and nothing waits; the conflict is
reported, not serialized.

### Logging

Steps write through `ctx.Log` or the standard `ILogger` abstraction; the lines are collected as that
step's output, each stamped with the time since the scenario started, and appear under the step in
the runner and the report:

```
+0.320s seeded 3 patients
+0.322s [resource] Create Patient:Jane
+1.010s booked appointment 42
```

```csharp
ctx.GetLogger<BookingSteps>().LogInformation("seeded {Count} patients", count);
```

Registering `RaunLoggerProvider` with an in-process system under test attributes *its* logs to the
step that provoked them, because the destination is resolved per write from the step that is running:

```csharp
builder.Logging.AddProvider(new RaunLoggerProvider());
```

`ScenarioContext.Current` is the running step's context for code below the DSL that has no `ctx`
parameter to hand.

### Teardown

Cleanup is registered by the step that created the thing, so the closure captures both the object and
the connection it needs — nothing to derive, nothing to wire. A cleanup runs inside the scenario's
final `Teardown` step, long after the registering step has been reported, so it takes the *teardown*
context to log or attach anything:

```csharp
[StepName("Given patient {name} exists")]
[return: Created]
public static async Task<Patient> PatientExists(string name, ScenarioContext? ctx = null)
{
    var patient = await Db.InsertPatient(name);
    ctx?.OnTeardown(teardown =>
    {
        teardown.Log($"deleted patient {patient.Id}");
        return Db.DeletePatient(patient.Id);
    });
    return patient;
}
```

Reaching for the step's own `ctx` inside the cleanup instead is a compile error (`RAUN014`): that
output would be lost. Every scenario reports a final `Teardown` step, so a cleanup that throws fails
visibly instead of being swallowed. Cleanups run in reverse dependency order, and one that throws does
not stop the rest — every error is collected onto that step.

`[Teardown(Run.OnSuccess)]` on the scenario leaves state intact when the test failed; `Run.Never`
disables cleanup entirely while you go and poke at it. A registration marked `Cleanup.Required`
ignores that policy and runs regardless — including after cancellation or a timeout — for things
whose absence is a leak rather than a choice:

```csharp
ctx?.OnTeardown(Cleanup.Required, () => container.StopAsync());
```

### Tracing

Raun emits OpenTelemetry-ready spans from the `Raun` `ActivitySource`: one root span per scenario,
a child span per step and per teardown, tagged with the OpenTelemetry test semantic conventions
(`test.suite.name`, `test.case.name`, `test.case.result.status`) and the step's identity, with log
lines and resource events as span events. The step span is `Activity.Current` while the step runs,
so every outgoing `HttpClient` call carries its `traceparent` and the system under test's own spans
land under the step that provoked them — one trace per scenario, across the wire. Raun never exports;
subscribe with whatever you already use:

```csharp
using var tracing = Sdk.CreateTracerProviderBuilder()
    .AddSource(RaunTelemetry.SourceName)
    .AddHttpClientInstrumentation()
    .AddOtlpExporter()
    .Build();
```

Each step's output ends with its trace id, and the HTML report shows it. Nothing is recorded
without a listener.

## Aspire

`Raun.Aspire` builds your AppHost as the run's preflight node, waits for the resources you name, and
registers the running `DistributedApplication` for your steps. Your `Program.cs` stays real code:

```csharp
return await RaunAspire.RunAsync<Projects.MyAppHost>(args, aspire =>
{
    aspire.WaitFor("api");
    aspire.Services(services => services.AddHttpClient("api", (sp, client) =>
        client.BaseAddress = sp.GetRequiredService<DistributedApplication>().GetEndpoint("api")));
});
```

See `samples/AspireAppointments` for the full shape, including exporting Raun's and the API's spans
to one collector.

## Project layout

| Project | What it is |
| --- | --- |
| `src/Raun` | Runner-neutral core: phase markers, parallel awaiters, `ScenarioContext`, the graph model, resources, teardown, tracing, and the DAG scheduler. |
| `src/Raun.Generator` | Roslyn incremental generator + analyzer (`RAUN000`–`RAUN015`). netstandard2.0, shipped inside `Raun.Mtp`. |
| `src/Raun.Mtp` | Microsoft.Testing.Platform test framework: `[Scenario]`, discovery, run loop, per-step node reporter, HTML report. |
| `src/Raun.Aspire` | Aspire integration: builds the AppHost, starts it as the run's preflight while waiting for the resources you declare, registers it for your steps. Plumbing only — no phase markers, no steps. |
| `samples/AppointmentTests` | End-to-end sample: linear, tuple, array, LINQ, conditionals, resources, teardown, logging, a custom phase marker; run with `--report-html` for the report showcase. |
| `samples/AspireAppointments` | Aspire end-to-end: an AppHost, a mock API, and a suite that starts it as preflight, drives it as two actors, and exports traces. |
| `test/*` | Scheduler tests, generator/analyzer tests (behavioral + Verify snapshots), and MTP acceptance tests. |

Design documents live under `docs/superpowers/specs/`, one per feature, each recording the
alternatives that lost. Releases are tag-driven; see [docs/RELEASING.md](docs/RELEASING.md).

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
