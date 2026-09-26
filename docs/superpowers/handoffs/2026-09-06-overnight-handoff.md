# Handoff: Raun — after the 2026-09-05/06 session (rename, packaging, tracing); pick the next item

## Goal
Raun is feature-complete for v1 and published as previews to
GitHub Packages. Success next = a first real version tag and one of the remaining product items.

## State
main = latest push, CI green, 497 tests, 0 warnings, working copy clean. Nothing in flight.
Shipped this session, each its own commit on main: dead C2 scaffolding deleted; resource conflict
detection (RAUN013 analyzer + per-scenario ResourceLedger, pre-call claim emission); lifecycle sample
scenarios; teardown context overloads + RAUN014; not-taken branches reported as skipped; step filter
runs only the selected closure; Apache-2.0 + MinVer tag-driven versioning + CI/release workflows
publishing to GitHub Packages; Raun→Raun rename incl. GitHub repo; AGENTS.md replaces CLAUDE.md;
WaitsFor ordering edge (post-if statement waits for arm tails); timer-stamped log entries with resource
events in the stream; OpenTelemetry-ready tracing; README rewrite; LINQ names at discovery;
attachments in the HTML report.

## Next move
Patrik's call. Recommended order: (1) `git tag -a v0.1.0 -m "Raun 0.1.0" && git push origin v0.1.0`
(his to run; jj only imports tags) — the Release workflow publishes; then (2) brainstorm concurrent
scenario execution (RaunRunLoop foreach is the seam) followed by Tier 3 type-level admission control.

## Key locations
AGENTS.md — committed agent guidance (build/test/verify, conventions, looks-like-a-bug list).
CLAUDE.local.md — Patrik's untracked jj-only rules; never run git mutations, tags are his.
docs/RELEASING.md — tag-driven release checklist incl. moving RAUN rules from Unshipped to Shipped.
docs/superpowers/specs/2026-09-05-resource-conflict-detection-design.md — why locks were rejected; Tier 3 deferred; race opt-out open item.
docs/superpowers/specs/2026-09-06-tracing-design.md — span tree, attributes, Aspire wiring, dashboard follow-up.
docs/superpowers/specs/2026-09-03-scenario-conditionals-design.md (Amendment 2026-09-05) — WaitsFor.
src/Raun/Scheduling/ScenarioScheduler.cs — DAG loop, targets closure, ledger, spans, ScenarioStart holder.
src/Raun/Resources/ResourceLedger.cs — structural conflict detection over ScenarioGraph.Predecessors.
src/Raun/Model/ScenarioGraph.cs — the one definition of ordering edges (DependsOn ∪ MergeSources ∪ WaitsFor ∪ Guards).
src/Raun/Tracing/RaunTelemetry.cs — ActivitySource "Raun", attribute/event names.
src/Raun.Mtp/RaunRunLoop.cs — run span, scenario root spans linked to it, SelectTargets.
src/Raun.Generator/Analysis/ScenarioAnalyzer.cs — RAUN013 (group conflicts), RAUN014 (step ctx captured in cleanup).
src/Raun.Generator/Lowering/ScenarioParser.cs — `_pendingWaits`/`Advance()`; BuildStep sets WaitsFor.
samples/AspireAppointments/*/Program.cs, AppHost.cs — OTLP export gated on OTEL_EXPORTER_OTLP_ENDPOINT.
.github/workflows/{ci,release}.yml — previews on main push, versions on v* tags, GitHub Packages.

## Decided / rejected
- Lock-based C2 (2PL + wound-wait) REJECTED for good: return-role claims unlockable, claim sets unknown up front, wound-wait = re-running real side effects. Detection instead. Fail inside a scenario, serialize across scenarios (Tier 3, needs concurrent scenarios first).
- WaitsFor is a second edge kind, not DependsOn: a not-taken arm must not cascade to the post-if statement.
- Tracing: Raun EMITS, never exports (no OTel dependency in core). Scenario = trace root; step = child; run span LINKED from scenarios (a run root = giant trace, all-or-nothing sampling). No spans for skipped steps (event on scenario span). Attribute names `raun.run`/`raun.scenario`/`raun.step` (not `raun.run.id` — "beach boys").
- Log lines carry a timer from scenario start, not timestamps. Real-mode scenario start = first step's StartedAt (an up-front clock read broke injected-TimeProvider tests). Resource events live IN the log stream.
- Cleanups take the TEARDOWN context; capturing the step ctx in a cleanup is RAUN014. Capturing anything else is fine.
- Not-taken → MTP Skipped with reason; step filter = predecessor closure + teardown, nothing after.
- Apache-2.0 (Patrik's precedent: Synto). Versioning MinVer, lockstep packages. GitHub Packages "for now"; nuget.org = change --source/--api-key in both workflows.
- Raun.Aspire ships plumbing only (no steps, no phase markers); consumers use their own DI. Hold this boundary.
- Loops excluded on purpose; runnable scenario bodies rejected; switch rejected as ergonomics-only.

## Gotchas & scope guard
- Build/test gates: pipe through grep hides failures — use `set -o pipefail` + `${PIPESTATUS[0]}`; a broken build reached main once (f2f7e727, fixed next commit).
- MTP rejects `--nologo` and `--filter`; run whole projects. Analyzer release tracking: new RAUN rules need a row in AnalyzerReleases.Unshipped.md (RS2000).
- AnalysisLevel latest-all + warnings-as-errors: CA1034 (nested public types), CA1068 (CancellationToken last), CA1859, CA1873 (log args), xUnit1031 all bit this session.
- sed across a file can rewrite the body of the helper you just added (Advance() recursion). Check the grep output.
- Verify snapshots: review the received diff before mv over verified.
- Aspire: all AppHost work inside the preflight delegate; testing builder disables the dashboard (hence env-var forwarding).
- Do NOT re-propose resource locks; do NOT start Tier 3 before concurrent scenarios; race opt-out only when a real scenario needs it.
- GitHub Packages needs a read:packages PAT even for public packages (README shows nuget.config).

## Verify
`dotnet build Raun.slnx` → 0 warnings. `dotnet test Raun.slnx` → 497 (count only grows).
`dotnet run --project samples/AppointmentTests/AppointmentTests.csproj` → 52 total, 51 passed, 1 skipped (the not-taken else arm).
`dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj` → 9/9.
`gh run list --repo redoz/Raun --limit 1` → success.

## First move
read AGENTS.md and docs/superpowers/specs/2026-09-06-tracing-design.md, run `jj st`, then ask Patrik which backlog item: tag v0.1.0, concurrent scenarios + Tier 3, dashboard-in-test-run, report diamonds, VS Test Explorer grouping, race opt-out.
