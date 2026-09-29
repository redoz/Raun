# Step classes and the scenario world — Design

- **Date:** 2026-09-29
- **Status:** Draft for review. Nothing here is implemented.
- **Scope:** `src/Raun` (phase and scenario base types, the world lifetime, the Setup node),
  `src/Raun.Generator` (step recognition, receiver lowering, diagnostics), `src/Raun.Mtp` (reporting
  Setup), both samples, and all three test projects.
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

## Principle

A scenario body is ordinary C# against ordinary objects:

- `Given` is a property;
- `Patients` is a property;
- `Exists` is an instance method on a class you wrote.

The generator still reads the body instead of running it, because that is how Raun builds the graph.
But it emits each call exactly as it was written, on a real instance. Its only job is deciding *when*
each call runs.

Two kinds of state are kept apart:

- **`Context`** belongs to Raun: the running step's cancellation, logging, teardown and resources.
- **`World`** belongs to you: `TestIsolation`, clients, anything scenario-scoped.

Nothing of yours goes into `ScenarioContext`, and nothing of Raun's goes into your world.

## Surface

### 1. The world: your per-scenario state

```csharp
public sealed class AppointmentWorld(IConfiguration config) : IScenarioWorld, IAsyncDisposable
{
    public TestIsolation Isolation { get; private set; } = null!;

    // Awaited before the first step. `context` is the Setup node's context and is never null.
    public async ValueTask InitializeAsync(ScenarioContext context)
        => Isolation = await TestIsolation.CreateAsync(config, context.CancellationToken);

    // Runs after teardown, whether or not a step failed.
    public ValueTask DisposeAsync() => Isolation.DisposeAsync();
}
```

- The world is constructed from the scenario's DI scope, so constructor injection works and nothing
  needs registering.
- `InitializeAsync` is a default interface method, so a world with nothing asynchronous to do leaves
  it out.
- Disposal is plain `IAsyncDisposable` (or `IDisposable`).

### 2. Step classes: plain classes, instance methods

```csharp
public sealed partial class AppointmentGiven : Given<AppointmentWorld>
{
    // A group is a property. `Given.Patients.` lists only patient steps.
    public PatientSteps Patients => Steps<PatientSteps>();

    [StepName("Given an available slot exists")]
    [return: Created]
    public Task<Slot> AvailableSlot()
        => World.Isolation.Slots.CreateAsync(Context.CancellationToken);
}

public sealed class PatientSteps : Given<AppointmentWorld>
{
    [StepName("Given patient {name} exists")]
    [return: Created]
    public async Task<Patient> Exists(string name)
    {
        var patient = await World.Isolation.Patients.CreateAsync(name, Context.CancellationToken);
        Context.OnTeardown(t => World.Isolation.Patients.DeleteAsync(patient, t.CancellationToken));
        return patient;
    }
}

public sealed class AppointmentWhen : When<AppointmentWorld>
{
    [StepName("When {patient} books {slot}")]
    [return: Created]
    public Task<Appointment> Book([Read] Patient patient, [Edited] Slot slot)
        => World.Isolation.Api.BookAsync(patient, slot, Context.CancellationToken);
}

// A custom phase is the same thing with its own label.
[PhaseName("Eventually")]
public sealed class AppointmentEventually : Phase<AppointmentWorld> { /* … */ }
```

Everything else a step can declare works exactly as it does today: `[StepName]`, resource roles,
`[Uses<T>]`, timeouts and `OnTeardown`. Only where a step lives has changed.

### 3. The wiring: once per project

```csharp
public abstract class AppointmentScenarios : Scenarios<AppointmentWorld>
{
    public AppointmentGiven      Given      => Steps<AppointmentGiven>();
    public AppointmentWhen       When       => Steps<AppointmentWhen>();
    public AppointmentThen       Then       => Steps<AppointmentThen>();
    public AppointmentEventually Eventually => Steps<AppointmentEventually>();
}
```

Built-in and custom phases are declared on the same line, the same way. Go-to-definition on `Given`
inside a scenario lands here.

