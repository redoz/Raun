# Pattern branching and the move to .NET 11

Status: design approved 2026-10-03; spec awaiting review.

## Goal

Make scenarios able to branch on *what a step returned*, not only on whether it returned `true`, and
move Raun onto .NET 11 / C# 15 so that union types and closed class hierarchies — the C# 15 features
whose cases the compiler knows completely — work as first-class scenario branches.

Decisions taken in brainstorming (Patrik, 2026-10-03):

- Raun moves its floor to .NET 11, **now, on RC1**, and its packages target **`net11.0` only**.
- Scenarios gain **`switch` on any step result plus `is` patterns** in `if`, `when` clauses included.
- The runtime branching model becomes **"a condition selects an arm"** (approach A). The other
  approach was lowering `switch` to nested `if`/`else` over hidden tests; it was rejected because it
  makes the internal graph unlike the source.

Out of scope:

- Unions in Raun's own public model (for example, `StepResult` as a union). That breaks every
  consumer for an internal benefit, so it is deferred.
- Extension indexers and labeled `break`/`continue`.
- Closed hierarchies for Raun's phases, because custom phases must stay possible.

## C# 15, as shipped in RC1

The features C# 15 stabilizes are collection expression arguments (`[with(...), ...]`), unions,
closed class hierarchies, extension indexers, labeled `break`/`continue`, and non-virtual static
interface members. Dictionary expressions, closed enums and the inference improvements did *not*
make C# 15. Collection expression arguments need nothing from Raun: since #2 a step argument is any
expression, lowered by symbol. A test pins that.

## 1. Authoring surface

```csharp
// is-pattern condition: the first arm when it matches, the second (else) otherwise
if (await When.SubmitOrder(order) is Rejected rejected)
    await Then.CustomerNotified(rejected.Reason);

// switch on a step result: one arm per section
switch (await When.SubmitOrder(order))
{
    case Accepted { Total: > 1000 } big:
        await Then.ManualReviewRequested(big);
        break;
    case Accepted accepted when accepted.Express:
        await Then.ShipmentScheduled(accepted.Shipment, Speed.Express);
        break;
    case Rejected rejected:
        await Then.CustomerNotified(rejected.Reason);
        break;
    default:
        await Then.OrderIsPending(order);
        break;
}
```

With `public union OrderOutcome(Accepted, Rejected);` as the step's result type, the compiler checks
that the switch is exhaustive. Raun neither needs nor adds exhaustiveness checking of its own.

Rules:

- **Branchable conditions.** A branch is one of:
  - `if (await S)`, where `S`'s result can drive a C# `if`, as today;
  - `if (await S is P)`, where `P` is any pattern, `not` patterns included;
  - `switch (await S) { … }`.

  `S` is always a step call, so the decision is a node in the graph. Anything else in a condition —
  `&&`, `||`, a non-awaited value, a step result held in a local — is RAUN011, which says which of
  these it was.
- **Arms.**
  - `if` has two arms: the body, and the `else` (empty when absent).
  - A `switch` has one arm per section. Several labels on one section (`case A: case B:`) are one arm.
  - A section ends in `break;`. A `return`, `throw` or `goto case` in a section is RAUN003, the
    existing control-flow rule.
  - An arm's body is the same subset as an `if` arm's, so branches nest.
- **`when` clauses** are lowered with `ArgumentLowering`, the same rule as step arguments. They may use
  the case's pattern variables, constants, static members and earlier step outputs. They may not use
  `await`, a step call, a write to a step output, or a private name (RAUN007, at the expression). An
  earlier step output a `when` reads becomes a dependency of the condition node, because the branch
  decision reads it.
- **No match.** When no arm matches, every arm is reported not taken. This is exactly what a bare
  `if` whose condition is false does today. Exhaustiveness is the compiler's job: unions and closed
  hierarchies get it, and other types do not need it.
- **Locals.**
  - A local assigned differently across arms is merged after the branch, with one source per arm.
  - An arm that leaves the local alone contributes a pass-through.
  - C#'s definite-assignment rules already demand a `default` arm, or full coverage, when such a local
    is read afterwards.
- **Not supported:**
  - a switch *expression* whose arms are steps, because an expression arm cannot hold a statement;
  - `goto`;
  - labeled `break`;
  - switching on anything but an awaited step.

  A switch expression over a step's *recorded* result, used inside a step argument, is an ordinary
  argument expression and already works.

