# Optional Coverage Leaf Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for native execution, or superpowers:subagent-driven-development if selected. Steps use checkbox syntax for tracking.

**Goal:** Make classified coverage an explicit optional package without changing existing coverage/report behavior.
**Architecture:** Raun.Coverage depends on MTP, which exposes a small integration lifetime contract without referencing analysis code. Move the existing integration into the leaf and register it explicitly in generated/custom entry points.
**Tech Stack:** .NET 10, Roslyn incremental generator, Microsoft.Testing.Platform, MinVer/NuGet.
**Spec:** ../specs/2026-10-14-optional-coverage-leaf-design.md

## Global Constraints

- User approved the leaf design and explicitly requested no new tests. Preserve existing tests, moving them as needed; use manual scratch-consumer verification.
- Keep the four existing packages; no fifth integration package and no version property.
- Preserve middleware source API namespace/name; document rebuild requirement when moving assembly ownership.
- Core coverage models and HTML presentation remain in Raun; heavy analysis dependencies belong only to the leaf.
- Preserve independent scope/key fixes, beta.22/beta.23, and original dirty checkout.

## Review Focus

Existing tests/manual checks must cover builder isolation, duplicate activation, exceptional bootstrap disposal, generated versus custom Main, and collector delivery after report completion. No new automated tests per user instruction.

### Task 1: Reverse package ownership and retain the existing delivery pipeline

Files: new src/Raun.Mtp/CoverageIntegration.cs; modify RaunTestApplication.cs, RaunTestFramework.cs, Raun.Mtp.csproj and Raun.Coverage.csproj; move src/Raun.Mtp/Coverage/*.cs into src/Raun.Coverage/Integration/; new ClassifiedCoverageIntegration.cs in that directory; move test/Raun.Mtp.Test/CoveragePipelineTests.cs into test/Raun.Coverage.Test/ and update its namespace/using references as needed.

Interfaces:
- MTP public IRaunCoverageIntegration : IDisposable: Task AttachReportAsync(string session, HtmlReportSink report); Task CompleteAsync().
- MTP public builder.UseCoverageIntegration(Func<ITestApplicationBuilder, IRaunCoverageIntegration> factory), stored per builder; bootstrap creates/owns it after configure. Internal factory lookup returns null when absent.
- Leaf public builder.UseClassifiedCoverage() on existing Raun.Mtp.CoverageBuilderExtensions, idempotent per builder. UseBranchClassificationMiddleware also enables it while keeping factory order and isolation.
- Leaf internal ClassifiedCoverageIntegration owns coordinator, config, artifact consumer and Coverlet enrichment; Attach delegates; Complete enriches then drains; Dispose releases coordinator.

- [x] Remove MTP -> Coverage reference; add Coverage -> MTP with build/analyzer flow preserved. Add MTP friend access for Raun.Coverage to use existing report-path and diagnostics internals without expanding public APIs.
- [x] Move the six existing pipeline files and their existing tests, preserving artifact filtering, correlation, diagnostics, and delivery behavior.
- [x] Replace analysis bootstrap code with optional factory creation and owned integration lifetime. Pass only the interface to RaunTestFramework; keep report placeholders conditional on collector/integration availability.
- [x] Implement leaf activation and wrapper with same analysis/settings behavior. Verify idempotence/isolation in code review and existing middleware test.
- [x] Run focused existing coverage tests and build after locked files are regenerated; inspect warnings rather than suppressing valid rules.

### Task 2: Preserve package opt-in for generated/custom entry points

Files: src/Raun.Generator/ScenarioGenerator.cs, Emit/EntryPointEmitter.cs, samples/AspireAppointments/AspireAppointments.Tests/{Program.cs, project file}, README.md, affected existing project lock files/snapshots only if current tests require updates.

Interfaces:
- EntryPointEmitter.Emit(bool classifiedCoverage = false) emits RunAsync(args) without the leaf, or an explicit configure callback calling global::Raun.Mtp.CoverageBuilderExtensions.UseClassifiedCoverage(builder) when the optional symbol resolves.
- Custom Main registers builder.UseClassifiedCoverage() in configure; it separately registers its existing collector extensions.

- [x] Extend incremental entry-point selection with optional coverage-symbol detection while preserving disabled/bootstrap-absent behavior.
- [x] Add explicit leaf project reference and activation to the Aspire coverage sample; leave core-only consumers without leaf references.
- [x] Update README package opt-in, custom bootstrap, collectors, and middleware examples; document dependency direction and compatibility cost.
- [x] Run existing generator snapshots/platform/coverage tests; review any changed existing snapshots without adding new tests.

### Task 3: Verify distribution, publish, and close finding 6

- [x] Restore Raun.slnx in locked mode; zero-warning Release build and full solution test run.
- [x] Run the existing 73 report interaction checks and pack all four projects.
- [x] Inspect nuspec dependency direction; manually restore/build/run scratch MTP and Aspire consumers without coverage and one opted-in consumer. Inspect project.assets.json and output for absence/presence of workspace analysis and BuildHost-netcore assets. Use existing collector coverage execution to check report enrichment.
- [x] Obtain fresh staff review of integration ownership, startup, and real package evidence; fix material findings.
- [ ] Commit/push, wait for hosted CI, tag the next beta and verify release publication.
- [ ] Save exact evidence and close finding 6 in docs/reviews/2026-10-14-raun-staff-review.md; keep the separate intermittent cancellation follow-up visible.

Execution evidence: existing pipeline tests moved unchanged; no new tests added. Manual packed
MTP/Aspire consumers have no heavy analysis graph or BuildHost; opted-in consumer has both and
generated activation. All three bootstraps ran --help successfully. Existing Aspire Coverlet run
passed 16 tests and enriched report status available; its collector scope supplied no eligible
module hits, recorded in report diagnostics. Staff review cleared source after all transitive locks
were force-refreshed. Final local gate: 1,418 passed / 5 skipped / 0 failed; zero-warning build; 73 report checks. Publication remains.