### 4. Scenarios: instance methods, explicit results

```csharp
public sealed class BookingScenarios : AppointmentScenarios
{
    [Scenario("patient books an available slot")]
    public async Task Books()
    {
        var (patient, slot) = await (Given.Patients.Exists("Jane"), Given.AvailableSlot());
        var appointment     = await When.Book(patient, slot);
        await Then.IsConfirmed(appointment);
    }
}
```

Tuple and array groups, LINQ unrolls, `if`/`else` conditions and step arguments are unchanged.

### What Raun ships

```csharp
public interface IScenarioWorld
{
    ValueTask InitializeAsync(ScenarioContext context) => ValueTask.CompletedTask;
}

public abstract class Phase<TWorld> where TWorld : IScenarioWorld
{
    protected TWorld World { get; }                     // bound by Raun, one per scenario
    protected ScenarioContext Context { get; }          // the running step's; throws outside a step
    protected T Steps<T>() where T : Phase<TWorld>;     // groups: same scenario, same world
}

[PhaseName("Given")] public abstract class Given<TWorld> : Phase<TWorld> where TWorld : IScenarioWorld;
[PhaseName("When")]  public abstract class When<TWorld>  : Phase<TWorld> where TWorld : IScenarioWorld;
[PhaseName("Then")]  public abstract class Then<TWorld>  : Phase<TWorld> where TWorld : IScenarioWorld;

public abstract class Scenarios<TWorld> where TWorld : IScenarioWorld
{
    public TWorld World { get; }
    protected T Steps<T>() where T : Phase<TWorld>;
}

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class PhaseNameAttribute(string name) : Attribute;

public sealed class NoWorld : IScenarioWorld;           // for suites with no state
```

This replaces:

- `IPhase` and the sealed `Given`/`When`/`Then` markers;
- static extension-member DSLs;
- the `ScenarioContext? ctx = null` parameter;
- static `[Scenario]` methods.

## Runtime model

### Instances

For each scenario run, Raun creates:

- **one scenario-class instance.** It needs a parameterless constructor, because a scenario class holds
  no state (state belongs in the world);
- **one world**, built from the scenario's DI scope;
- **at most one instance of each step class**, created on first use by `Steps<T>()`. Creation goes
  through the same DI scope, so a step class may take constructor dependencies. `World` is bound
  after construction, so it cannot be used in the constructor.

Nothing is static, and nothing is shared between scenarios.

### Context

