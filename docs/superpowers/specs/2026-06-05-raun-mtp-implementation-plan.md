# Implementation plan: Raun.Mtp (own MTP test framework, v1)

**Design spec (read first):** `docs/superpowers/specs/2026-06-05-raun-mtp-test-framework-design.md`
**Evidence/handoff:** `docs/superpowers/handoffs/2026-06-05-mtp-step-discovery-handoff.md`
**xUnit reference source (the closest analog to what we build):** `C:\dev\vendor\xunit\src\common\MicrosoftTestingPlatform\` (esp. `TestPlatformTestFramework.cs`, `TestPlatformExecutionMessageSink.cs`, `TestPlatformDiscoveryMessageSink.cs`, `TestNodeExtensions.cs`) at tag `v3-3.2.2`.
**Patterns being replaced:** `src/Raun.Xunit/` (`ScenarioDiscoverer.cs`, `ScenarioStepReporter.cs`, `ScenarioTestCase.cs`, `Raun.Xunit.csproj` packaging).

This plan is executed by an autonomous overnight workflow. Each phase is one focused agent. **Every agent must:** work TDD (failing test first), leave the **entire solution building with all tests green**, fix anything a prior phase left broken, then commit. Do not move scope between phases.

## Conventions (all phases)
- **Repo root:** `C:\dev\raun`. **Branch:** `feat/raun-mtp` (already created; commit onto it).
- **Build:** `dotnet build` at repo root. **Test:** `dotnet test` (existing test projects use the in-repo runner). Verify both are green before finishing.
- **Commit** per phase when green: `git add -A && git commit -m "<concise message>"`. **No `Co-Authored-By` or tooling trailers** (project rule). One commit per phase, present-tense subject.
- **Do not touch** `src/Raun/` (engine) or `src/Raun.Generator/` core logic except where a phase explicitly says so (Phase 6 only). Leave `src/Raun.Xunit/` in place (retirement is out of scope).
- **MTP package version:** determine the correct `Microsoft.Testing.Platform` package + version by inspecting what `xunit.v3` references transitively and/or the vendor source's package refs / `Directory.Packages.props`. Prefer the repo's central package management if present.
- If genuinely blocked (e.g. a package cannot restore), record the blocker in the structured result rather than thrashing; still leave the build green if possible by isolating the unfinished piece.

## Phase 1 — Scaffold `Raun.Mtp` + test project
- Create `src/Raun.Mtp/Raun.Mtp.csproj` (`net10.0`, library): references `Raun` core + `Microsoft.Testing.Platform`; packages `Raun.Generator` as an analyzer (mirror `src/Raun.Xunit/Raun.Xunit.csproj` packaging targets); `InternalsVisibleTo Raun.Mtp.Test`.
- Create `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj` mirroring `test/Raun.Xunit.Test` (same runner setup the other test projects use).
- Add both to the solution (`*.sln`/`*.slnx`).
- **Acceptance:** solution builds; new (empty) test project runs green. Commit.

## Phase 2 — `ITestFramework` shell + public bootstrap
- Implement `Microsoft.Testing.Platform.Extensions.TestFramework.ITestFramework`: `CreateTestSessionAsync`, `CloseTestSessionAsync`, `ExecuteRequestAsync` (dispatch `DiscoverTestExecutionRequest` vs `RunTestExecutionRequest`). Model on `TestPlatformTestFramework.cs` but **without** xUnit's project/config/reporter/serialization machinery.
- Public bootstrap: `public static Task<int> RunAsync(string[] args, Action<ITestApplicationBuilder>? configure = null)` that builds the app, calls `builder.RegisterTestFramework(...)`, runs it. This is the **escape-hatch API** a hand-written `Program.cs` calls.
- **Acceptance:** unit tests cover session create/close and request dispatch (discover vs run routing). Build + tests green. Commit.

## Phase 3 — Discovery
- From `ScenarioRegistry` → each `ScenarioDefinition` → emit one `TestNode` per `ScenarioNode`: `Uid = "{ScenarioId}:{StepId}"`, `DisplayName = "{scenario} ▸ {step template}"`, `TestFileLocationProperty(SourceFile, span(SourceLine))`, `DiscoveredTestNodeStateProperty`. Step nodes only (no parent node).
- Publish via the MTP message bus (see `TestPlatformDiscoveryMessageSink.cs` + `TestNodeExtensions.AddMetadata`).
- **Acceptance:** tests assert N nodes per scenario with correct uids, display names, and file-location properties (use a registered test scenario fixture). Green. Commit.

## Phase 4 — Reporter (`IStepObserver` → `TestNodeUpdateMessage`)
- New reporter implementing `Raun.Scheduling.IStepObserver`. Map per the spec §6 table: start→InProgress; Passed→Passed; Failed→Failed/Timeout/Error (by exception kind: `TimeoutException`→Timeout, assertion→Failed, else Error); Skipped→Skipped(reason). Attach `TimingProperty`, file location, and `ScenarioContext.Logs`/`Attachments`. Update node `DisplayName` to the runtime-formatted name on start/finish.
- **Acceptance:** tests drive each `StepStatus` and assert the emitted node state + timing + location. Green. Commit.

## Phase 5 — Run loop + "light up all"
- On run: read filter (`TestNodeUidListFilter` uid set, or null = all). Map each uid → scenario via `ScenarioId`. Run each **distinct** scenario **once** via `ScenarioScheduler` with the Phase-4 reporter, a `CancellationTokenSource` per scenario run (owned by the loop, honoring MTP's token). Publish updates for **every** executed step (siblings light up). Cross-scenario concurrency: sequential for v1 (leave a clear seam/comment for a future bound).
- **Acceptance:** tests prove (a) a multi-step filter for one scenario ⇒ exactly one scheduler run, (b) a single-step filter ⇒ all executed siblings published, (c) one step's cancellation does not kill the shared run for siblings. Green. Commit.

## Phase 6 — Generator emits `Program.cs` + escape hatch
- Extend `src/Raun.Generator` to emit a `Program.cs` whose `Main` calls `Raun.Mtp` `RunAsync(args)`. Gate emission on MSBuild property `RaunGenerateProgram` (default `true`); when `false`, emit nothing (user supplies their own).
- Wire the property through the generator (analyzer config / `CompilerVisibleProperty`).
- **Acceptance:** generator tests assert the entry point is emitted by default and suppressed when the property is `false`. Build + generator tests green. Commit.

## Phase 7 — Sample migration + end-to-end
- Migrate `samples/AppointmentTests`: reference `Raun.Mtp` (drop `Raun.Xunit`) + `xunit.v3.assert`; drop `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` + `xunit.v3`; add repo `global.json` `{ "test": { "runner": "Microsoft.Testing.Platform" } }` if not already present.
- Run the sample under `dotnet test`. **Acceptance:** the run shows the per-step nodes (≈18 across the sample's scenarios), and a single-step filtered run lights up that scenario's siblings. Capture the observed node list into the structured result. Green. Commit.

## Phase 8 — Review + harden
- Adversarially review the whole `Raun.Mtp` implementation against the spec (correctness, the §7 obligations: server-mode vs `dotnet test`, `--list-tests`, cancellation/CTS ownership, attachments). Fix real findings. Run the **entire** solution test suite; ensure 0 warnings.
- **Acceptance:** full suite green, 0 warnings; a short written summary of what was built, what's verified, and any residual gaps. Final commit.
