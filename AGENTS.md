# AGENTS.md

Guidance for coding agents and humans working in this repository.

## Project

Raun is a scenario-testing runtime for .NET. It ships as a Microsoft.Testing.Platform test framework.
You write scenarios as plain C# with a Given/When/Then DSL. A Roslyn source generator lowers each
scenario into a directed acyclic graph of steps, and a scheduler runs independent steps in parallel.
Resource roles record what each step touches. Each run produces a self-contained HTML report.
The name is Old Norse *raun*, meaning a trial, or proof by experience.

| Path | What |
|---|---|
| `src/Raun` | The runtime: attributes, model, scheduler, run loop, resources, teardown, logging bridge, HTML report |
| `src/Raun.Generator` | Source generator and analyzer (`RAUN000`…). Targets netstandard2.0 and ships inside `Raun` |
| `src/Raun.Mtp` | The MTP adapter: discovery, node reporter, filter translation, entry-point bootstrap |
| `src/Raun.Aspire` | Aspire AppHost bootstrap as the run's preflight node |
| `test/*` | xUnit v3 test projects. Generator tests snapshot the emitted source with `test/Shared/Snapshot.cs` |
| `samples/AppointmentTests` | Canonical end-to-end sample with simulated time. It demonstrates the HTML report |
| `samples/AspireAppointments` | Aspire end-to-end sample that drives a real AppHost |
| `docs/superpowers/specs` | One design document per feature. Each records rejected alternatives |
| `docs/RELEASING.md` | Versioning and release checklist |

## Build, test, verify

```bash
dotnet build Raun.slnx          # must end with 0 warnings: warnings are errors repo-wide
dotnet test Raun.slnx           # whole solution; the sample projects are test projects too
dotnet run --project samples/AppointmentTests/AppointmentTests.csproj
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj
```

- MTP rejects `--nologo` and `--filter` on the command line. Run whole projects.
  `--max-parallel-scenarios 1` makes a run sequential when you need deterministic output order.
- A changed snapshot leaves a `*.received.*` file next to the `*.verified.*` one under
  `test/Raun.Generator.Test/Snapshots`. Review the diff, then move received over verified.
  Or re-run with `RAUN_ACCEPT_SNAPSHOTS=1`, which does the move for you. The generator's snapshots
  are one file per generated file, named `<test>#<hint name>.verified.cs`. A verified file that the
  run no longer produces fails the test instead of staying behind.
- `AnalysisLevel=10.0-all` with `TreatWarningsAsErrors` makes CA and IDE rule violations fail the
  build. Fix the code rather than suppressing the rule, unless the rule is wrong for the case.
  In that case, add a comment that says why.
- Add new analyzer rules to `src/Raun.Generator/AnalyzerReleases.Unshipped.md`. Rule RS2000 reports
  a rule missing from that file.

## Conventions

- **Tests first.** Write a behavioural test, then the smallest change that makes it pass. Keep every
  guard and merge assertion when node counts move, and do not weaken them.
- **Design before code.** For anything architectural, write a spec under `docs/superpowers/specs/`
  named `YYYY-MM-DD-<topic>-design.md`. The spec lists the alternatives considered and why they lost.
- **Commit messages.** Use a conventional-commit subject such as `feat(scheduler): …`,
  `fix(generator): …`, or `docs: …`, and a body that explains the why. Add no `Co-Authored-By` or
  tooling trailers.
- **Versioning is tag-driven.** MinVer derives the version from tags. Never add a `<Version>`
  anywhere. See `docs/RELEASING.md`.
- **Version control.** This is a colocated Jujutsu repository, with `.jj/` next to `.git/`. Plain git
  works. A `git status` that looks like a detached `HEAD` is normal here and needs no fixing.

## Things that look like bugs but are not

- A `Teardown` node exists in every scenario, even with nothing registered. It reports Passed.
- A `Setup` node exists in every scenario too. It creates the scenario's world, including `NoWorld`.
  Every step depends on it. It is unnumbered, so `--list-tests` shows `Setup` then `1. …`. A
  failed Setup skips every step with `dependency failed: Setup`.
- `Scenarios<TWorld>.Steps<T>()` throws if called. Phase and group properties exist for the compiler
  and the IDE. The generator binds a step class by its type and never runs them.
- A not-taken `if` arm is reported as skipped with reason `not taken: <condition>`.
- Selecting one step in a run filter executes its predecessor closure and teardown, and nothing after
  it. The predecessor closure is the step's dependencies, merge sources, and guard conditions.
- `Microsoft.Testing.Extensions.CodeCoverage` must stay on a version compatible with the pinned MTP.
  A `TypeLoadException` after tests pass is a version mismatch, not a coverage bug.
- All Aspire work must happen inside the preflight delegate. Building a `DistributedApplication`
  during discovery disposes an unstarted one and probes for a container runtime.
- Scenarios run concurrently by default, so console and step-completion order across scenarios
  varies from run to run. `--max-parallel-scenarios 1` makes a run sequential. The HTML report lists
  scenarios in registration order regardless.
- A scenario header reading "waited 1.2 s for Database" is admission control, not slowness. An
  `[Uses<T>]` declaration made the scenario wait for a running scenario.
- There is no `--filter`. The platform never had one. It belongs to `Microsoft.Testing.Extensions.VSTestBridge`,
  which xUnit and MSTest use for VSTest compatibility. Raun registers the platform's own
  `--treenode-filter` instead, plus `--filter-uid`, which is what an IDE sends for a single test.
- A `--treenode-filter` path has five segments, not the conventional four, because a step is a test
  node: `/{assembly}/{namespace}/{class}/{scenario}/{step}`. Raun escapes segments minimally and does
  not URL-encode them. It escapes only `%` and `/`, so a display name matches as typed, spaces included.
  `--list-tests` prints step names with a numbering prefix, such as `4. When creating an appointment`.
  Filter on the name with that prefix stripped. Scenario names don't appear in `--list-tests` at all,
  so get them from `[Scenario("...")]` or the HTML report instead.
- `--maximum-failed-tests` stops Raun from launching new scenarios. Scenarios already running finish
  and report. Raun stops no running scenario, so the failure count can exceed the threshold by the
  failures of scenarios that were already running.