`Phase<TWorld>.Context` returns the context of the step that is running. Underneath, it reads the
`AsyncLocal` that `ScenarioContext.Current` already uses for per-step log attribution, so parallel
siblings each see their own context. Outside a running step it throws an `InvalidOperationException`
that says so. It never returns null. `RAUN014` (a step's context captured into a cleanup) keeps
applying: a cleanup takes the teardown context as its lambda parameter.

### Lifetime

1. **Setup node.** Raun constructs the scenario instance and the world, then awaits
   `World.InitializeAsync(context)`. Setup is reported like Teardown, and logs written during
   initialization land on it.
2. **Steps** run on the graph, as today.
3. **Teardown node:** registered cleanups run in the order the teardown design defines.
4. **World disposal** fills the reserved final slot that the teardown design left for framework-owned
   cleanups. It runs after every user cleanup, because those cleanups may still use the world. It runs
   whatever the scenario's outcome, the same way a `Cleanup.Required` registration does.

If Setup fails, every step is skipped with `dependency failed: Setup`. Teardown still runs (a
half-initialized world may already have registered cleanups), and a world that was constructed is
still disposed.

Selecting one step with a run filter also runs Setup, the same way it already runs Teardown.

### Parallelism

- **Across scenarios:** each scenario has its own world, step-class instances and DI scope.
  Anything genuinely shared goes through DI singletons and `[Uses<T>]`, as today.
- **Within a scenario:** siblings in a tuple or array group share the world and the step-class
  instances concurrently. **A world must be read-only once `InitializeAsync` returns, or
  thread-safe, and a step class must hold no mutable state.** RAUN013 does not police this, because
  the world is not a step output. A later analyzer rule could flag writable fields on step classes.

## Generator

### Recognising a step

A call is a step when the invoked method is declared on a class deriving from `Phase<>`. Its phase
label comes from the nearest `[PhaseName]` in the class's base chain. Built-in and custom phases go
through exactly the same path, which covers requirement 1.

### Lowering the receiver

The receiver has to be a chain of property or field reads that starts at the scenario instance:
`Given`, `this.Given`, `Given.Patients`. The generator emits that chain unchanged against the instance
Raun created:

```csharp
// today
Invoke = static async (__inputs, __ctx) => { var __r = await Given.PatientExists("Jane"); … }

// proposed
Invoke = static async (__scenario, __inputs, __ctx) =>
    { var __r = await ((BookingScenarios)__scenario).Given.Patients.Exists("Jane"); … }
```

The generated code is compiled outside the scenario class, so every link in the chain must be
reachable from there, which means public or internal. This is the same reachability rule that step
arguments already follow.

### Diagnostics

New rules (numbers provisional; each goes in `AnalyzerReleases.Unshipped.md`):

| Rule | When | Reported at |
|---|---|---|
| **RAUN018** Step receiver cannot be followed | The receiver is not a property/field chain from the scenario. It might be a local (`var g = Given;`), a method call (`GetGiven().X()`), an indexer, or a link that is `private`/`protected`. The message names the link and the reason. This replaces the generic RAUN004 for grouped calls (requirement 2). | the offending link |
| **RAUN019** Scenario class shape | A `[Scenario]` method that is static; a scenario class that does not derive from `Scenarios<TWorld>`; a scenario class with no accessible parameterless constructor. | the method or class |
| **RAUN020** Step class and scenario worlds differ | A step class for `WorldA` is reached from a `Scenarios<WorldB>`. | the receiver |

RAUN004 stays for a call on something that is not a step class at all. The messages of RAUN002, 004,
006 and 011 change from "phase-marker call" to "step call".

### Emission

- `ScenarioDefinition` gains a factory for the scenario instance and one for the world.
- The Setup node is emitted in every scenario, the way Teardown already is.
- Step identity (the uid) keeps its current definition, so moving a step into a group changes its
  uid, just as renaming it does today.

## Reporting

- Setup is a reported node with the reserved name `Setup`, mirroring `Teardown`.
- The HTML report shows it at the top of the scenario card.
- Group membership does not change the display name. `[StepName]` stays the whole name, and a group
  exists only for authoring.

## Requirements, and how each is met

| Requirement | Met by |
|---|---|
| Groups for Given/When/Then and custom phases | A group is a property on a step class. Recognition goes by `Phase<>`, and custom phases take the same path. |
| A specific diagnostic | RAUN018 names the link it could not follow and the reason. |
| Useful IntelliSense | A step class's public surface is its steps plus its groups. `World`, `Context` and `Steps<T>()` are protected, so they are hidden from call sites. No flat duplicates are needed. |
| Execution tests | See Testing below. |
| One typed state object per scenario | `Scenarios<TWorld>` / `IScenarioWorld`, built per scenario from the scenario's DI scope. |
| Step results stay explicit | Unchanged. The world is ready before the first step, so it adds no graph edges and hides none. |
| A non-null context | `InitializeAsync(ScenarioContext)` receives it as a parameter. Steps read it as `this.Context`, which is never null. The nullable parameter is gone. |
| Lifetime and parallel behaviour | The Setup node, disposal in the reserved final slot, and the parallelism rules above. |

## Migration

Raun is pre-1.0 (`docs/RELEASING.md`: anything may change between minors). **Cut over rather than
support both authoring models.** Keeping two lowering paths would double the generator's surface and
every diagnostic's wording. The migration is mechanical:

1. `extension(Given) { public static … }` becomes `class XGiven : Given<W> { public … }`.
2. `ctx?.` becomes `Context.`.
3. `[Scenario] public static` becomes an instance method on a `Scenarios<W>` subclass.
4. The phase properties are declared once.

Both samples are migrated as part of the work. The Aspire sample's app handle moves into its world,
which resolves it from DI. The preflight still builds the app, as AGENTS.md requires.

## Testing

Tests come first, in the existing project split.

| Project | Coverage |
|---|---|
| `Raun.Test` | World lifetime: `InitializeAsync` runs before the first step; disposal runs after the last cleanup when a step failed, when the scenario timed out, and when Setup threw. A Setup failure skips every step with `dependency failed: Setup`. Two parallel scenarios get distinct worlds. Two parallel siblings each observe their own `Context`. `Context` throws outside a step. `Steps<T>()` returns one instance per scenario. |
| `Raun.Generator.Test` | Snapshots of a grouped call under each of Given, When, Then and a custom phase. RAUN018 for each shape it refuses (local, method call, indexer, private link), placed on the link. RAUN019 and RAUN020. A test asserting that `Phase<TWorld>`'s public surface is empty. **Execution tests** (requirement 4): compile the generated source, run it through the scheduler, and assert step order, the arguments a step received from prior results, a failing step's status and exception, and the rendered display names, for both flat and grouped calls. |
| `Raun.Mtp.Test` | Setup is discovered and reported, and survives a single-step filter. Step numbering around Setup (see open questions). |
| Samples | `AppointmentTests` uses a world, a group and a custom phase. The Aspire sample holds its app in the world. |

## Open questions for review

1. **Numbering Setup.** Should Setup be numbered, which shifts every step's number by one, or reported
   unnumbered? Recommendation: unnumbered, so step numbers and `--list-tests` output stay stable.
2. **Can step arguments read the world?** For example `When.Book(World.Isolation.DefaultClinic, slot)`.
   Recommendation: yes, read-only. The world is ready before step 1, so there is no graph edge to track,
   and writes are refused as they are for step outputs.
3. **The name "World".** It is the Cucumber/Reqnroll term, so many readers will know it. `State` or
   `Fixture` are the alternatives.
4. **`InitializeAsync` versus a static factory.** `InitializeAsync` costs a `= null!` on properties set
   there. A `static abstract ValueTask<TSelf> CreateAsync(ScenarioContext, IServiceProvider)` avoids that,
   but static abstract interface members are less familiar than an `IAsyncLifetime`-style init method.
   Recommendation: `InitializeAsync`.
5. **Steps shared across worlds.** A generic step class (`class AuditThen<TWorld> : Then<TWorld> where
   TWorld : IHasIsolation`) should work with no special support. The spike should confirm this.

## Alternatives considered

- **Groups as static extension properties on the existing markers** (`extension(Given) { public static
  PatientSteps Patients => … }` plus `extension(PatientSteps) { … }`). This solves grouping alone. A static
  group has nowhere to keep state, and it takes the extension-block trick one level deeper.
- **Groups as DI-constructed objects that own their isolation.** This was the first sketch, and the
  reviewer turned it down: it merges step organisation with state ownership, so every group re-derives
  the same context.
- **Raun-owned `Given<TWorld>` with steps as extension members on it.** Less ceremony (no user step classes,
  no wiring base), but steps read `given.World`, not `this.World`, and extension blocks stay the main
  way of writing steps.
- **The scenario class as the world (xUnit-style, one instance per test).** This removes a concept, but it
  ties every step class to one scenario base class and mixes state with wiring.
- **No base classes: all DI.** Step classes would be injected through each scenario class's primary
  constructor, with an `IScenarioContextAccessor`. It is the purest option, but the constructor has to
  be repeated on every scenario class, and steps read `accessor.Context` instead of `this.Context`.
- **An ambient `Scenario.State<T>()` accessor with no base classes.** It works, but the world's type
  appears nowhere in a step's signature or class, so a step's dependency on it is invisible.
- **A generator-filled parameter declared `ScenarioContext context = default!`.** The call site shows
  nothing, and the default lies about nullability. It is today's contract with the `?` hidden.
