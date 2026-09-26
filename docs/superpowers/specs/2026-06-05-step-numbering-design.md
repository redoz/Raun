# Spec — Step numbering + scenario-name grouping (Raun.Mtp)

**Date:** 2026-06-05
**Status:** Draft (awaiting user review)
**Area:** `src/Raun.Mtp` (display/reporting layer only)

## Problem

In VS Test Explorer the per-step leaf nodes under a scenario render in **alphabetical**
order, not execution order — VS sorts sibling leaves lexically by display name. So
`Booking`'s steps show as `an available slot exists`, `creating an appointment`,
`patient Jane exists`, `the appointment should exist` instead of their logical Given→When→Then
order. Separately, the method-level grouping node shows the C# method name (`Booking`) rather
than the human scenario name (`customer books an appointment`).

## Goals

1. Number the steps so they **sort into logical order** in VS.
2. Make a parallel/array group consume one top-level number, with its members sub-numbered.
3. Show the **scenario name** as the method-level grouping node, keeping namespace/class as-is.

## Requirements

- **R1 — Sequential numbering.** Steps are numbered in `ScenarioNode.Index` (source) order.
  A standalone step (`GroupId == null`) consumes the next top-level integer `N`.

- **R2 — Group sub-numbering.** A parallel/array group (consecutive nodes sharing a non-null
  `GroupId`) consumes **one** top-level integer `N`; its members get sub-indices `N.1`, `N.2`,
  … in `Index` order. There is no separate node for the group itself.

- **R3 — Leaf text.** The leaf display name drops the old `"{scenario} ▸ {step}"` prefix and
  becomes:
  - standalone: `"{label}. {step}"` → `1. the database is clean`
  - group member: `"{label} {step}"` → `2.1 patient Jane exists`

  (Note the standalone label carries a trailing `.`; the group label does not — matching the
  approved mockup.)

- **R4 — Zero-padding for lexical sort (load-bearing).** VS sorts sibling leaves **lexically as
  strings**, so the numeric prefix MUST be zero-padded to a fixed width *per scenario* so that
  lexical order equals numeric order:
  - Pad the **top-level number** to the digit-width of the scenario's largest top-level number.
  - Pad each **sub-index** to the digit-width of the largest group in that scenario.

  Without padding, `10.` sorts before `2.`, and `2.10` before `2.2`. With per-scenario padding,
  small scenarios stay clean (`1.`…`4.`) and large ones stay correctly ordered (`01.`…`12.`).

  *Worked example (12 steps, step 2 is a group of 11):*
  ```
  01. ...                 02.01 ...   02.02 ...   …   02.11 ...   03. ...   …   12. ...
  ```
  Lexical sort of these equals their logical order. (Widths are computed per scenario, so a
  ≤9-step scenario with ≤9-member groups uses width 1 and renders exactly as the mockups below.)

- **R5 — Scenario name as Method.** `TestMethodIdentifierProperty.MethodName` becomes the
  scenario's `DisplayName`. `Namespace` and `TypeName` are unchanged (still derived from the
  scenario method's FQN → `AppointmentTests` / `Scenarios`). Assembly / arity / parameter-types /
  void return are unchanged.

- **R6 — Uid unchanged.** The node `Uid` stays `{ScenarioId}:{StepId}`. Numbering and naming are
  display concerns only and never affect identity, so single-step run filters still resolve to the
  same node discovery emitted.

- **R7 — Discovery and execution agree.** Both the discovery path (`RaunDiscoverer`) and the
  live/finished path (`RaunStepReporter`) compute the label from the **same** numbering source so
  the discovered tree and the running tree show identical leaf text.

## Design

### Component 1 — `ScenarioStepNumbering` (new, `src/Raun.Mtp`)

A pure, unit-testable helper mirroring the existing `ScenarioTestIdentity` precedent.

```
public static IReadOnlyDictionary<int, string> Compute(ScenarioDefinition definition)
```

Returns a map from `ScenarioNode.Index` → label string (e.g. `"1"`, `"2.1"`, `"2.2"`, `"3"`;
or padded `"02.01"` for large scenarios). The caller appends the `.`/space and step text per R3.

