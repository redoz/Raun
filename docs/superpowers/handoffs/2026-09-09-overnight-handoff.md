# Handoff: overnight 2026-09-08 → 09 — the four review findings, done

## Goal
Patrik asked for the four architecture findings (parser silent null; core/adapter boundary; model
identity; surface tightening) to be spec'd, planned, implemented and re-reviewed by both reviewer
personas while he slept. All done. Nothing pushed (memory rule: local commits only unless asked).

## State
main = 89f82c45 (remote, CI green). Local, on top of it, eleven commits, all green:

| commit | subject |
|---|---|
| 08db1db9 | feat(generator): a scenario the parser rejects is reported, never dropped (RAUN017) |
| 37a8ef3f | docs: spec and plan for the core/adapter boundary |
| e8f4cf94 | refactor(core): [Scenario], step numbering and step uids live in Raun |
| b31f76ff | refactor(core): the run loop is part of the runtime, not the adapter |
| 78ffb895 | refactor(core): the HTML report is a runtime sink, not an MTP feature |
| a26bbc69 | build: the generator ships in the Raun package |
| b409fac9 | feat(model): scenarios carry their namespace and type name |
| 07f779b3 | refactor(core): internalize what only the runtime uses |
| 82786e02 | refactor(core): a host walks the registry and resolves identity through Raun |
| 2d02b34e | refactor(core): tighten the new public surface per review |
| (this)   | docs: handoff and plan close-out |

Verification at the tip: `dotnet build Raun.slnx` 0 warnings; `dotnet test Raun.slnx` 685 total,
684 pass, 1 pre-existing skip (includes both samples); `dotnet run` samples 52/51/1 and 13/13;
`dotnet pack` of all three packages inspected (Raun carries `analyzers/dotnet/roslyn5.3/cs` and
`buildTransitive/Raun.props`; Raun.Mtp depends on Raun with `include="All"`); two scratch consumers
built from the packed artifacts — Raun.Mtp-only gets the generated `Main` and lists 3 steps,
Raun-only library gets the manifest and no `Main`; `--report-html` from the sample renders the
moved template with `1`, `2.1` labels.

## Landing
```
jj bookmark set main -r @-
jj git push --bookmark main
```
Then decide the one open design question below; it is a pre-tag decision.

## Decisions taken autonomously (each has its alternative in the spec)
- Moved types are **public** in `Raun.Running` / `Raun.Reporting` / `Raun.Reporting.Html` /
  `Raun.Model.StepUid`, not `InternalsVisibleTo("Raun.Mtp")`: the next adapter is not us.
- **No type forwarders**: no tag exists, only previews. Recorded so nobody adds them from caution.
- Generator in `Raun` with the **entry point gated** on `Raun.Mtp.RaunTestApplication` resolving.
- `ScenarioDefinition.Namespace/TypeName` optional with `""` defaults; adapters fall back to the
  dotted split (`Raun.Model.ScenarioIdentity.Resolve`). Nested types join with `+`.
- Preflight node now files under namespace `Raun`, class `Preflight` (was `""` / `Raun` via the
  split). Visible in Test Explorer grouping and tree-filter paths for that one node.
- `ResourceClaim` deleted with its tests; `StableId`, `SimulatedClock`, `ResourceLedger`,
  `TeardownLog`, `ContentionGate` internal.
- `StepInputs.Get` throws for an unproduced slot (generator-bug detector); `FormatName` catches it
  for skipped steps as before.

## Open for Patrik — decide before the first tag
**Entry point via the platform's hook** (both reviewers, independently). The generator still knows
one adapter by name and `Raun` ships an MTP-only MSBuild knob, `RaunGenerateProgram`, which becomes
a consumer-visible contract once tagged. `Microsoft.Testing.Platform.MSBuild` has a
`TestingPlatformBuilderHook` item: the adapter ships `buildTransitive/*.props` naming a static
`AddExtensions(ITestApplicationBuilder, string[])` and the platform's own generated entry point
calls it. Taking it deletes `EntryPointEmitter`, the gate, `RaunGenerateProgram`, `Raun.props`,
`Raun.Mtp.targets`; hand-written `Program` (Aspire, `simulateTime`) uses the platform's
`GenerateTestingPlatformEntryPoint=false`. Cost: `Raun.Mtp` depends on
`Microsoft.Testing.Platform.MSBuild`. Details in the spec's Follow-ups §1. Not done: it changes a
consumer-visible property, and the reviewer's claim about the hook's shape should be verified
against the 2.4.0 targets before building on it.

## Recorded follow-ups (spec, Follow-ups section, in recommended order)
2. Factor node execution (five `new StepResult` sites; prerequisite for external-process steps).
3. `ScenarioScope` replacing the four `Attach*` mutators — at the fifth attach.
4. `RunOptions` record — with the next run option.
5. Analyzer walk derived from the parser — maintenance-only now; do it with loops.
6. Lift verdict/log formatting from `MtpReportSink` when a second adapter exists.
Also noted by the veteran: `ParsedScenario` carries `UsingDirectiveSyntax`/`TypeSyntax` (reference
equality), so the scenario source output regenerates on every keystroke. Pre-existing; the RAUN017
path itself is value-only.

## Key locations
- Spec: docs/superpowers/specs/2026-09-09-core-adapter-boundary-design.md
- Plan (all boxes ticked): docs/superpowers/plans/2026-09-09-core-adapter-boundary.md
- New public adapter contract: src/Raun/Running/{RunLoop,Preflight,NodeSelector,RunStopSignal}.cs,
  src/Raun/Reporting/StepNumbering.cs, src/Raun/Reporting/Html/HtmlReportSink.cs,
  src/Raun/Model/{StepUid,ScenarioIdentity}.cs, ScenarioRegistry.Definitions()
- Generator gate: src/Raun.Generator/ScenarioGenerator.cs (CompilationProvider + Combine),
  src/Raun.Generator/Emit/EntryPointEmitter.cs (BootstrapTypeName)
- Packaging: src/Raun/Raun.csproj (`_IncludeRaunGenerator`, props), PrivateAssets="none" in
  Raun.Mtp.csproj and Raun.Aspire.csproj; docs/RELEASING.md "Generator ↔ runtime contract"
- Coverage guard: test/Raun.Generator.Test/LoweringCoverageTests.cs (add every new sample to DslFor)

## Gotchas
- Two classes in Raun.Test register process-wide `ActivityListener`s (TracingTests,
  Running/RunLoopTests); they share `[Collection("ActivityListeners")]`. Removing it brings back
  a 1-in-5 flake in `Without_a_listener_nothing_is_recorded…`.
- The generator test harness has two reference sets: `References` (with Raun.Mtp) and
  `CoreOnlyReferences` (filters Raun.Mtp out of the TPA list too). Use the latter for "no
  bootstrap" cases.
- `PlatformSurfaceTests` pins both `TESTINGPLATFORM_UI_LANGUAGE` and `DOTNET_CLI_UI_LANGUAGE`;
  MTP reads its own variable first.
- Scratch consumers and packed artifacts live in the session scratchpad, not the repo.
