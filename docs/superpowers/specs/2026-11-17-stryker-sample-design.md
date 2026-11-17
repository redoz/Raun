# Stryker sample: a real mutation run in the repo

## Why

Scenario granularity (one test per scenario under Stryker.NET) is covered by unit tests that fake
the environment and the filter. Nothing runs Stryker itself. Detection through
`STRYKER_MUTANT_FILE`, discovery as scenarios, the uid round trip between discovery, coverage and
mutant runs, and the override variable reaching the host are all unproven against the real tool.

## Goals

- A real `dotnet stryker` run against Raun scenarios, on every CI run.
- Proof that under Stryker the tests are scenarios, not steps.
- Proof that mutants are killed by scenarios for the right reason, not because every run errors.
- Proof that `RAUN_TEST_GRANULARITY=step` reaches the test host and that both granularities agree
  on every mutant.
- A user-facing example the Stryker section of `docs/reference/running-tests.md` can point at.

## Non-goals

- Mutation testing Raun's own source.
- Changing the flagship `AppointmentTests` sample.
- A mutation score target for the sample as a quality bar; the numbers are a means to the checks.

## Anti-goal

A check that passes when Stryker or the host is broken. A host that crashes on every mutant run
reports every mutant as killed, which looks like a perfect score.

## Approaches

- **A. New small sample (picked).** `samples/MutationTesting/` with a tiny library and Raun
  scenarios against it. Fast, isolated, doubles as documentation. Picked by Patrik.
- **B. Inside `AppointmentTests`.** Extract its domain code to a library. Realistic, but churns the
  flagship sample and makes the mutation run slow.
- **C. Test fixture under `test/Fixtures/`.** Validates, but gives users nothing to copy.

## Constraints

- Stryker mutates a source project referenced by the test project, in process. No existing sample
  has one, so the sample needs its own library.
- Stryker's MTP runner (`test-runner: mtp`) is marked preview, and the repo SDK is a .NET 11 RC.
  Either may fail in ways outside Raun's control.
- Runs in the existing CI job, as blocking steps (Patrik's decision).
- Restore is locked: every new project needs a `packages.lock.json`.

## Design

**Layout**

```
samples/MutationTesting/
  Booking/                 class library, net10.0, plain C#, no Raun reference
  Booking.Tests/           Raun scenarios, generated host (same csproj shape as GettingStarted)
  stryker-config.json      test-runner mtp, project Booking.csproj, json reporter
  README.md
```

Both projects join `Raun.slnx`, so `dotnet test Raun.slnx` runs the scenarios as an ordinary sample.

**Library.** A few small pure rules with branches and arithmetic (slot availability, capacity,
overlap, a fee), enough to produce a few dozen mutants. One line is deliberately not asserted by
any scenario and is marked as such in a comment: the sentinel.

**Scenarios.** A handful of multi-step scenarios, at least one with three or more steps where a
late step asserts on a value an early step computed, so step granularity would re-run earlier steps.

**Tool.** `.config/dotnet-tools.json` pins `dotnet-stryker`; CI and users run `dotnet tool restore`.

**Runs.** Two, into separate output folders:

1. Default: no Raun variable set. Raun must detect Stryker and choose scenario granularity.
2. `RAUN_TEST_GRANULARITY=step`.

**Check.** A node script (the repo already runs node checks in CI), `test/Stryker/check-report.cjs`,
reads both `mutation-report.json` files and fails on any of:

- run 1 lists a test whose name is not a scenario display name, or lists a different number of
  tests than the sample has scenarios;
- run 2 lists scenario-level tests instead of steps;
- a mutant outside the sentinel line is not killed in run 1;
- the sentinel mutant is killed in either run (guards the anti-goal);
- any mutant's status differs between the two runs.

**CI.** Three steps in the existing job, after `Test`: tool restore, the two Stryker runs, the
check. The reports are uploaded as an artifact.

**Docs.** `docs/reference/running-tests.md` links the sample from its Stryker section;
`samples/README.md` gains a row and the command.

## Order of work

1. Feasibility first: library, scenarios, config, one local Stryker run. If the preview runner or
   the RC SDK blocks it, stop and report what failed before any CI change.
2. Check script, second run.
3. CI steps, docs.

## Assumptions to verify in step 1

- A released `dotnet-stryker` has the MTP runner and runs on the pinned SDK.
- Stryker test hosts inherit the parent environment, so `RAUN_TEST_GRANULARITY` reaches them.
- The JSON report carries test names (mutation-testing-elements `testFiles`).

## Left to the builder

Library content, scenario wording, the Stryker version, report paths, and how the sentinel line is
identified in the report (file plus line, or a marker the script searches for).