## 2. Runtime model

One branching concept: **a condition node selects an arm.**

```csharp
public readonly record struct Guard(int ConditionIndex, int Arm);

public sealed class ScenarioNode
{
    // replaces Func<object?, bool>? EvaluateCondition
    public Func<object?, int>? SelectArm { get; init; }      // 0..Arms.Count-1, or -1: no arm
    public IReadOnlyList<string> Arms { get; init; } = [];   // labels as written
    …
}
```

- **`SelectArm`** is emitted by the generator as the real C# construct over the condition node's
  recorded result:
  - for a `switch`, a `switch` statement whose sections return their arm index;
  - for an `if`, `value is P ? 0 : 1`, or `(bool)value ? 0 : 1`.

  Pattern semantics, `when` clauses and union matching are therefore the compiler's, never Raun's.
  Patterns are lowered through `ArgumentLowering`, so the type and member names in them resolve in
  generated code, as they do in arguments.
- **`Arms`** labels each arm as the source writes it: `case Accepted accepted`,
  `case Accepted accepted when accepted.Express`, `default`, `if`, `else`. Validation checks every
  `Guard.Arm` against it. A not-taken step's reason names the arm that ran, for example
  `not taken: 'submitting the order' matched case Rejected rejected`, or says that nothing matched.
- **Case nodes.** An arm whose pattern declares variables (`case Accepted a`,
  `{ Total: var total }`, `is Rejected r`) gets one hidden *case node*:
  - it is guarded on that arm and depends on the condition node;
  - it re-runs that arm's pattern on the recorded result and returns the variables, as a single value
    or a value tuple;
  - inside the arm those variables are ordinary step outputs, read through the case node, so
    dataflow, dependencies and display-name placeholders work unchanged.

  A case node is synthetic, like today's merge and pass-through nodes. It is not a reported business
  step.
- **Merges** generalize to N sources: one per arm that defines the local, plus a pass-through, guarded
  on its arm, for each arm that does not. Validation keeps requiring the sources of a merge to be
  mutually exclusive. Two different arms of one condition are exclusive by construction.
- **Scheduler.** When a condition node passes, the scheduler calls `SelectArm` once and records the
  arm. A guard holds when its condition passed and the recorded arm equals `Guard.Arm`. Not-taken
  propagation, waits, filter closure (`ScenarioGraph`) and the resource ledger read guards only, so
  they carry over unchanged apart from the field.
- **Compatibility.** `EvaluateCondition` and `Guard.WhenValue` are removed, which breaks the
  generated-code contract. This is acceptable because the generator and the runtime ship in one
  package and this is a breaking release (`0.3.0`). RELEASING.md's contract section records the break.

## 3. Generator

- **Parser.** `ParseIf` handles the boolean and `is`-pattern forms. A new `ParseSwitch` handles switch
  statements.
  - **Arms:** an arm walk generalizes today's two-arm walk. Each arm gets its guard
    (`Guard(condition, k)`), its child definition map, and its tail of waits.
  - **Rejoin:** for every local the arms define differently, it inserts one N-way merge.
  - **Pattern variables:** these are bound to the arm's case node as a new `VarSource` shape (case
    output, element *i*), and spelled through it.
  - **Diagnostics:** everything wrong is reported, and the walk continues (the one-walker design).
- **`ArgumentLowering`** gains a pattern entry point: patterns and `when` clauses are lowered with the
  same symbol-bound rewriter as arguments, with the case's own pattern variables in scope.
- **Emitter.** It emits `SelectArm`, `Arms`, `Guard(…, arm)`, and the case nodes' `Invoke`, which
  repeats the pattern and returns the variables.

## 4. Platform: .NET 11 RC1

- **Toolchain:**
  - The .NET 11 RC1 SDK, `11.0.100-rc.1.26425.128`, installed from Microsoft's official installer.
  - A new `global.json` pins the repo to the 11.0 SDK, with `allowPrerelease: true` and
    `rollForward: latestFeature`.
- **Targets.** Every project in `src`, `test` and `samples` moves to `net11.0`. The generator stays
  `netstandard2.0`. `LangVersion latest` is C# 15.
