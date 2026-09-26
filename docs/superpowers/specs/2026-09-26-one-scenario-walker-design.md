# One scenario walker: the parser reports, the analyzer steps back

Status: implemented 2026-09-26. Follows on from the step-arguments change (issue #2).

## Problem

Two hand-written walkers read every `[Scenario]` body, and nothing forced them to agree.

- `ScenarioParser` lowered the body into a graph. It stopped at the first statement it could not
  lower and reported RAUN017 there.
- `ScenarioAnalyzer` re-walked the same body to report RAUN001–007 and RAUN011–013.

Because each re-implemented the statement grammar and its own set of step-output locals, they drifted.
Some shapes both accepted, and the value was silently dropped (`Field = await Given.X()`). Some the
parser refused with a vague RAUN017 while the analyzer said nothing (`var (a, b) = await
Given.X()`, `;`). Some the analyzer refused falsely. RAUN017 existed only to catch that drift.

## Decision

There is one walker and one reporter.

- **The parser is the only thing that reads a scenario body.** It no longer stops at the first
  problem. It reports every diagnostic it finds and keeps walking, then returns either a scenario or
  its diagnostics, never both. `ParseOutcome` can only be built through `Lowered(scenario)` or
  `Refused(diagnostics)`, and `Refused` requires at least one diagnostic. A scenario therefore either
  lowers or carries at least one error, so it cannot vanish; a parser bug that broke this rule would
  surface as RAUN000.
- **One mistake, one diagnostic.** A statement that fails marks every local it declares or assigns
  as a *failed* output. That local still counts as a step output, so its readers are not reported
  again, and no merge is attempted through it. What a local holds has three distinct shapes: a step,
  an array group, or failed. No code can therefore read a step index off a local that has none.
- **The generator reports those diagnostics** under their existing ids: RAUN001–007, RAUN011,
  RAUN013, RAUN017. They are located in the scenario's own source tree, found through the compilation
  in a reporting step that is kept separate from emission. The editor squiggles them, and `#pragma` and
  `[SuppressMessage]` apply, while the emitted files stay cached.
- **The analyzer keeps only the rules that are not about lowering a scenario body:** `[StepName]`
  placeholders (RAUN008), resource roles and lineage on DSL methods (RAUN009/010), cleanup context
  (RAUN014), and contended resources (RAUN015/016).

A test that checks the two sides agree would prove nothing, because there is only one side. Each rule
keeps its own behavioural test, now read through `GeneratorHarness.DiagnoseAsync`. That helper
returns everything a build would report, so a test states the rule without caring which component
reports it.

Why the generator reports, and not the analyzer:

- A single pass over each body.
- No way to lose a scenario silently when analyzers are switched off (`RunAnalyzers=false`).
- Generator diagnostics appear in the IDE and in the build just like analyzer diagnostics.
  They are carried as plain values in the IR (never a `Location`) to keep the incremental pipeline
  cacheable, the same approach RAUN017 already took.

## RAUN017

RAUN017 now means "not generated, for a reason no other rule names", and it always carries that
reason. Examples:

- an expression-bodied scenario;
- a deconstruction of a single step's result;
- an array group merged across `if` arms;
- a call that binds to no method.

It is no longer a safety net for disagreement, because there is nothing left to disagree.

## RAUN012 is retired

RAUN012 ("a conditionally assigned local has no step-produced definition") has no case left:

- A non-step initializer before the `if` (`Appointment a = null!;`) is RAUN002 at the declaration.
- A local declared without a value, assigned in one arm and read after the `if`, is already a C#
  error (CS0165).
- What remained was a local assigned in one arm and read only inside it. That lowers fine, so the
  rule only ever fired falsely. The id stays unused.

## Behaviour changes

- An empty statement (`;`) is accepted. The analyzer always accepted it; the parser used to refuse it.
- `var (a, b) = await Given.X()` and an array group assigned in only one arm, for example, now report
  a specific RAUN017 reason at the construct, instead of a bare RAUN017 at the statement.
- A statement that fails no longer hides the problems in the statements after it: every problem is
  reported in one build.
- A LINQ unroll with a count of zero has its body checked too.
