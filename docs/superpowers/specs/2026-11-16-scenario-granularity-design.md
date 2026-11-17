# Scenario-as-one-test under Stryker — design

Date: 2026-11-16. Size: small change (adapter only).

## Why

Raun reports every step as its own test. Stryker.NET's coverage pass runs every
discovered test alone, one request per test. Selecting one step runs its predecessor
closure, so a 10-step linear scenario costs 55 step executions instead of 10, and
each step is credited with the coverage of everything before it.

Reporting a scenario as a single test removes both problems: one request per
scenario, each step executed once, coverage attributed to the scenario.

## Goals

- Under Stryker, each scenario is one discovered test and one result.
- Works with no setup on the user's side.
- Normal runs (console, IDE, CI) are unchanged: one test per step.

## Non-goals

- No change to the core runtime, the scheduler, or the HTML report (which stays
  per step in both modes).
- No scenario node as a parent of step nodes.
- No forcing of scenario or step parallelism. Stryker's per-test coverage sends
  one test per request, so one scenario runs at a time there anyway.

## Anti-goal

A session that mixes modes. Stryker maps mutants to tests by uid across its
discovery, coverage and mutant runs; the mode must be fixed for the life of the
test host.

## Approaches considered

1. **Auto-detect plus override — picked.** Scenario mode when the host was started
   by Stryker; a `RaunTestApplication` option and an environment variable override
   either way. Zero setup, with an escape hatch and a way to use the mode elsewhere.
2. Explicit only. Predictable, but forgetting the setting gives slow runs and no
   error.
3. Auto-detect only. No escape hatch; only testable by faking Stryker's variable.

Rejected outright: a command-line option as the only switch (Stryker passes no
extra arguments to the host), and a granularity concept in core (breaks the
core/adapter boundary; this is purely how results are presented to the platform).

## Design

### The switch: `TestGranularity`

`Raun.Mtp` gets a public `TestGranularity { Step, Scenario }` and
`RaunTestApplication.RunAsync` gets a new optional parameter
`TestGranularity? testGranularity = null` — the suite's own choice, in code, next
to `maxParallelScenarios`. `null` (the default) means "decide automatically".

The effective value is resolved once, when the framework is constructed:

1. Environment `RAUN_TEST_GRANULARITY` = `step` or `scenario` (case-insensitive)
   wins. Any other non-empty value fails startup with a message naming the two
   valid values — a typo must not silently fall back. This is the per-run
   override, the role a command-line option plays for the parallelism settings;
   it is an environment variable because Stryker passes no arguments to the host.
2. Otherwise the `testGranularity` argument, when not `null`. An explicit `Step`
   here opts the suite out of auto-detection.
3. Otherwise `STRYKER_MUTANT_FILE` set and non-empty means `Scenario`. Stryker's
   MTP runner sets this on every test host it starts.
4. Otherwise `Step`.

The resolver takes the code option and a `Func<string, string?>` for the
environment lookup, so it is unit-testable without touching the real environment.

The generated `Program.cs` keeps calling `RunAsync` without the argument, so
"just add the package" suites get auto-detection. No `--test-granularity`
command-line option for now (my call: nothing needs it yet).

### Discovery

In `Scenario` mode `RaunDiscoverer` emits one `TestNode` per scenario:

- `Uid` = `ScenarioId` (no step suffix, so it can never collide with a step uid).
- `DisplayName` = the scenario's display name.
- Same identity and tag properties step nodes carry today
  (`ScenarioTestIdentity`, `ScenarioNodePath.Tags`).
- File location = the first non-synthetic step's source location, when known.
- A scenario is emitted when the selector is null or matches at least one of its
  non-synthetic steps.

Preflight goes through the same path: its one-step definition becomes one node.

### Selection

In `Scenario` mode a selected scenario always runs whole.

- Uid list filter: a scenario is selected when the list contains its `ScenarioId`.
- Tree-node filter: a scenario is selected when the filter matches any of its steps.

Both are expressed as a `NodeSelector` wrapper that widens "any step matches" to
"every step matches", so `RunLoop` and the scheduler see an ordinary selector and
need no change.

### Results

A second sink, `ScenarioMtpReportSink`, replaces `MtpReportSink` in `Scenario`
mode. `HtmlReportSink` is added exactly as today.

- `ScenarioStarted` publishes the node as in-progress.
- `ScenarioFinished` publishes the terminal node, folded from the step results
  (synthetic steps ignored):

  | Steps | Scenario state |
  |---|---|
  | any timed out | timeout, from the first such step in execution order |
  | else any failed | failed or error, from the first such step, using the existing assertion-vs-error mapping |
  | else at least one passed | passed |
  | else (nothing ran) | skipped, with the first skip reason |

  Not-taken branches and steps skipped behind a passing path never count against
  the scenario.
- Failure text names the step: the exception is reported as is, and the node's
  standard output lists every step as `{number}. {name} — {status}` followed by
  that step's log lines, so the failing step is visible in Stryker's logs.
- Timing: earliest step start to latest step end.
- Attachments from all steps are attached to the scenario node.

The state-mapping and output helpers now in `MtpReportSink` are shared between
the two sinks, not copied.

### Docs

`docs/reference/running-tests.md` gets a short "Mutation testing with Stryker"
section: what changes, the override variable, and that the HTML report still
shows steps.

## Testing

Behavioural tests first, in `test/Raun.Mtp.Test`:

- granularity resolution: default, Stryker variable, code option both ways
  (including explicit `Step` under Stryker), environment override beating the
  code option, bad value
- discovery: one node per scenario, uid and name, selector narrowing
- run: each fold row above; a not-taken branch does not fail or skip the scenario
- uid filter with a scenario uid runs every step of that scenario exactly once
- step mode output is byte-for-byte what it is today (existing tests stay green)

## Left to the builder

- Exact wording of the output lines and the startup error.
- Whether the shared helpers live in a static class or a small base sink.
- Not verified here: an end-to-end run under real Stryker. Worth one manual run
  against a sample before tagging.
