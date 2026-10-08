# Pattern branching

Status: design approved 2026-10-03. Revised 2026-10-08: the move to .NET 11 is deferred; branching
ships on .NET 10 / C# 14 first.

## Goal

Make scenarios able to branch on *what a step returned*, not only on whether it returned `true`.

Decisions taken in brainstorming (Patrik, 2026-10-03, revised 2026-10-08):

- Scenarios gain **`switch` on any step result plus `is` patterns** in `if`, `when` clauses included.
- The runtime branching model becomes **"a condition selects an arm"** (approach A). The other
  approach was lowering `switch` to nested `if`/`else` over hidden tests; it was rejected because it
  makes the internal graph unlike the source.
- **Raun stays on .NET 10 (`net10.0`, C# 14).** The move to .NET 11 is a later, separate piece of
  work (see "Deferred: .NET 11").

Out of scope:

- The move to .NET 11, and with it C# 15 unions, closed hierarchies and collection expression
  arguments.
- Unions in Raun's own public model (for example, `StepResult` as a union). That breaks every
  consumer for an internal benefit.
- Labeled `break`/`continue`.

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

Raun adds no exhaustiveness checking of its own. Once Raun is on C# 15, a `union` or `closed`
hierarchy as the step's result type gets the compiler's exhaustiveness check with no change to Raun,
because the generated selector is the scenario's own `switch`.

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
  `if` whose condition is false does today.
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
    // replaces Func<object?, bool>? EvaluateCondition; reads the value via inputs.Get<T>(Index)
    public Func<IStepInputs, int>? SelectArm { get; init; }  // 0..Arms.Count-1, or -1: no arm
    public IReadOnlyList<string> Arms { get; init; } = [];   // labels as written
    …
}
```

- **`SelectArm`** is emitted by the generator as the real C# construct over the condition node's
  recorded result:
  - for a `switch`, a `switch` statement whose sections return their arm index;
  - for an `if`, `value is P ? 0 : 1`, or `(bool)value ? 0 : 1`.

  It takes the scenario's inputs, not just the condition's value, because a `when` clause may read
  other step outputs. Pattern semantics and `when` clauses are therefore the compiler's, never
  Raun's.
  Patterns are lowered through `ArgumentLowering`, so the type and member names in them resolve in
  generated code, as they do in arguments.
- **`Arms`** labels each arm as the source writes it: `case Accepted accepted`,
  `case Accepted accepted when accepted.Express`, `default`, `if`, `else`. Validation checks every
  `Guard.Arm` against it. A not-taken step's reason names the arm that ran, or says that nothing
  matched. *Refined during planning:* the implemented format is
  `not taken: <operation> took <arm label>` (for example `not taken: Submit took case Rejected
  rejected`) and `not taken: <operation> matched no arm`; it names the condition by its operation,
  not its display name. An arm label shows its tokens as written, each run of whitespace or comments
  between them as one space.
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

## 4. Samples, docs and release

- **Platform:** unchanged. `net10.0`, C# 14, the current SDK floor and Roslyn baseline.
- **Samples.** `samples/AppointmentTests` gains a scenario that switches on a step returning a small
  record hierarchy (for example `Booked`, `Waitlisted`, `Refused`), with one `case` per subtype.
- **Docs.** The README's supported-subset section documents `switch` and `is` branching. RELEASING.md
  records the generator-contract break.
- **Release.** `v0.3.0-beta.1`, because the generated-code contract breaks.

## Deferred: .NET 11

When Raun moves to .NET 11 (after GA, November 2026), as its own spec:

- `net11.0` everywhere, the SDK floor (RAUN018) at 11.0.100, CI on the 11.0 SDK.
- Tests for C# 15 types: an exhaustive switch over a `union` (a value-type case included) and over a
  `closed` hierarchy, plus a collection expression argument (`[with(...), ...]`). No production
  change is expected: the selector is the compiler's own `switch`, and since #2 a step argument is
  any expression.
- Union and closed-hierarchy samples.
- Code style under the .NET 11 SDK: with `AnalysisLevel` at `latest-all`, it enables every IDE style
  rule at its default preference, which contradicts this repo's style (about 1,100 errors: `var`,
  expression bodies, file-scoped namespaces). Record the repo's preferences in `.editorconfig` (or pin
  `AnalysisLevel`), then lift the `global.json` pin to the 10.0 SDK bands.

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
- **Value-type results.** A switch on a step returning a value type (an `int`, with constant and
  relational patterns) selects the right arm and binds unboxed values.
- **Snapshots.** A new snapshot for a switch scenario. The existing conditional snapshot moves once,
  deliberately, because `EvaluateCondition` becomes `SelectArm`. That change is reviewed, not
  rubber-stamped.

## Risks

- **Contract break.** `EvaluateCondition` and `Guard.WhenValue` disappear, so code generated by 0.2
  does not run on 0.3. The generator and runtime ship in one package, and scenario libraries are not
  supported, so a rebuild is all a consumer needs.