- **Generator baseline.** `RaunRoslynVersion` becomes the Roslyn version that ships inside RC1. It is
  read from the installed SDK's compiler during implementation, not guessed. That package is on
  Microsoft's `dotnet-tools` feed, not nuget.org, so:
  - `nuget.config` adds that feed, with package-source mapping so it serves only
    `Microsoft.CodeAnalysis*`;
  - lock files are regenerated, and locked-mode restore keeps working;
  - the test harness uses the same compiler, so union and closed-hierarchy sources compile in tests.
- **SDK floor.**
  - `RaunMinimumSdkVersion` becomes `11.0.100-rc.1`. RAUN018 says the .NET 11 SDK is required.
  - `SdkFloorTests` gains prerelease version strings (`11.0.100-rc.1.26425.128`, `11.0.100-preview.7…`,
    `11.0.100`), proving the comparison orders prereleases correctly.
- **CI and release workflows.** `setup-dotnet` installs `11.0.x` with `dotnet-quality: preview`.
- **Dependencies.** `Microsoft.Extensions.*` and Aspire stay on their stable 10.x versions, which run
  on net11. No RC libraries are taken.
- **Samples and docs.**
  - `samples/AppointmentTests` gains a union scenario (a step returning
    `union BookingOutcome(Booked, Waitlisted, Refused)`, switched on) and a closed-hierarchy one.
  - The README's install section names the .NET 11 SDK and C# 15. Its supported-subset section
    documents `switch` and `is` branching.
  - RELEASING.md records the contract break.
- **Release.** `v0.3.0-beta.1`.
- **GA follow-up (November 2026).** When .NET 11 is GA:
  - move `global.json`, `RaunMinimumSdkVersion` and `RaunRoslynVersion` to the GA versions;
  - take the GA Roslyn from nuget.org;
  - remove the `dotnet-tools` feed and its mapping;
  - drop `dotnet-quality: preview`.

## Testing

TDD throughout, behavioural first: generated code is compiled and run through the real scheduler.

- **Runtime.**
  - Arm selection, for 2-arm and N-arm conditions.
  - No arm matched, so every arm is not taken.
  - Nested guards across a switch inside an `if`.
  - N-way merge resolution.
  - The not-taken reason names the matched arm.
  - Validation rejects an out-of-range arm and non-exclusive merge sources.
- **Lowering.** For each form (`if` boolean, `if is`, `if is not`, `switch` with type, constant,
  property and positional patterns, `when`, multiple labels, `default`), the test checks the
  definition shape (guards, arms, case nodes, merge sources) and runs the scenario for each arm.
- **Pattern variables.** They flow into steps, into display-name placeholders, and through N-way
  merges.
- **`when` clauses.** A clause that reads an earlier step output makes the condition depend on it; one
  that holds `await` or a step call is RAUN007 at the expression.
- **Diagnostics.**
  - Compound conditions are RAUN011.
  - `return`, `throw` or `goto case` in a section is RAUN003.
  - A switch on a non-step is RAUN011.
  - Each fires once, at the offending node.
- **Unions and closed hierarchies** compiled with the RC1 compiler. An exhaustive switch over each
  lowers and runs every arm. A union case that is a value type is matched through the boxed `Value`.
- **Collection expression arguments** in a step argument (`[with(StringComparer.Ordinal), a, b]`)
  lower and run.
- **Snapshots.** A new snapshot for a switch scenario. The existing conditional snapshot moves once,
  deliberately, because `EvaluateCondition` becomes `SelectArm`. That change is reviewed, not
  rubber-stamped.
- **Platform.** `SdkFloorTests` prerelease cases. Every existing test stays green on net11.0.

## Risks

- **RC churn before GA.** Union pattern lowering changed twice during the previews ("try both" in
  Preview 7). Because `SelectArm` is the compiler's own `switch`, Raun inherits whatever GA decides
  and re-implements nothing. Only the union samples could need a touch at GA.
- **The `dotnet-tools` feed** is Microsoft's public engineering feed. Package-source mapping limits it
  to `Microsoft.CodeAnalysis*`, and it is removed at GA.
- **Consumers must install the RC SDK** to take `0.3.0-beta.1`. RAUN018 makes that a clear build
  error, not a silent green run of zero tests.
