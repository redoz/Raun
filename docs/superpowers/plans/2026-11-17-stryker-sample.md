# Stryker Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A sample that runs real Stryker.NET against Raun scenarios in CI and proves scenario granularity works end to end.

**Architecture:** A tiny `Booking` library is mutated; `Booking.Tests` holds Raun scenarios against it. CI runs Stryker twice (default, then `RAUN_TEST_GRANULARITY=step`) and a node script compares the two JSON reports.

**Tech Stack:** .NET 10 target, Raun.Mtp + Raun.Generator (project references), `dotnet-stryker` local tool with `test-runner: mtp`, node (`node --test`) for the report check.

**Spec:** `docs/superpowers/specs/2026-11-17-stryker-sample-design.md`

## Global Constraints

- Version control is Jujutsu. Commit with `jj commit -m "..."`. Never run mutating `git` commands. No trailers in commit messages. Do not push and do not move `main`.
- Warnings are errors (`AnalysisLevel` 10.0-all) in every project, samples included.
- Restore is locked: each new project gets a committed `packages.lock.json`.
- Sample projects target `net10.0` only, like the other samples.
- No Raun reference in the `Booking` library.
- No old project names anywhere.
- If Stryker itself cannot run (preview MTP runner, SDK), stop after Task 2 and report; do not touch CI.

## Review Focus

- Report with zero mutants: check must fail, not pass vacuously. (Task 3)
- Report with zero tests: check must fail. (Task 3)
- Every mutant killed including the sentinel (broken host): check must fail. (Task 3)
- Report file missing or not JSON: script exits non-zero with the path in the message. (Task 3)
- Windows versus Linux path separators in report file keys: mutants must still pair up between runs. (Task 3)

---

### Task 1: The sample library and its scenarios

**Files:**
- Create: `samples/MutationTesting/Booking/Booking.csproj`, `samples/MutationTesting/Booking/BookingRules.cs`
- Create: `samples/MutationTesting/Booking.Tests/Booking.Tests.csproj`, `samples/MutationTesting/Booking.Tests/BookingScenarios.cs`
- Create: both `packages.lock.json` files
- Modify: `Raun.slnx` (add both under `/samples/`)

**Interfaces:**
- Produces: library API below; scenario display names; the marker comment `// stryker-sentinel` on exactly one source line.

- [ ] **Step 1: Create `Booking.csproj`** — plain class library, `net10.0`, `IsPackable` false, no package or project references.

- [ ] **Step 2: Write `BookingRules.cs`**

```csharp
namespace Booking;

public sealed record Slot(int StartHour, int Capacity);

public static class BookingRules
{
    // Open 08:00 inclusive to 18:00 exclusive.
    public static bool IsWithinOpeningHours(int hour);

    public static bool HasRoom(Slot slot, int alreadyBooked);

    // 50 per started half hour; members pay 80%.
    public static decimal Fee(int minutes, bool isMember);

    // Called by a scenario, never asserted on. Its return expression carries the marker comment.
    public static string Describe(Slot slot);
}
```

`Describe` returns an interpolated or literal string on one line ending `// stryker-sentinel`, so Stryker produces a string mutant there.

- [ ] **Step 3: Create `Booking.Tests.csproj`** — copy the shape of `samples/GettingStarted/GettingStarted.csproj` (generated host, same properties and references), plus a `ProjectReference` to `../Booking/Booking.csproj`.

- [ ] **Step 4: Write `BookingScenarios.cs`** — `Scenarios<NoWorld>` with Given/When/Then step classes. Requirements:
  - at least three scenarios, each three or more steps;
  - a late step asserts on a value an early step computed;
  - boundary inputs so every non-sentinel mutant can die: hours 7, 8, 17, 18; booked equal to and one below capacity; fees for 30, 31 and 60 minutes, member and non-member;
  - one step calls `BookingRules.Describe` and only logs the result (no assertion).

- [ ] **Step 5: Add both projects to `Raun.slnx`, restore to generate lock files**

Run: `dotnet restore Raun.slnx`
Expected: two new `packages.lock.json`; then `dotnet restore Raun.slnx --locked-mode` succeeds.

- [ ] **Step 6: Run the sample**

Run: `dotnet run --project samples/MutationTesting/Booking.Tests`
Expected: all steps pass, 0 failed, 0 warnings in build.

- [ ] **Step 7: Commit** — `jj commit -m "samples: a booking library with scenarios for mutation testing"`

---

### Task 2: Stryker runs locally (feasibility gate)

**Files:**
- Create: `.config/dotnet-tools.json`
- Create: `samples/MutationTesting/stryker-config.json`
- Modify: `.gitignore` (ignore `StrykerOutput/`)

**Interfaces:**
- Produces: report paths `artifacts/stryker/scenario/reports/mutation-report.json` and `artifacts/stryker/step/reports/mutation-report.json`; the two commands in Step 3 and Step 5, used verbatim by CI.

- [ ] **Step 1: Pin the tool**

Run: `dotnet new tool-manifest` then `dotnet tool install dotnet-stryker`
Expected: `.config/dotnet-tools.json` names an exact `dotnet-stryker` version. It must accept `--test-runner mtp`; if the latest stable does not, use the newest prerelease that does and say so in the commit body.

- [ ] **Step 2: Write `stryker-config.json`**

```json
{
  "stryker-config": {
    "project": "Booking.csproj",
    "test-projects": ["Booking.Tests/Booking.Tests.csproj"],
    "test-runner": "mtp",
    "reporters": ["json", "progress"]
  }
}
```

- [ ] **Step 3: Scenario run**

