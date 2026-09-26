# xUnit v3 Scenario Graph Extension Design

## Goal

Design an xUnit v3 extension for integration-style scenario tests where business steps are reported as individual xUnit tests. Scenarios should support readable Given/When/Then source, sequential execution by default, explicit fork/join parallelism, typed state flowing between steps, and dependent steps skipped after failures.

The primary authoring experience is legal C# 14 using static extension members:

```csharp
using AppointmentTests;
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

The scenario method is source for the generator. xUnit does not execute this method directly. The generator lowers the method body into a scenario manifest/executor that runs each DSL call as a separate xUnit-reported step.

## Architecture

The design has two packages:

1. Runtime xUnit v3 extension package.
2. Source-generator/analyzer package.

The runtime owns behavior: discovery, stable test identities, execution scheduling, per-step xUnit reporting, failure handling, skip propagation, cancellation, logging, attachments, and awaiter helpers for tuple/array parallelism.

The generator owns compile-time lowering. It analyzes `[Scenario]` methods, recognizes calls to `Given`, `When`, and `Then` static extension members, validates the scenario subset, and emits a compact manifest plus executor consumed by the runtime.

This maps onto xUnit v3 extension seams:

- A custom `[Scenario]` attribute marks scenario methods.
- A custom `IXunitTestCaseDiscoverer` creates one scenario test case per scenario.
- A custom `IXunitTestCase` exposes one visible test per scenario step.
- `ISelfExecutingXunitTestCase` runs the generated scenario graph so it can report pass/fail/skip messages per step and implement dependency-aware skip semantics.

## Phase Types and Domain DSL

The framework defines marker phase types:

```csharp
namespace Raun;

public static class Given;
public static class When;
public static class Then;
```

If C# static extension members cannot target static classes in the final compiler shape, these can become uninstantiable marker classes without changing the scenario call site.

Applications define domain DSLs as C# 14 static extension members on those phase types:

```csharp
namespace AppointmentTests;

public static class AppointmentDsl
{
    extension(Given)
    {
        [StepName("patient {name} exists")]
        public static async Task<Patient> PatientExists(string name)
        {
            throw new NotImplementedException();
        }

        [StepName("an available slot exists")]
        public static async Task<Slot> AvailableSlot()
        {
            throw new NotImplementedException();
        }
    }

    extension(When)
    {
        [StepName("creating an appointment")]
        public static async Task<Appointment> CreateAppointment(Patient patient, Slot slot)
        {
            throw new NotImplementedException();
        }
    }

    extension(Then)
    {
        [StepName("the appointment should exist")]
        public static async Task AppointmentExists(Appointment appointment)
        {
            throw new NotImplementedException();
        }
    }
}
```

These methods are real implementations. They are not no-op stubs and they do not return custom `Step<T>` handles. The scenario source can use ordinary domain values because each `await` unwraps `Task<T>` into `T`.

## Source Generation Model

The generator treats the `[Scenario]` method body as declarative source. It does not rely on executing the method.

For each supported statement, the generator uses Roslyn syntax and semantic information to build a scenario graph:

- An awaited `Given`, `When`, or `Then` DSL call becomes a step node.
- The local variable assigned from an awaited call becomes that node's named output.
- Later DSL calls that reference that local variable become dependent on the producing node.
- Source order creates sequencing edges by default, so two statements without data dependency still run in the order written.
- Explicit parallel forms create sibling nodes that may run concurrently and join before the next statement.

Generated output includes:

- A manifest with scenario metadata, step metadata, display names, source locations, stable IDs, traits, timeout settings, and dependency edges.
- An executor method that invokes the original DSL methods in the generated order, stores each step output, and supplies outputs to dependent steps.
- Optional debug/source mapping so generated diagnostics point back to the scenario source.

The original scenario method can remain in the compiled assembly, but xUnit uses the generated executor, not the user-authored method body.

## Sequential and Parallel Semantics

Source order is sequential by default:

```csharp
public static async Task Booking()
{
    await Given.DatabaseIsClean();
    var patient = await Given.PatientExists("Jane");
    var slot = await Given.AvailableSlot();
}
```

The three statements run in the written order, even if a later statement does not consume an earlier value.

Parallelism is explicit with awaited tuples:

```csharp
public static async Task Booking()
{
    await Given.DatabaseIsClean();

    var (patient, slot) = await (
        Given.PatientExists("Jane"),
        Given.AvailableSlot());

    var appointment = await When.CreateAppointment(patient, slot);

    await Then.AppointmentExists(appointment);
}
```

The tuple elements are sibling step nodes. They can run in parallel after `DatabaseIsClean`, and the following statement waits for both.

Parallelism is also explicit with awaited arrays for homogeneous bulk work:

```csharp
public static async Task ImportUsers()
{
    var users = await new[]
    {
        Given.UserExists("alice"),
        Given.UserExists("bob"),
    };

    var import = await When.ImportUsers(users);

    await Then.ImportShouldContainUsers(import, users);
}
```

LINQ-generated arrays are supported:

```csharp
public static async Task ImportUsers()
{
    var users = await Enumerable.Range(1, 10)
        .Select(i => Given.UserExists($"user-{i}"))
        .ToArray();

    var import = await When.ImportUsers(users);

    await Then.ImportShouldContainUsers(import, users);
}
```

The runtime supplies awaiters so direct execution is honest C#:

```csharp
public static TaskAwaiter<(T1, T2)> GetAwaiter<T1, T2>(
    this (Task<T1> first, Task<T2> second) tuple);

