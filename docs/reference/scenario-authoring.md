# Writing scenarios

[Start here](../../README.md) · [Sample walkthrough](../../samples/AppointmentTests/README.md) · [Resources](resources.md) · [Running tests](running-tests.md)

## Scenarios and steps

A scenario class derives from `Scenarios<TWorld>`. Use `NoWorld` when the scenario needs no shared state.
Properties such as `Given => Steps<AppointmentGiven>()` select step classes.
Steps are public instance methods on classes derived from `Given<TWorld>`, `When<TWorld>`, `Then<TWorld>`, or `Phase<TWorld>`.
Use any assertion library. Put retries and polling inside steps.

Scenario bodies are processed at build time; do not call them directly. The analyzer reports unsupported syntax.
See the [scenario](../../samples/AppointmentTests/Scenarios.cs) and [step definitions](../../samples/AppointmentTests/AppointmentDsl.cs).

## A complete starter example

After the [installation steps](running-tests.md#install-and-run), save this as PatientScenarios.cs.
This small example shows the class wiring. Replace the local name step with calls to your application.

```csharp
using Raun;
using Xunit;

namespace MyScenarios;

public sealed class PatientScenarios : Scenarios<NoWorld>
{
    public PatientGiven Given => Steps<PatientGiven>();
    public PatientThen Then => Steps<PatientThen>();

    [Scenario("a patient keeps their name")]
    public async Task PatientName()
    {
        var name = await Given.PatientName("Jane");
        await Then.NameIs(name, "Jane");
    }
}

public sealed class PatientGiven : Given<NoWorld>
{
    [StepName("Given a patient named {name}")]
    public Task<string> PatientName(string name) => Task.FromResult(name);
}

public sealed class PatientThen : Then<NoWorld>
{
    [StepName("Then the patient is named {expected}")]
    public Task NameIs(string actual, string expected)
    {
        Assert.Equal(expected, actual);
        return Task.CompletedTask;
    }
}
```

Run it with `dotnet run -- --report-html`. For a larger example, follow the
[appointment sample](../../samples/AppointmentTests/README.md).

## Inline scenario cases

Add named constant rows beside a parameterized scenario. The analyzer checks each row against the
method's parameter types and reports missing, extra or incompatible arguments during the build.

```csharp
[Scenario("endpoint access")]
[ScenarioCase("anonymous", "/appointments", EndpointRole.Anonymous, HttpStatusCode.Unauthorized)]
[ScenarioCase("reader", "/appointments", EndpointRole.Reader, HttpStatusCode.OK)]
public async Task EndpointAccess(string endpoint, EndpointRole role, HttpStatusCode expected)
{
    var status = await When.Request(endpoint, role);
    await Then.StatusIs(status, expected);
}
```

Each row appears as a separate scenario, such as `endpoint access [reader]`, with its own world,
DI scope, Setup and teardown. Steps can be discovered, filtered and reported independently. See the
runnable [authorization example](../../samples/AppointmentTests/AuthorizationScenarios.cs), which also checks
forbidden access to an admin endpoint with a local handler.

Case parameters are read-only inside step argument expressions.

Use positional primitive/string constants, enum values (including flags), and null for nullable
parameters. Arguments follow C# implicit conversion rules. Arrays, `typeof` values, member-data
providers and DI-created argument values are not supported. Case methods cannot be generic,
overloaded, or use optional, `params` or by-reference parameters.

Case names must be nonblank and unique within the method; they are case-sensitive. Keep those names
stable: scenario and step IDs use the method identity and case name. Editing values or reordering rows
preserves their IDs; renaming a case changes them. Rows appear in source order. Parameterized scenarios
require at least one row; parameterless scenarios continue to use `[Scenario]` alone.

Escape the display name's brackets when using the platform tree-filter grammar. This runs the reader
case, including Setup and teardown:

```bash
dotnet run --project samples/AppointmentTests -- --treenode-filter '/*/*/*/endpoint access \[reader\]/*'
```

IDE step-UID selection also isolates an individual case.

## Step titles

`[StepName]` uses C# interpolation syntax inside its ordinary string literal: `{{` and `}}` escape
literal braces, and `{value,8:D2}` combines alignment and formatting. `$return` is the extra token
for the step result; put a common format after the whole fallback: `{$return ?? name:id}`.
Parameter/member paths remain the supported title expressions.

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

In the example, `Create("Jane")` selects the shorter title;
`Create("Jane", time: "09:00")` selects the title that also mentions the time.
Explicitly supplied null values count as supplied arguments for title selection.

See the [title examples](../../samples/AppointmentTests/ResourceTrackingScenarios.cs) for a runnable example.

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
See the [branch examples](../../samples/AppointmentTests/BookingOutcomeScenarios.cs).

## Worlds, Setup, and services

Implement `IScenarioWorld<TWorld>.CreateAsync(ScenarioContext)` to create scenario state.
Raun calls it in each scenario's `Setup` node. Steps access that state through `World` and runtime services through `Context`.
A failed Setup skips the scenario's steps. Disposable worlds are released during teardown.

Resolve services from `context.Services` in the factory. Use `context.Scenario` for scenario metadata.
Parallel steps share one world and step-class instance, so keep references stable and services safe for concurrent use.
See the [world example](../../samples/AppointmentTests/CancellationScenarios.cs).

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