Run (from `samples/MutationTesting`): `dotnet stryker -f stryker-config.json -O ../../artifacts/stryker/scenario`
Expected: completes; `reports/mutation-report.json` exists.

- [ ] **Step 4: Verify the three assumptions by reading the report**
  - `testFiles` lists tests, and their `id` values are Raun uids: exactly one per scenario, none containing `:`.
  - Every mutant outside the sentinel line is `Killed` (or `Timeout`, `CompileError`, `Ignored`). If one survives, add the missing boundary assertion to Task 1's scenarios, or simplify the rule; do not add Stryker disable comments.
  - The sentinel line's mutant is `Survived`.

- [ ] **Step 5: Step run**

Run: same command with `RAUN_TEST_GRANULARITY=step` set and `-O ../../artifacts/stryker/step`
Expected: test ids are `{scenarioId}:{stepId}`; mutant statuses match Step 3.

- [ ] **Step 6: Gate.** If Step 3 or 5 cannot be made to work for reasons in Stryker or the SDK, stop here: commit what exists, report the exact failure, skip Tasks 3 and 4. If test ids are not Raun uids, report that and continue with names instead of ids in Task 3.

- [ ] **Step 7: Commit** — `jj commit -m "samples: run Stryker.NET against the booking scenarios"`

---

### Task 3: The report check

**Files:**
- Create: `test/Stryker/check-report.cjs`
- Test: `test/Stryker/check-report.test.cjs`

**Interfaces:**
- Consumes: the two report paths from Task 2; the `// stryker-sentinel` marker.
- Produces: `check(scenarioReport, stepReport) -> string[]` (empty means pass), exported; CLI `node test/Stryker/check-report.cjs <scenario-report> <step-report>` printing each failure and exiting 1 when any.

Report shape (mutation-testing-elements): `files[path] = { source, mutants: [{ mutatorName, replacement, status, location: { start: { line, column } } }] }`, `testFiles[path].tests = [{ id, name }]`.

Rules for `check`:
- sentinel line per file = 1-based line of `source` containing `stryker-sentinel`;
- mutant key = file path with `\` replaced by `/` + start line + start column + mutatorName + replacement;
- passing statuses for non-sentinel mutants: `Killed`, `Timeout`, `CompileError`, `Ignored`.

- [ ] **Step 1: Write failing tests** with small inline report objects built by a helper `report({ tests, mutants })`:

```js
test('passes for a healthy pair of runs', ...)            // check(...) deepEqual []
test('fails when the scenario run lists step uids', ...)   // id 'scn:a' in run 1
test('fails when the step run lists scenario uids', ...)   // id without ':' in run 2
test('fails when scenario ids differ between runs', ...)   // run 2 prefixes before last ':' != run 1 ids
test('fails when a non-sentinel mutant survives', ...)
test('fails when the sentinel mutant is killed', ...)      // in either run
test('fails when no sentinel line exists', ...)
test('fails when a mutant status differs between runs', ...)
test('fails on zero mutants', ...)
test('fails on zero tests', ...)
test('pairs mutants across path separators', ...)          // 'a\\b.cs' vs 'a/b.cs', expect []
test('cli exits 1 naming a missing report path', ...)      // spawnSync, status 1, stderr includes path
```

- [ ] **Step 2: Run to see them fail**

Run: `node --test test/Stryker/check-report.test.cjs`
Expected: FAIL, module not found.

- [ ] **Step 3: Implement `check` and the CLI** in `check-report.cjs` (CLI only when `require.main === module`).

- [ ] **Step 4: Run tests, then the real reports**

Run: `node --test test/Stryker/check-report.test.cjs`
Expected: all pass.
Run: `node test/Stryker/check-report.cjs artifacts/stryker/scenario/reports/mutation-report.json artifacts/stryker/step/reports/mutation-report.json`
Expected: exit 0, no output besides a one-line summary.

- [ ] **Step 5: Prove it bites.** Temporarily add an assertion on `Describe`'s result, rerun the scenario Stryker run and the check: expect exit 1 naming the sentinel. Revert.

- [ ] **Step 6: Commit** — `jj commit -m "test: check Stryker reports for scenario and step granularity"`

---

### Task 4: CI and docs

**Files:**
- Modify: `.github/workflows/ci.yml` (existing job, after the `Test` step)
- Modify: `docs/reference/running-tests.md`, `samples/README.md`
- Create: `samples/MutationTesting/README.md`

- [ ] **Step 1: Add CI steps** to the existing job, in this order, after `Test`:
  1. `dotnet tool restore`
  2. `Mutation testing sample (scenario tests)`: Task 2 Step 3 command, `working-directory: samples/MutationTesting`
  3. `Mutation testing sample (step tests)`: Task 2 Step 5 command with `env: RAUN_TEST_GRANULARITY: step`
  4. `Check mutation reports`: `node --test test/Stryker/check-report.test.cjs` then the CLI on both reports
  5. Upload `artifacts/stryker` as artifact `mutation-reports`, `if: always()`, action pinned by commit sha like the existing upload steps.

- [ ] **Step 2: Docs.** `samples/MutationTesting/README.md`: what the sample shows, the two commands, what the sentinel is for. `samples/README.md`: one table row plus the command. `docs/reference/running-tests.md`: link the sample from the Stryker section.

- [ ] **Step 3: Verify**

Run: `dotnet build Raun.slnx -c Release` then `dotnet test Raun.slnx -c Release --no-build`
Expected: 0 warnings, 0 failed.
Run: each new CI command locally, in order.
Expected: all exit 0.

- [ ] **Step 4: Commit** — `jj commit -m "ci: run the Stryker sample and check its reports"`