**Algorithm (single pass, group-by-first-encounter so it is robust to non-contiguous groups):**
1. Walk `definition.Nodes` in `Index` order. For each node:
   - `GroupId == null` → allocate the next top-level number `T`; record `(node → (T, sub:0))`.
   - `GroupId == g` seen for the first time → allocate the next top-level number `T`; start a
     sub-counter for `g` at 1; record `(node → (T, sub:1))`.
   - `GroupId == g` seen before → reuse `g`'s top-level `T`; record `(node → (T, sub:++))`.
2. Compute padding widths: `topWidth = digits(maxTopLevel)`; `subWidth = digits(maxGroupSize)`.
3. Render each entry: `sub == 0` → `pad(T, topWidth)`; else `pad(T, topWidth) + "." + pad(sub, subWidth)`.

### Component 2 — `ScenarioTestIdentity.Create` signature change

`Create(string methodFullName, string scenarioDisplayName)`: derive `Namespace`/`TypeName` from
`methodFullName` as today, but set `MethodName = scenarioDisplayName` (R5).

### Component 3 — display composition (`RaunDiscoverer.BuildNode`, `RaunStepReporter.BuildNode`)

Both build the numbering map once per definition and, for each step, compose the leaf name per R3
(standalone `"{label}. {step}"`, group member `"{label} {step}"`), dropping the scenario prefix.
Discovery uses `DisplayNameTemplate`; the reporter uses the runtime-formatted step name. Both pass
`definition.DisplayName` into `ScenarioTestIdentity.Create`.

## Worked examples (the four sample scenarios)

```
customer books an appointment            customer books with parallel arrange
  1. patient Jane exists                   1. the database is clean
  2. an available slot exists              2.1 patient Jane exists
  3. creating an appointment               2.2 an available slot exists
  4. the appointment should exist          3. creating an appointment
                                           4. the appointment should exist

bulk user import                         bulk user import via LINQ
  1.1 user alice exists                    1.1 user user-1 exists
  1.2 user bob exists                      1.2 user user-2 exists
  2. importing the users                   1.3 user user-3 exists
  3. the import should contain 2 users     2. importing the users
                                           3. the import should contain 3 users
```

(Note `bulk user import` has the group as the **first** statement → it correctly takes top-level
`1` with members `1.1`/`1.2`.)

## Testing

- **`ScenarioStepNumberingTests` (new):** linear; tuple group; array group; group-at-start;
  two groups in one scenario; ≥10 top-level steps (padding to width 2); a group with ≥10 members
  (sub-index padding); single step; explicit assertion that the produced labels sort lexically
  into logical order.
- **`ScenarioTestIdentityTests`:** updated — `MethodName` equals the scenario display name;
  namespace/type still derived from the FQN.
- **`RaunDiscovererTests` / `RaunStepReporterTests`:** leaf display is the numbered form with no
  scenario prefix; identity method equals the scenario name; uid unchanged.

## Out of scope / follow-ups

- Revert the `await Task.Delay(5000)` debugging artifact in
  `samples/AppointmentTests/AppointmentDsl.cs`.
- **VS re-verification:** the `Method` now contains spaces (`customer books an appointment`).
  VS rendered `Booking` fine and renders parametrized names with spaces/parens, so this is
  expected to work — but because VS's MTP→VSTest bridge managed-name path proved fragile (see the
  grouping handoff), the plan includes a clean-`TestStore` VS check that grouping still holds with
  the scenario name as the method, and that special characters in a scenario name (if any) don't
  break the managed name.

## Files touched

- `src/Raun.Mtp/ScenarioStepNumbering.cs` (new)
- `src/Raun.Mtp/ScenarioTestIdentity.cs` (signature + method = scenario name)
- `src/Raun.Mtp/RaunDiscoverer.cs` (compose numbered display)
- `src/Raun.Mtp/RaunStepReporter.cs` (compose numbered display)
- `test/Raun.Mtp.Test/ScenarioStepNumberingTests.cs` (new)
- `test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs`, `RaunDiscovererTests.cs`, `RaunStepReporterTests.cs`
- `samples/AppointmentTests/AppointmentDsl.cs` (revert debug delay)
