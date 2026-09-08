# Filling in the Microsoft.Testing.Platform surface — Design

- **Date:** 2026-09-08
- **Status:** Approved in brainstorm (Patrik, 2026-09-08: "use the standard one no? just try to
  implement as much of MTP as we can so we provide a nice experience for user").
- **Scope:** `src/Raun.Mtp` (filter selection, capabilities, builder registrations),
  `Directory.Packages.props` (platform pin). Tests in `test/Raun.Mtp.Test`. Docs: README, AGENTS.md.
- **Non-goals:** a Raun-invented filter syntax; VSTest-bridge compatibility; trait attributes;
  `--fail-fast`; TRX or JUnit reporters; anything in `src/Raun` or the generator.

## Why

Raun implements `ITestFramework` natively and registered nothing beyond its own two options, so
users get neither of the platform's filter experiences. The investigation that prompted this design
established three facts, each verified against the shipped assemblies rather than assumed:

- **The platform has no `--filter`.** Its filter options are `--filter-uid` and `--treenode-filter`.
  `--filter` belongs to `Microsoft.Testing.Extensions.VSTestBridge`, which xUnit and MSTest use for
  VSTest compatibility. xUnit's `mtp-v2` package instead replaces the command line wholesale with
  xUnit's own runner CLI.
- **`--treenode-filter` is opt-in, not capability-gated.** `TestApplicationBuilderExtensions`
  exposes a public (experimental) `AddTreeNodeFilterService(IExtension)`; the option provider it
  registers has no enablement check. TUnit — a native MTP framework, the closest analogue to Raun —
  calls it and implements no filter provider of its own. Raun simply never called it.
- **`--maximum-failed-tests` is opt-in *and* capability-gated.**
  `AddMaximumFailedTestsService(IExtension)` registers it, and the provider's enablement check
  requires the framework to declare `IGracefulStopTestExecutionCapability`.

Both helpers already exist in the pinned platform version, so the filter work does not depend on the
version bump; the bump is independent and buys terminal options.

## Decisions

### Use the platform's filter, not our own

Raun registers `AddTreeNodeFilterService` and handles the `TreeNodeFilter` the platform hands to the
framework. No Raun-specific filter option and no Raun-specific syntax: a user who knows
`--treenode-filter` from any other MTP framework knows Raun's.

**Rejected: registering our own `--filter`.** It would have avoided the experimental API and matched
the name people type from xUnit and VSTest habit, but it invents a second dialect of an existing
feature, and it would collide in spirit with the platform option the same runner already documents.

### Node paths are five segments

```
/{assembly}/{namespace}/{class}/{scenario}/{step}
```

Each segment is escaped minimally, not URL-encoded: only `%` (as `%25`, first, so the escape stays
unambiguous) and `/` (as `%2F`) are escaped, and everything else — spaces included — is left literal.
The platform's own documentation calls the path "segment URL encoded", but that wording is nominal:
the shipped matcher splits the filter on `/` and regex-matches the raw segment text, decoding nothing.
The only real structural requirement is that a segment cannot itself contain a literal `/`, since that
would split it into extra path segments (and the platform rejects a literal slash inside a filter
segment outright) — which is exactly what the minimal escaping prevents.

The segments come from data Raun already has, via the same derivation `ScenarioTestIdentity` uses
for `TestMethodIdentifierProperty`, so filtering and IDE grouping agree:

| Segment | Source |
|---|---|
| assembly | entry assembly's simple name (`GetName().Name`), not the full name the identity property uses |
| namespace | `ScenarioDefinition.MethodName` split, the part ahead of the type |
| class | `ClassDisplayName` when set, else the derived type name |
| scenario | `ScenarioDefinition.DisplayName` |
| step | the step's display name **without** the numbering prefix discovery adds |

The step segment drops the `1.` / `2.1` prefix on purpose. The number is positional, so a filter
keyed on it would silently select a different step the moment one is inserted above it. The name is
what the author wrote; the number is presentation.

Five levels rather than the conventional four because in Raun a **step** is a test node, which is the
framework's whole premise. `/*/*/*/booking/*` selects a scenario, `/*/*/*/booking/*reminder*`
selects one step inside it. A matched step keeps today's execution semantics: it runs with its
predecessor closure and teardown, because the match feeds the existing target mechanism rather than
replacing it.

**Rejected: four segments with the scenario as leaf.** Conventional, but it makes a step
unaddressable from the command line and contradicts per-step nodes.
**Rejected: four segments with a `scenario.step` compound leaf.** Conventional depth, but the leaf
becomes awkward to wildcard and no longer matches any name the user has seen.
**Rejected: full URL-encoding (`Uri.EscapeDataString`).** Tried first, and it does satisfy the
platform's nominal "segment URL encoded path" contract. But the platform never decodes the path — its
matcher regex-matches the raw segment text — so full encoding bought nothing structurally beyond what
minimal escaping already gives, while forcing a user to percent-encode every space (`%20` for each one)
before a filter would match. That silently broke the documented workflow of copying a name straight out
of `--list-tests` into a `--treenode-filter`: the copied name, spaces and all, would match nothing.

### Nodes expose two filterable properties

The bag passed to `MatchesFilter` carries `TestMetadataProperty` entries — the only property type
the platform's matcher inspects — for `Phase` (Given/When/Then or a custom marker) and `Scenario`
(the scenario display name). That makes `/*/*/*/*/*[Phase=When]` work without Raun inventing a trait
system. The bag is built per node and stays empty of anything else; a filter with no property
expression never inspects it.

### Selection becomes one abstraction

Today `RaunTestFramework.ReadUidFilter` reduces a filter to a uid set, and both discovery and
execution consume that set. A tree filter cannot reduce to a uid set without first enumerating every
node, and both call sites need the same answer, so the reduction becomes a **node selector**: an
object answering "does this scenario's step match?".

- A null or no-op filter selects everything (the selector is null, exactly as the uid set is today).
- `TestNodeUidListFilter` selects by uid, unchanged behaviour.
- `TreeNodeFilter` selects by path and properties.

`RaunRunLoop.SelectScenarios` and `SelectTargets` take the selector instead of a uid set. This keeps
one definition of "selected" for discovery, execution, and the predecessor-closure expansion.

### An unrecognized filter no longer fails the run

The current switch throws `ArgumentException` for any filter type it does not know. Unreachable
today, but it turns a future platform filter into a hard run failure. It becomes: run everything, and
log a warning through the platform logger naming the type. A filter the framework cannot honour is a
reason to over-select, never to abort — the same instinct behind reporting a broken report sink
without failing the run.

### Graceful stop drives the existing halt path

`IGracefulStopTestExecutionCapability.StopTestExecutionAsync` is implemented by a small capability
that sets a **stop signal** the run loop already knows how to honour: the concurrency work gave
`LaunchAsync` a `halted` flag that stops admitting new scenarios while in-flight ones drain. Stopping
therefore means setting that flag, not new machinery, and the semantics under concurrency are the
honest ones: no scenario is killed mid-flight, and the run still publishes `RunFinished`.

The signal is a tiny mutable object created in the bootstrap, handed to both the capability and the
framework, because capabilities are constructed by a different factory than the framework and the two
need to meet. Registering `AddMaximumFailedTestsService` then lights up `--maximum-failed-tests`,
whose provider asks the platform to stop once the threshold is crossed.

### The banner names Raun

`IBannerMessageOwnerCapability` returns Raun's name and informational version, so the header
identifies the framework rather than only the platform. Cosmetic and cheap; TUnit does the same.

### Platform pin moves to 2.4.0

Independent of everything above, and worth taking for options the platform added since the pin:
progress and ansi control, a `minimal` output mode, slowest-test and flaky-test reporting, and
per-result-type output filtering. `Microsoft.Testing.Extensions.CodeCoverage` must move in lockstep —
AGENTS.md already records that a mismatch surfaces as a `TypeLoadException` after tests pass — and
the Aspire sample is the project that proves it, since it is the only one referencing coverage.

## Experimental API

`AddTreeNodeFilterService`, `AddMaximumFailedTestsService` and `TreeNodeFilter` are all marked
`[Experimental("TPEXP")]`. Raun already suppresses TPEXP for `NopFilter`. Each new suppression is
scoped to the smallest region and carries a comment naming what it is for, so a future platform
version that changes these is easy to find. This is a deliberate bet: the alternative is inventing a
parallel filter surface, which is worse.

## Testing

- **Path building:** a unit test per segment source, including a display name containing `/`, a
  scenario in the global namespace, and a `[DisplayName]` class override.
- **Selection:** the selector matches and rejects for uid filters and tree filters; a step match
  expands to its predecessor closure and teardown; a scenario-level match takes every step; a
  property expression on `Phase` selects only that phase.
- **Unknown filter:** a fake filter type runs everything and logs, rather than throwing.
- **Graceful stop:** a stop mid-run stops admitting scenarios, drains in-flight ones, and publishes
  `RunFinished` exactly once.
- **Registration:** the sample's `--help` lists `--treenode-filter` and `--maximum-failed-tests`;
  an end-to-end run with a tree filter selects the expected steps. This is the test that would have
  caught the original gap, so it belongs in the suite rather than only in a manual check.
- The whole solution stays at 0 warnings, and both samples keep their current totals except where a
  filter is applied deliberately.

## Deferred

- `--fail-fast`, TRX and JUnit reporters, and a JSON output mode: all things TUnit registers that
  Raun could add later, none of them asked for here.
- `IGracefulStopTestExecutionResultCapability`, the newer variant that reports whether the stop was
  honoured. Only in 2.4.0, and the boolean adds nothing until Raun can refuse a stop.
- A trait attribute for scenarios and steps, which would make property filtering richer than the two
  built-in keys.
