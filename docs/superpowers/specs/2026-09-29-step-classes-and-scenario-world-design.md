# Step classes and the scenario world — Design

- **Date:** 2026-09-29
- **Status:** Implemented. A spike proved the core on the reviewer's cancellation scenario; the
  cutover followed: step classes are now the only authoring model (see [Implementation](#implementation)).
- **Scope:** `src/Raun` (phase and scenario base types, the world, the Setup node), `src/Raun.Generator`
  (step recognition, receiver binding, diagnostics), `src/Raun.Mtp` (reporting Setup), both samples, and
  all three test projects.
- **Out of scope:** union-typed step outcomes and pattern-matching conditions (a separate spec), and
  "pure local values" (still deferred from the step-arguments design).

## Problem

This started as a narrow request about step groups (`Given.Patients.Exists("Jane")`):

1. Accept grouped calls consistently for `Given`, `When` and `Then`, including groups on custom phases.
2. Report a specific diagnostic when a grouped call cannot be lowered, instead of the generic RAUN004.
3. Keep IntelliSense useful: a group exposes only its own steps, without duplicate flat methods.
4. Test the generated code by running it, not only by compiling it: step order, prior-result
   arguments, failure reporting and display names.

Groups turned out to be the visible end of a deeper gap. **Raun has no scenario state.** Everything a
step needs arrives as a parameter:

- Suites thread the same context (a `TestIsolation`, a tenant, an API client) through every step
  call, so it shows up in every step signature and every scenario line.
- `ScenarioContext` reaches a step through an optional trailing parameter, `ScenarioContext? ctx = null`.
  The body has to type-check without it, so it is declared nullable, and every step writes `ctx?.`
  even though the generator always supplies it.
- Steps are static extension members on sealed marker types (`extension(Given) { … }`). That is
  C# 14 syntax most readers have not met. A static member cannot hold state, so a group cannot either.

A user of Raun reviewed an earlier sketch, in which groups were DI-constructed objects that owned
their own isolation. They asked for four things instead:

- **Scenario state:** Raun creates one typed state object per scenario. It can hold `TestIsolation`
  and register its cleanup. No static or shared mutable state.
- **Step results stay explicit:** a result produced by one step is still passed as an argument to the
  next, so the dependency graph stays visible.
- **Framework context:** a non-null `ScenarioContext` for state creation and, where needed, for steps.
  The nullable optional parameter should not be the API contract.
- **Lifetime:** Raun awaits asynchronous state creation before any step runs, and disposes the state
  even when a step fails. Parallel-scenario behaviour is defined up front.

A last constraint came from the maintainer: **use more standard C# and fewer tricks.** Someone reading a
scenario should be able to press F12 on every name and land on ordinary code.

The same reviewer then read the first draft of this design. They agreed with its direction and asked
for three things to be settled before implementing it, plus a spike on a real cancellation scenario.
The [review](#review-resolutions) section records how each is settled.

## Principle

A scenario body is ordinary C# against ordinary objects:

- `Given` is a property;
- `Patients` is a property;
- `Exists` is an instance method on a class you wrote.

The generator still reads the body instead of running it, because that is how Raun builds the graph.
Each call becomes a call to the same method, on the instance Raun holds for that scenario.

Two kinds of state are kept apart:

- **`Context`** belongs to Raun: the running step's cancellation, logging, teardown and resources.
- **`World`** belongs to you: `TestIsolation`, clients, anything scenario-scoped.

Nothing of yours goes into `ScenarioContext`, and nothing of Raun's goes into your world.

## Surface

The code below is `samples/AppointmentTests/CancellationScenarios.cs`, shortened.

### 1. The world: your per-scenario state

```csharp
public sealed class ClinicWorld : IScenarioWorld<ClinicWorld>
{
    private ClinicWorld(TestIsolation isolation) => Isolation = isolation;

    public TestIsolation Isolation { get; }

    public static async ValueTask<ClinicWorld> CreateAsync(ScenarioContext context)
    {
        // Scenario metadata: [IsolationSeed(2102)] on the scenario, else a seed from its stable id.
        var seed = context.Scenario.Method?.GetCustomAttribute<IsolationSeedAttribute>()?.Seed
            ?? StableSeed(context.Scenario.Id);

        var isolation = await TestIsolation.CreateAsync(seed, context.CancellationToken);
        context.OnTeardown(Cleanup.Required, _ => isolation.ReleaseAsync());   // registered the moment it exists
        return new ClinicWorld(isolation);
    }
}
```

- The world is created by a **static factory**, not by a constructor plus an init method. A world that
  exists has therefore finished initializing: its members need no `= null!`. If creation fails part
  way, there is no half-built object to dispose. Whatever was made before the failure is released by
  the cleanup registered the moment it was made.
- `context` is the Setup node's context and is never null. Its logs land on Setup, `context.Scenario`
  describes the scenario being set up, and `context.Services` is the scenario's DI scope.
- A world that implements `IAsyncDisposable` or `IDisposable` is also disposed in teardown.

### 2. Step classes: plain classes, instance methods

```csharp
public sealed class ClinicGiven : Given<ClinicWorld>
{
    // A group is a property returning Steps<T>(). `Given.Customers.` lists only customer steps.
    public CustomerSteps Customers => Steps<CustomerSteps>();
    public BookingSteps Bookings => Steps<BookingSteps>();
    public StubSteps Stubs => Steps<StubSteps>();
}

public sealed class BookingSteps : Given<ClinicWorld>
{
    [StepName("Given {customer} has a booking in {daysAhead} days")]
    public Task<Booking> Existing(Customer customer, int daysAhead)
    {
        var booking = new Booking($"B-{World.Isolation.Seed}-{customer.Name}", customer, /* … */);
        Context.OnTeardown(teardown => { teardown.Log($"deleted {booking.Id}"); return Task.CompletedTask; });
        return Task.FromResult(booking);
    }
}

// A custom phase is the same thing with its own label.
[PhaseName("Eventually")]
public sealed class ClinicEventually : Phase<ClinicWorld> { /* … */ }
```

Everything else a step can declare works as it does today: `[StepName]`, resource roles, `[Uses<T>]`,
timeouts and `OnTeardown`. Only where a step lives has changed.

### 3. The wiring: once per suite

```csharp
public abstract class ClinicScenarios : Scenarios<ClinicWorld>
{
    public ClinicGiven      Given      => Steps<ClinicGiven>();
    public ClinicWhen       When       => Steps<ClinicWhen>();
    public ClinicThen       Then       => Steps<ClinicThen>();
    public ClinicEventually Eventually => Steps<ClinicEventually>();
}
```

### 4. Scenarios: instance methods, explicit results

```csharp
[DisplayName("Appointment cancellation")]
public sealed class CancellationScenarios : ClinicScenarios
{
    [Scenario("customer cancels a booking")]
    [IsolationSeed(2102)]
    public async Task CustomerCancels()
    {
        var customer = await Given.Customers.Exists("Jane");
        var booking = await Given.Bookings.Existing(customer, daysAhead: 10);
        await (Given.Stubs.Accepts("notifications"), Given.Stubs.Accepts("calendar"));

        var cancellation = await When.Bookings.Cancel(customer, booking);

        await (Then.IsRecorded(cancellation), Then.Notifications.WereSent(customer, booking));
        await Eventually.SlotIsFree(booking);
    }
}
```

Tuple and array groups, LINQ unrolls, `if`/`else` conditions and step arguments are unchanged.

### What Raun ships

```csharp
public interface IScenarioWorld<TSelf> where TSelf : class, IScenarioWorld<TSelf>
{
    static abstract ValueTask<TSelf> CreateAsync(ScenarioContext context);
}

public abstract class Phase<TWorld> where TWorld : class, IScenarioWorld<TWorld>
{
    protected TWorld World { get; }                     // the scenario's world
    protected ScenarioContext Context { get; }          // the running step's; throws outside a step
    protected T Steps<T>() where T : Phase<TWorld>;     // groups
}

[PhaseName("Given")] public abstract class Given<TWorld> : Phase<TWorld> …;
[PhaseName("When")]  public abstract class When<TWorld>  : Phase<TWorld> …;
[PhaseName("Then")]  public abstract class Then<TWorld>  : Phase<TWorld> …;

public abstract class Scenarios<TWorld> where TWorld : class, IScenarioWorld<TWorld>
{
    protected T Steps<T>() where T : Phase<TWorld>;     // for the compiler and the IDE; never run
}

public sealed class PhaseNameAttribute(string name) : Attribute;
public sealed record ScenarioInfo(string Id, string DisplayName, string MethodName, MethodInfo? Method);
public sealed class NoWorld : IScenarioWorld<NoWorld>;   // for suites with no state
```

`ScenarioContext` gains `Scenario` (a `ScenarioInfo`). `ScenarioScope<TWorld>` is public, because
generated code calls it, but it is hidden from IntelliSense and is not for user code.

These replace, and the cutover removed:

- `IPhase` and the sealed `Given`/`When`/`Then` markers;
- static extension-member DSLs;
- the `ScenarioContext? ctx = null` parameter (the generator no longer supplies a trailing context);
- static `[Scenario]` methods.

A suite with no state derives its scenarios from `Scenarios<NoWorld>`; every scenario has a world.

## Review resolutions

### 1. Where the isolation seed comes from

The suite calls `Given.Isolation(2102)` today, varying the seed per scenario. If the world creates the
isolation before any step runs, the seed has to be known before any step runs. **It is scenario
metadata.** `ScenarioContext.Scenario.Method` is the `[Scenario]` method, so a world reads an attribute
of its own from it:

```csharp
var seed = context.Scenario.Method?.GetCustomAttribute<IsolationSeedAttribute>()?.Seed
    ?? StableSeed(context.Scenario.Id);
```

This is plain reflection over a plain attribute. Raun does not know what a seed is. `context.Scenario.Id`
is the scenario's stable id, the same on every run and every machine, so a scenario that doesn't care
which seed it gets can derive one and needs no attribute at all.

The alternative was to keep isolation creation as an explicit step, with Setup creating only the
world's common dependencies. That stays possible: `var isolation = await Given.Isolation(2102);` is
an ordinary step whose result is passed explicitly, just as today. It was not chosen as the model,
because it brings back what the world exists to remove: every step that needs the isolation takes it as
a parameter again. Mutating the world from inside a step is not a middle way, because the world is
fixed once Setup finishes (see 3).

### 2. What guarantees a group belongs to the current scenario

The first draft had the generator emit the receiver chain as written, which re-evaluates the properties
when the step runs. A property could return a new, unbound step object. **Raun now binds a step class
by its type and never runs a phase or group property.** For `Given.Customers.Exists("Jane")` the
generator emits:

```csharp
__inputs.Get<ScenarioScope<ClinicWorld>>(0).Steps<CustomerSteps>().Exists("Jane")
```

Node 0 is Setup, and its output is the scenario's scope. `Steps<T>()` returns that scenario's one
instance of `T`, created on first use and bound to its world.

For that to be honest, every property on the way must say the same thing. The contract, checked by
RAUN019 at the offending link, is:

- the receiver is a chain of properties starting at the scenario: `Given`, `this.Given`,
  `Given.Customers`, and so on;
- the first property is declared on a `Scenarios<TWorld>` class, and each later one on a step class;
- each property is declared `=> Steps<T>()` (or a getter that returns it), where `T` is the
  property's own type.

A local, a cast, a method call, a field, or a property that builds its own instance is refused, and
the diagnostic names the link and says why. A property from a referenced assembly has no source to
read, so it is taken at its type.

`Scenarios<TWorld>.Steps<T>()` throws if it is ever actually called, because a scenario body is never
run directly. The properties exist for the compiler, the IDE and the reader.

### 3. What "read-only world" means

The world's **references** do not change after `CreateAsync` returns. `World.Isolation` is the same
object for every step of the scenario. The objects they point at (the isolation, its stub server, its
clients) are **mutable, shared infrastructure**. Sibling steps of a tuple or array group use them at
the same time:

```csharp
await (Given.Stubs.Accepts("notifications"), Given.Stubs.Accepts("calendar"));
```

The two steps above both write to the one stub server, concurrently, on one `StubSteps` instance. Raun
guarantees that:

- each sibling sees its own `Context`;
- both see the same `World`;
- the step class holds no per-step state.

Raun does **not** detect two siblings conflicting on shared infrastructure. RAUN013 covers step
outputs with resource roles, and the world is not a step output. Infrastructure that parallel steps
touch has to be safe for concurrent use: the sample's stub server keeps its stubs in a
`ConcurrentDictionary` keyed by service. Steps that cannot share it have to be sequenced rather than
grouped. RAUN022 flags writable fields and properties on step classes. It cannot see inside the
world's objects.

## Runtime model

### Instances

For each scenario run, Raun creates:

- **one world**, in the Setup node;
- **one scope**, `ScenarioScope<TWorld>`, which holds the world and the step-class instances;
- **at most one instance of each step class**, created on first use, through the scenario's DI scope
  when there is one, so a step class may take constructor dependencies. `World` is bound after
  construction, so it cannot be used in the constructor.

The scenario class itself is never instantiated. Nothing is static, and nothing is shared between
scenarios.

### Context

`Phase<TWorld>.Context` returns the context of the step that is running. Underneath, it reads the
`AsyncLocal` that `ScenarioContext.Current` already uses for per-step log attribution, so parallel
siblings each see their own. Outside a running step it throws an `InvalidOperationException` that
says so. It never returns null.

A side effect is that `Context` read *inside* a cleanup lambda is the Teardown node's context, because
the scheduler makes that one current while cleanups run. So `Context.Log(…)` in a cleanup lands on
Teardown, which is what RAUN014 exists to enforce. Capturing it first (`var ctx = Context;`) still gets
the registering step's context — and RAUN014 already flags that: it looks for any `ScenarioContext`
local or parameter captured from outside the cleanup, and `Context` itself is a property, not a capture.

### Lifetime

1. **Setup** is node 0. It awaits `TWorld.CreateAsync(context)` and outputs the scope. **Every step
   depends on it.** So Setup reuses the graph's own rules instead of adding new scheduler code:
   - a failed Setup skips every step with `dependency failed: Setup`;
   - a cancelled one skips them as cancelled;
   - a run filter that selects one step keeps Setup, because Setup is a dependency.
2. **Steps** run on the graph, as today.
3. **Teardown** runs cleanups in reverse order of the owning node, so **Setup's cleanups run last,
   after every step's.** Within Setup they run last-registered first: the world is disposed, and then
   the isolation it was built on is released.
4. A world that implements `IAsyncDisposable`/`IDisposable` is registered for disposal the moment it
   exists, as a required cleanup. It is disposed whatever the scenario's outcome, cancellation and
   timeout included.

## Generator

- **Recognising a step.** A call is a step of a step class when the invoked method is an instance
  method declared on a class deriving from `Phase<>`. Its phase label comes from the nearest
  `[PhaseName]` in the class's base chain, so built-in and custom phases take exactly the same path.
  Nothing else is a step: any other awaited call is RAUN004, as before.
- **Setup and dependencies.** Every scenario's class derives from `Scenarios<TWorld>`, so every
  scenario gets the Setup node, and every step depends on it.
- **Step identity.** It includes the group path: `Customers.Exists("Jane")`, not `Exists("Jane")`. Two
  groups with a same-named step therefore get different ids.
- **The method.** `ScenarioDefinition.Method` is emitted for every scenario, so a world can read
  the scenario method's attributes.
- **CA1822.** A step that reads neither `World` nor `Context` would draw "mark members as static" in a
  suite that runs the .NET analyzers, and a static step cannot be called through a phase property.
  Raun ships a `DiagnosticSuppressor` that suppresses CA1822 on public instance methods of step classes,
  and only there.

### Diagnostics

Each new rule is listed in `AnalyzerReleases.Unshipped.md`. The numbering skips RAUN018, which is the
MSBuild error `Raun.props` raises for an SDK below the floor.

| Rule | When | Reported at |
|---|---|---|
| **RAUN019** Step receiver cannot be followed | Any break in the binding contract above: a local, a cast, a method call, a field, a static or indexed property, a property on the wrong kind of class, a property not declared `=> Steps<T>()`, or a step class with no `[PhaseName]` in its chain. It replaces RAUN004 for grouped calls. | the offending link |
| **RAUN020** Scenario does not fit its class | A `[Scenario]` method whose class does not derive from `Scenarios<TWorld>` (reported once, instead of once per step), or a static one. | the method name |
| **RAUN021** Step class belongs to another world | A step class of `WorldA` reached from a `Scenarios<WorldB>`. | the receiver |
| **RAUN022** Step class holds mutable state (warning) | A non-readonly field or a property with a setter (`init` excepted) on a step class, instance or static. One instance serves every step of a scenario, and a static member every scenario. A warning, not an error: a field guarded by a lock is fine, and only its author knows. A readonly field is not looked into. | the member |

A step-class call nested inside another step's argument is refused by RAUN007, as a phase-marker call
already is.

## Reporting

- Setup is a reported node with phase `Setup` and display name `Setup`. **It is unnumbered.**
  `--list-tests` shows `Setup` and then `1. Given customer Jane exists`, so giving a scenario a world
  does not renumber its steps.
- The HTML report shows it first, with the world's log lines. Teardown shows the world's cleanups
  last.
- A group does not change a step's display name. `[StepName]` is the whole name, and a group exists
  only for authoring.

## Requirements, and how each is met

| Requirement | Met by |
|---|---|
| Groups for Given/When/Then and custom phases | A group is a property on a step class. Recognition goes by `Phase<>`, and custom phases take the same path. |
| A specific diagnostic | RAUN019 names the link it could not follow and the reason. |
| Useful IntelliSense | A step class's public surface is its steps plus its groups. `World`, `Context` and `Steps<T>()` are protected. A test asserts that `Phase<TWorld>` adds no public member. |
| Execution tests | See [Implementation](#implementation). |
| One typed state object per scenario | `IScenarioWorld<TSelf>`, created per scenario in Setup. |
| Step results stay explicit | Unchanged. The world is ready before any step, so it adds no step-to-step edges and hides none. |
| A non-null context | `CreateAsync(ScenarioContext)` receives it. Steps read `this.Context`, which is never null inside a step. |
| Async creation, disposal on failure | The Setup node; disposal and cleanups as required teardown entries, run after every step's. |
| Parallel behaviour | Per scenario, everything is separate. Within a scenario: see review resolution 3. |
| The seed | Scenario metadata through `context.Scenario.Method` (review resolution 1). |
| Group binding | Bound by type through the Setup node's scope; properties must return `Steps<T>()` (review resolution 2). |

## Implementation

Built in two passes on branch `claude/jolly-johnson-yo85m9` ([redoz/Raun#3](https://github.com/redoz/Raun/pull/3)):
first a spike beside the extension-member DSL, on the reviewer's cancellation scenario, then the
cutover.

- **Runtime** (`src/Raun/Steps/`): `IScenarioWorld<TSelf>`, `NoWorld`, `Phase<TWorld>`,
  `Given/When/Then<TWorld>`, `PhaseNameAttribute`, `Scenarios<TWorld>`, `ScenarioScope`, `ScenarioInfo`.
  It also adds `ScenarioContext.Scenario`, `ScenarioDefinition.Method` and `ScenarioNode.IsSetup`, and
  Setup is unnumbered in `StepNumbering`. `Phases.cs` (`IPhase` and the markers) is gone.
- **Generator:** the Setup node, step-class recognition, type-bound receivers, RAUN019–021, RAUN022 in the analyzer, step-class
  calls refused inside arguments, and the CA1822 suppressor. The phase-marker path and the trailing
  `ScenarioContext` injection are gone.
- **Samples:** all three suites are step classes.
  - `samples/AppointmentTests` booking and lifecycle scenarios use `NoWorld`, partial step classes
    split across two files, and a custom `Eventually` phase.
  - `CancellationScenarios.cs` in the same sample has the reviewer's shape: a seeded isolation
    (`[IsolationSeed(2102)]`, and a derived seed), groups under Given, When and Then, and parallel
    stub steps on the shared world.
  - `samples/AspireAppointments` holds the API client in its world, resolved once per scenario from
    the services `Program.cs` registers, instead of every step fetching it from `ctx.Services`.
- **Tests.** Every generator test source is migrated; scenario bodies did not change, and step ids
  did not move, only the classes around them. `test/Raun.Generator.Test/StepClassTests.cs` compiles the
  generated code, runs it through the real scheduler, and asserts:
  - step order, prior-result arguments, display names and phases, custom phase included;
  - Setup is node 0, every step depends on it, and it is unnumbered;
  - the seed read from the scenario method;
  - parallel siblings overlapping on one world, each with its own context and logs;
  - two concurrent scenarios with separate worlds;
  - a failing step keeps its name and exception, its dependents are skipped, and the world is still
    disposed after the step cleanups;
  - a Setup that fails part way skips every step and still releases the isolation it made;
  - a cancelled scenario still disposes its world;
  - a single-step filter keeps Setup;
  - grouped steps get distinct ids;
  - `Phase<TWorld>` adds no public member;
  - RAUN019 (local, cast, a property not returning `Steps<T>()`), RAUN020 (outside a `Scenarios<>`,
    static) and RAUN021.

  RAUN022 is tested for instance and static fields and settable properties, and for the members
  it must leave alone. `StepMethodSuppressorTests` covers the suppressor, and a snapshot
  (`GeneratorSnapshotTests.StepClass_scenario`) pins the emitted code.

**Not done:** step arguments that read the world (open question 2).

## Open questions

1. **The name "World".** It is the Cucumber/Reqnroll term, so many readers will know it. `State` or
   `Fixture` are the alternatives.
2. **Can a scenario pass a world value to a step?** For example
   `When.Book(World.Isolation.DefaultClinic, slot)`. Not today: `Scenarios<TWorld>` exposes no `World`,
   and an argument naming an instance member is RAUN007. Nothing needs it yet — a step already has
   `World`, so `When.Book(slot)` reads the clinic itself. It would matter only when a scenario has to
   choose *which* world value a step acts on; then it needs just the Setup edge every step already has.
3. **Steps shared across worlds.** A generic step class
   (`class AuditThen<TWorld> : Then<TWorld> where TWorld : class, IScenarioWorld<TWorld>, IHasIsolation`)
   should work with no special support. It is untested.

## Alternatives considered

- **Groups as static extension properties on the existing markers.** This solves grouping alone. A
  static group has nowhere to keep state, and it takes the extension-block trick one level deeper.
- **Groups as DI-constructed objects that own their isolation.** This was the first sketch, and the
  reviewer turned it down: it merges step organisation with state ownership.
- **Emitting the receiver chain as written.** This was the first draft. It re-runs arbitrary properties
  when a step runs, and a property could return an unbound instance (review resolution 2).
- **`InitializeAsync` on a constructed world.** This was the first draft. It costs a `= null!` on every
  member set there, and a world that failed part way through initializing still exists, so disposing
  it has to cope with half a world.
- **The isolation as an explicit step**, with a world of common dependencies only. It stays possible,
  but as the model it re-threads the isolation through every step (review resolution 1).
- **Raun-owned `Given<TWorld>` with steps as extension members on it.** Less ceremony, but steps read
  `given.World`, not `this.World`, and extension blocks stay the main way of writing steps.
- **The scenario class as the world (xUnit-style, one instance per test).** This removes a concept, but
  it ties every step class to one scenario base class and mixes state with wiring.
- **No base classes: all DI.** Step classes would be injected through each scenario class's primary
  constructor, with an `IScenarioContextAccessor`. The constructor has to be repeated on every scenario
  class, and steps read `accessor.Context` instead of `this.Context`.
- **An ambient `Scenario.State<T>()` accessor with no base classes.** The world's type appears nowhere in
  a step's signature or class, so a step's dependency on it is invisible.
- **A generator-filled parameter declared `ScenarioContext context = default!`.** The call site shows
  nothing, and the default lies about nullability.
