# Core/adapter boundary — Implementation Plan

**Goal:** Make `Raun` the whole runtime (author + run + report) and `Raun.Mtp` the platform adapter
only, ship the generator in `Raun`, and give the model the identity the adapters were re-deriving.

**Spec:** `docs/superpowers/specs/2026-09-09-core-adapter-boundary-design.md`

**Tech:** .NET 10, C# 14, Microsoft.Testing.Platform 2.4.0, xUnit v3, Verify, Jujutsu.

## Global constraints

- `dotnet build Raun.slnx` ends with 0 warnings (TreatWarningsAsErrors, AnalysisLevel latest-all).
- `dotnet test Raun.slnx` after every commit; never `--filter` or `--nologo`.
- Verify snapshots: read the diff, then `mv` received over verified. No `*.received.*` at commit.
- jj only. One commit per step below. No push (Patrik pushes).
- TDD for every new behaviour (gate, uid, identity members, label change, `StepInputs` throw).
  Pure relocations are covered by the tests that move with them.

## Steps

### C1 — attributes and identity helpers into core
- [x] Move `ScenarioAttribute.cs`, `TeardownAttribute.cs` to `src/Raun/Attributes/`.
- [x] Add `Raun.Model.StepUid.Of(string scenarioId, string stepId)` (red test in `Raun.Test/ModelTests`).
- [x] Move `ScenarioStepNumbering` → `src/Raun/Reporting/StepNumbering.cs`, rename; tests →
      `Raun.Test/StepNumberingTests.cs`; `ScenarioAttributeTests` → `Raun.Test`.
- [x] `RaunDiscoverer.MakeUid` callers use `StepUid.Of`; delete `MakeUid`.
- [x] Generator test project comment updated; harness `References` unchanged (still both assemblies).
- [x] Build, test, commit: `refactor(core): [Scenario], step numbering and step uids live in Raun`.

### C2 — run loop, preflight, selectors, stop signal into `Raun.Running`
- [x] Move + rename `RaunRunLoop` → `RunLoop`; `Preflight`; `NodeSelector` + `UidNodeSelector`;
      `RunStopSignal` (out of `Capabilities.cs`). All public. Fix `using`s in `Raun.Mtp`.
- [x] `ScenarioScheduler.CanceledSkipReason` constant; loop compares against it.
- [x] Tests: `RunLoopTests` splits — pure cases → `Raun.Test/Running/RunLoopTests.cs`; the six
      framework cases stay as `Raun.Mtp.Test/RunLoopFrameworkTests.cs`. `PreflightTests` likewise
      (discovery case stays). `NodeSelectorTests` uid half → `Raun.Test`. `ServiceScopeTests` →
      `Raun.Test`. `Raun.Test.csproj` gains `Microsoft.Extensions.DependencyInjection`.
- [x] Build, test, commit: `refactor(core): the run loop is part of the runtime, not the adapter`.

### C3 — HTML report into `Raun.Reporting.Html`
- [x] Move sink, builder, model, template; resource name `Raun.Reporting.Html.report-template.html`;
      `HtmlReportSink` public. Option provider and path stay.
- [x] Tests move with their verified file; `Raun.Test.csproj` gains `Verify.XunitV3` + `VerifyConfig`.
- [x] Build, test, commit: `refactor(core): the HTML report is a runtime sink, not an MTP feature`.

### C4 — generator ships in `Raun`; entry point gated on `Raun.Mtp`
- [x] Red: `EntryPointGenerationTests.Entry_point_is_not_emitted_without_the_mtp_bootstrap` using a
      harness path that omits the `Raun.Mtp` reference.
- [x] Generator: combine the options provider with `CompilationProvider.Select(c =>
      c.GetTypeByMetadataName("Raun.Mtp.RaunTestApplication") is not null)`.
- [x] `Raun.csproj`: generator project reference, `_IncludeRaunGenerator`, `buildTransitive/Raun.props`
      (moved from `Raun.Mtp.props`). `Raun.Mtp.csproj`: remove those; keep `Raun.Mtp.targets`;
      `PrivateAssets="none"` on the `Raun` reference. `Raun.Aspire.csproj`: same on both references.
- [x] Pack to `artifacts/`, inspect nuspecs and file lists; scratch consumers (spec Verification 4–5).
- [x] README (packages paragraph, layout table), AGENTS.md table, `docs/RELEASING.md` (generator
      location, compatibility rules).
- [x] Build, test, commit: `build: the generator ships in the Raun package`.

### C5 — model identity
- [x] Red: `ModelTests` for defaults; generator lowering test asserting `Namespace`/`TypeName`
      (including a nested type); `ScenarioTestIdentityTests` and `ScenarioNodePathTests` cases for
      the model-first path and the fallback; `HtmlReportModelBuilderTests` label expectation.
- [x] Emitter sets the members; adapters prefer them; report label uses `StepNumbering`.
- [x] Accept snapshot diffs after reading them.
- [x] Build, test, commit: `feat(model): scenarios carry their namespace and type name`.

### C6 — surface tightening
- [x] `ContentionGate`, `ResourceLedger`, `TeardownLog`, `SimulatedClock`, `StableId` → internal;
      delete `ResourceClaim`; `StepInputs.Get` throws (red test first).
- [x] Build, test, commit: `refactor(core): internalize what only the runtime uses`.

### Review
- [x] Both reviewers re-read the range; confirmed findings addressed in 82786e02 and 2d02b34e.
- [x] Handoff note (`docs/superpowers/handoffs/2026-09-09-overnight-handoff.md`) + memory update.
