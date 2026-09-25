# AGENTS.md

Guidance for coding agents (and humans) working in this repository.

## Project

**Raun** is a scenario-testing runtime for .NET, shipped as a Microsoft.Testing.Platform test framework: scenarios are written as plain
C# with a Given/When/Then DSL, a Roslyn source generator lowers each scenario into a step graph, a
DAG scheduler runs independent steps in parallel, resource roles trace what each step touches, and a
self-contained HTML report is produced per run. Old Norse *raun*: a trial, proof by experience.

| Path | What |
|---|---|
| `src/Raun` | The runtime: attributes, model, scheduler, run loop, resources, teardown, logging bridge, HTML report |
| `src/Raun.Generator` | Source generator + analyzer (`RAUN000`…) — netstandard2.0, ships inside `Raun` |
| `src/Raun.Mtp` | The MTP adapter: discovery, node reporter, filter translation, entry-point bootstrap |
| `src/Raun.Aspire` | Aspire AppHost bootstrap as the run's preflight node |
| `test/*` | xUnit v3 test projects (generator tests snapshot the emitted source; `test/Shared/Snapshot.cs`) |
| `samples/AppointmentTests` | Canonical end-to-end sample, simulated time, HTML report showcase |
| `samples/AspireAppointments` | Aspire end-to-end sample driving a real AppHost |
| `docs/superpowers/specs` | One design document per feature; each records rejected alternatives |
| `docs/RELEASING.md` | Versioning and release checklist |

## Build, test, verify

```bash
dotnet build Raun.slnx          # must end with 0 warnings: warnings are errors repo-wide
dotnet test Raun.slnx           # whole solution; the sample projects are test projects too
dotnet run --project samples/AppointmentTests/AppointmentTests.csproj
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj
```

- MTP rejects `--nologo` and `--filter` on the command line. Run whole projects;
  `--max-parallel-scenarios 1` makes a run sequential when you need deterministic output order.
- Snapshots: a changed snapshot leaves a `*.received.*` file next to the `*.verified.*` one under
  `test/Raun.Generator.Test/Snapshots`. Review the diff, then move received over verified — or re-run
  with `RAUN_ACCEPT_SNAPSHOTS=1` to have that done for you. The generator's snapshots are one file
  per generated file (`<test>#<hint name>.verified.cs`), and a verified file the run no longer
  produces fails the test rather than lingering.
- `AnalysisLevel=latest-all` with `TreatWarningsAsErrors`: expect CA/IDE rules to fail the build;
  fix the code rather than suppressing, unless the rule is genuinely wrong for the case (comment why).
- New analyzer rules must be added to `src/Raun.Generator/AnalyzerReleases.Unshipped.md` (RS2000).

## Conventions

- **Tests first.** Behavioural test, then the smallest change that makes it pass. Keep every guard
  and merge assertion when node counts move; do not weaken them.
- **Design before code** for anything architectural: a spec under `docs/superpowers/specs/` named
  `YYYY-MM-DD-<topic>-design.md`, with the alternatives considered and why they lost.
- **Commit messages:** conventional-commit style subject (`feat(scheduler): …`, `fix(generator): …`,
  `docs: …`), a body that explains the why. No `Co-Authored-By` or tooling trailers.
- **Versioning is tag-driven** (MinVer). Never add a `<Version>` anywhere. See `docs/RELEASING.md`.
- **Version control:** this is a colocated Jujutsu repository (`.jj/` next to `.git/`). Plain git
  works fine; a detached-`HEAD`-looking `git status` is normal here and needs no fixing.

## Things that look like bugs but are not

- A `Teardown` node exists in every scenario, even with nothing registered. It reports Passed.
- A not-taken `if` arm is reported as skipped with reason `not taken: <condition>`.
- Selecting one step in a run filter executes its predecessor closure (dependencies, merge sources,
  guard conditions) and teardown — nothing after it.
- `Microsoft.Testing.Extensions.CodeCoverage` must stay on a version compatible with the pinned MTP;
  a `TypeLoadException` after tests pass is a version mismatch, not a coverage bug.
- All Aspire work must happen inside the preflight delegate; building a `DistributedApplication`
  during discovery disposes an unstarted one and probes for a container runtime.
- Scenarios run concurrently by default (`--max-parallel-scenarios 1` makes a run sequential), so
  console and step-completion order across scenarios varies run to run. The HTML report lists
  scenarios in registration order regardless.
- A scenario header reading "waited 1.2 s for Database" is admission control, not slowness: an
  `[Uses<T>]` declaration held it back behind a running scenario.
- There is no `--filter`. The platform never had one — it belongs to `Microsoft.Testing.Extensions.VSTestBridge`,
  which xUnit and MSTest use for VSTest compatibility. Raun registers the platform's own
  `--treenode-filter` instead, plus `--filter-uid`, which is what an IDE sends for a single test.
- A `--treenode-filter` path has five segments, not the conventional four, because a step is a test
  node: `/{assembly}/{namespace}/{class}/{scenario}/{step}`. Segments are escaped minimally, not
  URL-encoded: only `%` and `/` are escaped, so a display name matches as typed, spaces and all.
  `--list-tests` prints step names with a numbering prefix (`4. When creating an appointment`) — filter
  on the name with that prefix stripped; scenario names don't appear in `--list-tests` at all, so get
  those from `[Scenario("...")]` or the HTML report instead.
- `--maximum-failed-tests` stops Raun launching new scenarios; scenarios already running finish and
  report. Nothing is killed mid-flight, so the number of failures can exceed the threshold by
  whatever was already in the air.