public static TaskAwaiter<T[]> GetAwaiter<T>(this Task<T>[] tasks);
```

The generator still lowers these forms explicitly, so xUnit sees each tuple/array element as its own step.

Bare collection expressions are not a v1 target because `await [ ... ]` has no target type. Supported v1 bulk forms are `await new[] { ... }` and `await query.ToArray()`.

## Execution Semantics

Runtime discovery creates one `ScenarioTestCase` per `[Scenario]`. The scenario test case exposes one `ScenarioStepTest` per generated graph node so reporters and IDEs can display each step separately.

Execution uses a DAG scheduler:

- Source-order dependencies and dataflow dependencies determine when nodes become ready.
- Explicit tuple/array groups may run concurrently, constrained by scenario-level max parallelism.
- A node starts only after all dependencies complete successfully.
- Operation parameters are resolved from generated locals/output storage.
- `ScenarioContext` can be supported as an optional DSL method parameter for cancellation, logging, attachments, DI, and per-scenario services.
- If a node fails, transitive dependents are reported skipped/not-run.
- Independent ready branches may continue by default; dependent branches are skipped.

The default failure policy is: continue already-ready independent work, skip anything that depends on the failed node.

## Analyzer Rules

The analyzer validates the supported scenario subset and reports clear diagnostics when code cannot be lowered safely.

Required diagnostics:

- `[Scenario]` methods must be `async Task` or `async ValueTask`.
- Step statements must be awaited DSL calls, awaited tuples of DSL calls, or awaited arrays of DSL calls.
- Unsupported control flow is rejected in v1 unless explicitly added later.
- DSL calls must resolve to static extension members on `Given`, `When`, or `Then`.
- DSL methods must return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`.
- Tuple and array parallel groups must contain lowerable DSL calls.
- Variables used as DSL arguments must come from prior generated step outputs or ordinary constants/parameters allowed by the scenario subset.
- Display-name format placeholders must bind to method parameters and avoid unsafe object dumping unless a formatter is configured.

Future versions can add loops, conditionals, richer collection expressions, and discriminated-union-friendly branch modeling.

## Reporting and Errors

Each scenario step has a stable unique ID based on scenario ID and generated step ID. IDs should not depend only on source line numbers.

Step display names come from DSL operation attributes, with formatted arguments where safe:

```csharp
[StepName("patient {name} exists")]
```

Complex object formatting should use explicit display formatters to avoid noisy or sensitive output.

Failure handling:

- Operation exceptions fail the current step.
- Dependent steps are skipped with a reason such as `dependency failed: creating an appointment`.
- Multiple dependency failures are summarized.
- Timeouts and cancellation can be declared at scenario or operation level.
- Logs, output, and attachments are routed through `ScenarioContext` and associated with the current step.

## Testing Strategy

Generator and analyzer tests should use Verify source-generator support to make generated output and diagnostics easy to review. Test cases should cover:

- Valid linear scenarios.
- Awaited tuple parallel groups.
- Awaited explicit array parallel groups.
- Awaited LINQ `.ToArray()` parallel groups.
- Source-order dependencies without dataflow.
- Type mismatch diagnostics.
- Unsupported syntax diagnostics.
- Duplicate names or generated IDs.
- Display-name formatting diagnostics.

Runtime DAG scheduler tests should be independent from xUnit:

- Source-order sequencing.
- Parallel ready nodes.
- Join behavior.
- Failure propagation.
- Cancellation.
- Skip reason generation.

xUnit v3 acceptance tests should verify discovery and execution messages:

- One scenario appears as one scenario test case.
- Each step appears as an individual visible test.
- Passing, failing, skipped, timed-out, and canceled steps produce expected messages.
- Tuple and array parallel groups can run in parallel and following steps wait for all prerequisites.

An end-to-end sample project should demonstrate the `AppointmentDsl` style API, including tuple and array parallelism.

## Rejected Baseline

Pure generated `[Fact]` methods plus an orderer is not the preferred design. It is simpler but weak for this use case: fail-dependent-skip behavior, typed graph state, fork/join scheduling, and stable reporting would all be harder and more runner-dependent.

Custom `Step<T>` handles in user syntax are also rejected for the primary API. They add ceremony without value once the generator lowers from the AST and the real DSL methods can return ordinary `Task<T>` domain results.

## Open Design Space

Future versions can explore .NET discriminated unions for richer branch result modeling. The core should not depend on unreleased discriminated-union syntax, but the typed-transition model should remain compatible with a future DU-friendly API.
