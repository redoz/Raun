# Optional coverage leaf package

Date: 2026-10-14
Status: approved, implemented, reviewed, and released in v0.3.0-beta.24
Scope: staff-review finding 6, after independent completion of findings 4 and 5.

## Goal

A consumer referencing Raun.Mtp or Raun.Aspire without Raun.Coverage must not restore coverage's
Roslyn workspaces, MSBuild locator, or build-host assets. A consumer explicitly adding Raun.Coverage
retains classified branch coverage, compatible collector support, custom middleware, and report
integration. Core report data and presentation remain in Raun.

## Package boundary

Make Raun.Coverage a leaf depending on Raun.Mtp and Raun; remove Raun.Mtp's dependency on it.
Move coverage analysis configuration, collector delivery, session coordination, middleware registration,
and Coverlet report enrichment from MTP into the coverage package. Preserve public middleware API
names/namespaces for source compatibility; this beta requires rebuilding consumers because these
extension methods move assemblies. Existing report coverage models stay in core Raun and carry no
compiler/MSBuild dependencies. Collector packages remain separate consumer choices.

MTP exposes one small coverage-specific registration/lifetime contract. A builder registers an
application-scoped integration factory; after consumer configuration, the bootstrap creates and owns
the integration. It can attach a session's HtmlReportSink, finish enrichment/drain after the app run,
and be disposed even on exceptional exits. No global mutable integration singleton, module initializer,
assembly scanning, or reflective loading is required. Duplicate enablement must be idempotent; builder
instances remain isolated. MTP creates a coverage report placeholder only when integration is present
and a collector was requested. Best-effort enrichment must preserve the test exit code.

The leaf provides builder.UseClassifiedCoverage(). This wires existing Microsoft Code Coverage artifact
consumption and Coverlet controller delivery to the same analysis pipeline. Middleware registration
also enables the integration so existing custom-rule calls retain their intent and application scope.

Generated entry points detect the optional coverage integration symbol at compilation time and emit
explicit registration when it is available. Without the leaf their entry point and dependency graph
remain unchanged. Handwritten Main programs enable it in RunAsync's configure callback. README will
show both package opt-in and custom-entry registration, collector choices, and middleware examples.
Samples that exercise coverage opt in explicitly; core-only samples/fixtures demonstrate absence.

## Alternatives considered

- Keep the mandatory reference but lazy-load analysis: rejected because restore, runtime assets, and
  build-host files still reach every consumer.
- A fifth Raun.Coverage.Mtp package: rejected because the existing coverage package can own its MTP
  integration and become the requested optional leaf without another distribution choice.
- MTP reflects over an optionally referenced assembly: rejected because startup/discovery becomes
  implicit and assembly loading makes correctness difficult to verify. Explicit generated/custom
  registration is visible and testable.
- Move report models/UI to coverage: rejected because report serialization/presentation is already a
  lightweight core concern and moving it expands compatibility cost without removing heavy dependencies.

## Verification and completion

Preserve existing coverage pipeline behavioral tests, moving ownership to the coverage test project
when necessary. Per the user instruction, add no new tests or committed consumer fixtures.
Use the existing suite and manually restore/build/run scratch consumers with and without the leaf
after packing real packages. Inspect project.assets.json and output: a plain MTP/Aspire consumer
must have no Raun.Coverage, Roslyn workspace/MSBuild analysis packages, or BuildHost-netcore output;
an opted-in consumer must have required build-host assets and working collector-to-report enrichment.
Retain collector correlation, duplicate artifact suppression, custom middleware ordering, unavailable
coverage diagnostics, and awaited delivery in either report/artifact order.

Run locked restore, zero-warning full solution build/tests, all report checks, all four package checks,
and fresh staff review. Commit and push, wait for hosted CI, publish the next beta, and record exact
verification/publication evidence in the staff-review checklist before closing finding 6.

Release evidence: implementation 7cad733; hosted CI 38629066244 and release 38629663072 passed.
No new tests added; 1,418 existing tests passed, 5 skipped, 73 report checks passed. Manual packaged
consumer graphs and generated/custom bootstraps verified; collector delivery enriched the report.
