# Step arguments: one lowering, one rule (issue #2)

Status: implemented 2026-09-26 (autonomous run; decisions below are mine and open to Patrik's veto).

## What the issue asked for

Issue #2 asks for two things: typed values built inline as step arguments (`new Spec { Customer = customer, ... }`,
constructor arguments, `with`), and projections of earlier results (`setup.Customer`, `setup.Existing[0]`).
Both must keep the dependency on the producing step, run that step once, and build the value when the
consuming step runs. It also asks that the analyzer and the generator agree, and that an unsupported
expression be reported where it is written, with a reason, instead of ending in a misleading `RAUN007` or a
late `RAUN017`.

## What was actually wrong

A probe of the issue's own scenario showed that it **already lowered, compiled and ran**. The parser rewrote
every identifier inside an argument, so initializers, `with`, projections and nested paths all worked. The
failures the issue describes came from a different place. **The analyzer and the parser read arguments
differently.** The analyzer bound identifiers to symbols, while the parser rewrote them by *name*, with its
own separate rules. The two disagreed on everything except the happy path:

| Argument | Analyzer | Generator |
|---|---|---|
| a `const` / static member of the scenario class | silent | `CS0103` in generated code |
| a type from the scenario's own namespace (not the DSL's) | silent | `CS0246` in generated code |
| `nameof(setup)` | silent | `CS8081` in generated code |
| `new { customer }` | silent | `CS0746` in generated code |
| `x is { P: var e } ? e : …`, `x => { var t = …; }` | false `RAUN007` | fine |
| lambda parameter shadowing a step local | fine | spurious dependency |
| `await Given.X()` / `Given.X()` inside an argument | silent | a hidden step runs inside another |
| `setup.Items[0] = …` inside an argument | silent | silently mutates another step's result |

The root cause is that an argument is **re-hosted**. The emitted call runs inside a static lambda in
`Raun.Generated.RaunGenerated`, with only the scenario file's top-level usings in scope. Every name that
the source resolved lexically has to mean the same thing there. A name-based rewriter cannot guarantee
that.

## The rule (documented in the README)

A step argument is an ordinary C# expression. It is evaluated inside the consuming step, when that step
starts (after its dependencies have finished), once per run of that step. It may use anything C# allows,
with these conditions:

- **A step output is read, not re-run.** Each mention of a local that an awaited step produced becomes a
  dependency on that step, and the argument reads the step's recorded result. This includes mentions of
  its members, elements, and mentions nested inside initializers, `with`, and lambdas.
- **Names mean what they meant in the scenario.** Types, static members and namespaces named by a simple
  name are emitted fully qualified. This covers constants too: an `internal const` of the scenario
  class qualifies like any other static member.

`RAUN007` rejects an argument that would hide scenario structure, or that cannot be reproduced outside the
scenario method. It is reported at the offending expression, and each case carries its own reason:

- a scenario local that no step produced;
- a parameter of the scenario method, since the method is never called;
- an instance member (`this`), since the step body is static;
- a member or nested type that is `private`/`protected`, and so unreachable from generated code. This
  includes a `private const`: see the decision below;
- `await`, because arguments are evaluated synchronously when the step starts;
- a step call (a phase-marker invocation), because it would run as an invisible part of another step;
- a write to a step output (`=`, compound assignment, `++`/`--`, `ref`/`out`), because later steps read
  the same value.

## Design

`Lowering/ArgumentLowering.cs` is the single definition of the rule. It is one
`CSharpSyntaxRewriter` pass over an in-tree argument, bound to the scenario's `SemanticModel`. The pass
produces three things together:

- the re-hosted expression;
- the step outputs it reads, as symbols;
- the violations, each with a node and a reason.

The parser and the analyzer both call it:

- The **parser** uses the expression, maps the reads to dependencies, and refuses the scenario on any
  violation. `RAUN017` then carries that reason and points at that expression.
- The **analyzer** reports the violations as `RAUN007` and uses the reads for `RAUN013`.

Because the same pass does both jobs, the analyzer and the generator cannot disagree about arguments.

The parser's definition map is keyed by `ILocalSymbol` instead of by name. This change is what makes
shadowing and branch-local locals correct. It also makes the LINQ unroll ordinary: the loop variable is
one more symbol substitution (`i` becomes a literal) on the in-tree lambda body. That replaces the detached
copy and the separate `semanticNode` lookup.

A step's identity (its uid) is still the arguments' tokens as written. The only change is that the LINQ
loop variable is replaced by its value, exactly as before. Qualification never reaches identity.

## Decisions

- **`private const` is refused ("make it internal") and not inlined.** Inlining a constant is exactly
  what C# does, but spelling an arbitrary constant as a literal (enums, `char`, `decimal`, negative
  numbers, typed `null`) is a second emission path. One reachability rule with one message is
  simpler to explain than "private is refused, except constants". This is cheap to revisit.
- **Tuple element names that come from a rewritten identifier (`(customer, 1)`) are not preserved.** At run
  time they carry nothing, and tuple conversions ignore names. Anonymous-object member names are
  preserved (`new { customer }` becomes `new { customer = … }`), because there the name is the member.

- **An awaited assignment to anything but a local (`Store = await Given.X()`) is refused** by both
  (RAUN002 / RAUN017). It used to lower as a bare step that silently dropped the write. The analyzer
  now builds its step-output set from the parser's own `Binding.FromAssignment`, not a mirror of it.

## Behaviour changes worth knowing

- **Display names bind named arguments by name.** Before this change they were bound by position, so
  `Then.Equal(expected: "x", actual: y)` now renders `{actual} equals x`, where before it put the
  value in the wrong slot.
- **Step identity is bound by symbol.** A lambda parameter that shadows the LINQ loop variable
  inside an unrolled call is no longer replaced by the element's value in the uid. Nobody writes
  that shape in practice; every other identity is byte-identical.

## Not done (deliberately)

- **Pure local values** (`var spec = new Spec { ... };`, then pass `spec`) are the issue's optional
  follow-up. Inline construction covers the use case. A pure local would have to be either inlined at
  each use, which evaluates it once per use (an opaque trap for `Guid.NewGuid()`), or become a synthetic
  node, which is new graph surface. That is left for Patrik to decide.
- **Purity is not enforced.** A method call in an argument (`Clock.GetUtcNow().AddDays(10)`) is allowed.
  Its evaluation point is exact and documented, and the analyzer cannot prove that a call is pure. The
  rule rejects only what would hide structure (steps, awaits) or corrupt shared state that the analyzer
  can see (writes).
- Usings declared inside a `namespace { }` block, and extension-method resolution in arguments, still
  depend on the scenario file's top-level usings, as they did before. Qualifying simple names fixes the
  common case (scenario-namespace types) without taking on extension lookup.
