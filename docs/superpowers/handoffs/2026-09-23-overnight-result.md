# Overnight result — 2026-09-23: Raun pilot-readiness

All eleven work items from the handoff landed, one commit each, on `main`, item 9 in two commits.
One stretch sub-item was deliberately not done; it is under **Not done** with the reasoning.

Baseline at 53b630d: 687 tests (686 pass, 1 expected skip), 0 warnings.
Now: **720 tests (719 pass, 1 expected skip), 0 warnings**, all three packages pack, locked restore
clean.

## What landed

| # | Commit | What |
|---|---|---|
| 1 | `feat(scheduler): enforce the scenario-wide timeout` | `[Scenario(Timeout = …)]` was read, emitted, and ignored. |
| 2 | `docs(mtp): document the configure route for platform extensions` | TRX/coverage/any MTP extension, documented and proven. |
| 3 | `build: fail a consumer build on an SDK below the floor` | RAUN018 instead of a silent green run of zero tests. |
| 4 | `fix(mtp): write step attachments into the run's results directory` | Out of `%TEMP%`, plus the PII paragraph in README. |
| 5 | `fix(mtp): recognize FluentAssertions, Shouldly and MSTest assertion failures` | Assertions report Failed, not Error. |
| 6 | `docs: describe the HTML report the way it actually looks` | The Gantt/resource-lane promise is gone. |
| 7 | `docs: tell a migrating suite to start sequential` | `maxParallelScenarios: 1` first, raise later. |
| 8 | `docs: scenarios must live in the test executable` | The class-library limitation, stated. |
| 9 | `perf(generator): give the lowered IR value equality so the editor can cache` + `perf(generator): emit one file per scenario` | Keystrokes stop re-emitting; an edit touches one scenario's file. |
| 10 | `fix(generator): hash a step's uid from what it is, not where it sits` | Inserting a step no longer renames the rest. |
| 11 | `ci: lock the dependency graph and pin the actions` | Lock files, SHA pins, determinism, Dependabot. |

## Decisions taken while you slept

**Item 1 — the timeout lives in the scheduler, not in RunLoop.** The handoff said to put
`CancelAfter(definition.Timeout)` on the per-scenario CTS in `RunLoop.RunOneAsync` and let the
scheduler's existing cancellation handling do the rest. That would not have worked: a step body that
never observes its token — `await Task.Delay(10_000)` with no token, which is exactly the handoff's
own test case — keeps the scheduler waiting for the full ten seconds, and a cancelled step reports
*Skipped*, so a timed-out scenario would have come back with no failure at all and reported green.

So the scenario timeout sits in `ScenarioScheduler.RunAsync`, beside the per-step timeout it already
implements, and works the same way: the token asks the steps to stop, and phase 3 races the timer
against the running steps so the scheduler stops *waiting*. Steps cut short are Failed with a
`TimeoutException` naming the scenario, the timeout and the step (MTP maps `TimeoutException` to a
timeout state, not an error — item 1's reporting requirement was already met by `MapFailure`); steps
that never ran are Skipped with `"scenario timed out"`, distinct from the host-cancellation reason.
Teardown still runs. Host cancellation still skips rather than fails — there is a test for that, so
the two paths cannot merge by accident.

One subtlety worth keeping: the winners are snapshotted *before* the cancellation is requested. A
cancelled step returns a Skipped outcome normally, so after the cancel there is no way to tell a step
that raced the timer to the line from one the timeout killed.

**Item 4 — attachments are not deleted at session close.** The handoff asked for deletion. Deleting
them destroys the artifacts the run just published, and a TRX or an IDE may hold links to the files;
the actual problem was that they lived in `%TEMP%\raun-mtp` for ever, invisible and unbounded. They
now go under the run's own results directory (`<results>/raun-attachments/<session>/`), which is
visible, per run, and already the user's to keep or delete. Only a session folder that stayed empty
is removed, so a results directory does not collect one empty folder per run. Verified end to end:
the sample's attachment now lands under `bin/Release/net10.0/TestResults/raun-attachments/…`.

**Item 5 — MSTest was added too.** The handoff named FluentAssertions and Shouldly. MSTest's
`AssertFailedException` has the same problem and is one more string in the same list.

**Item 9 — both halves landed, in two commits.** The IR change (value equality) and the file split
are independent fixes with independent tests, and the split rewrites the snapshot layout, so they are
reviewable separately.

The split's snapshot churn was checked mechanically, not eyeballed: every member block of the old
single-file snapshot is byte-identical to a block in the new files, and the multisets match for all
eight snapshots. Only file boundaries and the `partial` keyword moved.

One thing the split turned up: `IncrementalCachingTests`'s own source had been hand-written rather
than built on `SampleSources.Dsl`, and it lowered to **no scenarios at all** — so the caching
assertions around it were vacuous (the meaningful coverage was the five-shape theory over the real
samples, which did fail before the IR fix). It now builds on the shared DSL and emits two scenarios.
Worth remembering as a failure mode: a generator test whose input silently generates nothing still
passes every assertion about caching.

**Item 10 — merge and pass-through nodes keep their positional keys.** They are synthetic, never
reported as test nodes, and never named by a filter, so there is nothing for a stable uid to protect.
Every real step uid changed once; the snapshot diff is uid lines and nothing else (checked line by
line before accepting). Nothing in `test/` or `samples/` hardcoded a uid.

## Not done, and why

**Item 4's stretch — a `Redact` hook.** Deliberately skipped. A redaction callback that a suite can
forget to set, or that only covers log lines and attachment text while keys still travel into span
attributes, the HTML report and TRX, buys a false sense of safety. README now says plainly that Raun
redacts nothing and that keys must be synthetic test identities. If you do want one later, the honest
version covers keys, logs, attachments and exception messages at the point each is *recorded*, not at
the point each is reported.

## Verification

```
dotnet build Raun.slnx -c Release      # 0 warnings
dotnet test  Raun.slnx -c Release      # 720 total, 719 passed, 1 skipped (expected)
dotnet restore Raun.slnx --locked-mode # clean
dotnet run --project samples/AppointmentTests  # 52 nodes, attachment under TestResults/
```

Packs were verified via the CI pack step's commands locally at the start of the night and the
packaging inputs did not change except `buildTransitive/Raun.props`, which `SdkFloorTests` drives
directly.

## Things to look at when you wake up

- `RAUN018` is an MSBuild error code, not an analyzer rule, so it is not in
  `AnalyzerReleases.Unshipped.md`. If you would rather the diagnostic namespace stay analyzer-only,
  renaming it is a one-line change in `src/Raun/buildTransitive/Raun.props` plus the two docs that
  name it.
- `Microsoft.Testing.Extensions.TrxReport` 2.4.0 is now referenced by the `AppointmentTests` sample
  only, pinned in lockstep with `Microsoft.Testing.Platform`. It moves with MTP, like the coverage
  extension does.
- The action SHAs were resolved from the live tags tonight (`actions/checkout` v7,
  `actions/setup-dotnet` v6, `actions/upload-artifact` v7). Dependabot will keep them moving.
- The platform-hook (`TestingPlatformBuilderHook`) redesign is still deferred, as you asked. The
  `configure` route is now documented and tested, which is the honest description of today's surface.
