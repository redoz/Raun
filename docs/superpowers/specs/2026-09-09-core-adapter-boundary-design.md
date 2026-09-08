# Core/adapter boundary — Design

- **Date:** 2026-09-09
- **Status:** Approved by delegation (Patrik, 2026-09-08 evening: "do all of these while I sleep …
  organize the specs, planning, implementation and then rechecking with our two developers").
  Decisions below were made autonomously under that mandate; each names its alternative.
- **Scope:** `src/Raun`, `src/Raun.Mtp`, `src/Raun.Generator` (entry-point emission and packaging),
  `src/Raun.Aspire` (reference only), tests under `test/Raun.Test`, `test/Raun.Mtp.Test`,
  `test/Raun.Generator.Test`, packaging (`*.csproj`, `buildTransitive`), README, AGENTS.md,
  `docs/RELEASING.md`.
- **Non-goals:** a second adapter (this makes one possible; it does not build one); merging the
  analyzer's statement walk into the parser (recorded as the structural follow-up to RAUN017);
  writer preference in admission (deferred by decision on 2026-09-08); type forwarders or any other
  binary-compatibility shim (no version has been tagged; see Compatibility).

## Why

`src/Raun/Raun.csproj` describes itself as the "runner-agnostic core", and `Raun.Mtp` as the
Microsoft.Testing.Platform adapter. The code does not match the description. Everything below lives
in `Raun.Mtp` today and imports no MTP type:

| In `Raun.Mtp` today | What it is | MTP imports |
|---|---|---|
| `ScenarioAttribute`, `TeardownAttribute` (namespace `Raun`) | how a scenario is authored | none |
| `RaunRunLoop` (507 lines) | scenario selection, admission through `ContentionGate`, per-scenario DI scope, run/scenario spans, wait accounting, the `RunEvent` envelope | none |
| `Preflight` | the one-node preflight definition | none |
| `NodeSelector`, `UidNodeSelector` | "which steps does this request select" | none |
| `RunStopSignal` | the graceful-stop flag the loop honours | none |
| `ScenarioStepNumbering` | `1`, `2.1`, zero-padded labels | none |
| `HtmlReportSink`, `HtmlReportModelBuilder`, `HtmlReportModel`, the template | the HTML report, consuming only `RunEvent`s | none |
| the generator, packed under `analyzers/` | turns authored methods into graphs | n/a |

Consequences, observed rather than predicted:

- Nothing can author or run a scenario against `Raun` alone. A console runner, a VSTest bridge, or a
  TUnit-style host must reference `Raun.Mtp`, drag Microsoft.Testing.Platform in, and re-implement the
  loop it cannot reuse because it is `internal`.
- Every recent feature landed its runtime half in `Raun.Mtp` because that is where the loop is:
  concurrent scenarios, preflight, DI scoping, tracing spans, wait accounting. The trend is
  one-directional and will continue until the boundary moves.
- The adapter re-derives identity the generator already had: `ScenarioTestIdentity.Split` and
  `ScenarioNodePath` split `MethodName` on dots, with a documented nested-type imprecision, and the
  HTML report labels steps by `Index` while MTP shows `2.1` — two naming schemes for one node.

The last review (2026-09-09) named this the single most important structural risk and said it is
cheap now and expensive after v1. There is no tag yet. This is the moment.

## Decisions

### 1. `Raun` becomes the whole runtime; `Raun.Mtp` keeps only what touches the platform

**Moves into `Raun`:**

| From `src/Raun.Mtp` | To `src/Raun` | Namespace | Visibility |
|---|---|---|---|
| `ScenarioAttribute.cs`, `TeardownAttribute.cs` | `Attributes/` | `Raun` (unchanged) | public |
| `RaunRunLoop.cs` | `Running/RunLoop.cs` | `Raun.Running` | public |
| `Preflight.cs` | `Running/Preflight.cs` | `Raun.Running` | public |
| `Filtering/NodeSelector.cs` (`NodeSelector`, `UidNodeSelector`) | `Running/NodeSelector.cs` | `Raun.Running` | public |
| `RunStopSignal` (from `Capabilities.cs`) | `Running/RunStopSignal.cs` | `Raun.Running` | public |
| `ScenarioStepNumbering.cs` | `Reporting/StepNumbering.cs` | `Raun.Reporting` | public |
| `RaunDiscoverer.MakeUid` | `Model/StepUid.cs` | `Raun.Model` | public |
| `HtmlReport/HtmlReportSink.cs`, `HtmlReportModelBuilder.cs`, `HtmlReportModel.cs`, `report-template.html` | `Reporting/Html/` | `Raun.Reporting.Html` | sink and model public, builder internal (as today) |

**Stays in `Raun.Mtp`:** `RaunTestApplication` (bootstrap), `RaunTestFramework`, `RaunDiscoverer`
(builds `TestNode`s), `MtpReportSink`, `ScenarioTestIdentity` (builds the MTP
`TestMethodIdentifierProperty`), `ScenarioNodePath`, `TreeNodeSelector`, `RunOptionsProvider`,
`ScenarioParallelism` (reads the MTP command line), `RaunGracefulStopCapability`,
`RaunBannerCapability`, `RaunExtension`, `NodeDiagnostics`, `HtmlReportOptionsProvider`,
`HtmlReportPath` (the `--report-html` option and its path resolution).

The test: a file moves if and only if it has no `Microsoft.Testing.Platform` import and no
dependency on one that does. Every row above passes it; every "stays" entry fails it.

**Renames.** `RaunRunLoop` → `RunLoop` (the `Raun` prefix is redundant inside namespace `Raun.*`);
`ScenarioStepNumbering` → `StepNumbering`. Nothing else is renamed. The type formerly reached as
`RaunDiscoverer.MakeUid` becomes `StepUid.Of(scenarioId, stepId)`; `RaunDiscoverer` and
`MtpReportSink` call it.

**Rejected: `InternalsVisibleTo("Raun.Mtp")` instead of making the moved types public.** It keeps the
surface small today, but the whole point of the move is that the next adapter is not us. A
second adapter needs `RunLoop`, `NodeSelector`, `Preflight`, `RunStopSignal`, `StepNumbering`,
`StepUid`, `IRunEventSink`, `RunEventBus` and the `RunEvent` family — that is the adapter contract,
and it should be visible. The surface is still narrow: one loop, one selector abstraction, one
numbering function, one uid function.

**Rejected: leaving the HTML report in `Raun.Mtp`.** Its sink, builder and model import nothing
from the platform; only the `--report-html` option does. A console runner wants the same report.
The template moves with it (embedded resource name changes to
`Raun.Reporting.Html.report-template.html`).

**Rejected: a new `Raun.Runtime` package between core and adapters.** Four packages for three
concerns; nobody needs the model without the scheduler and loop.

### 2. The generator ships in the `Raun` package

`Raun.csproj` takes over the `_IncludeRaunGenerator` target and the `ReferenceOutputAssembly=false`
project reference; `Raun.Mtp.csproj` drops both. `buildTransitive/Raun.props` (the
`RaunGenerateProgram` default and its `CompilerVisibleProperty`) ships in `Raun`, because the
generator reads it. `buildTransitive/Raun.Mtp.targets` (suppressing the SDK's own entry points)
stays in `Raun.Mtp`, because only an MTP application has an entry point to suppress.

**Transitive analyzer flow.** NuGet pack writes a `ProjectReference`'s default `PrivateAssets`
(`contentfiles;analyzers;build`) into the nuspec as `<dependency … exclude="Build,Analyzers" />`. If
`Raun.Mtp` depended on `Raun` that way, `dotnet add package Raun.Mtp` would silently *not* bring the
generator. So `Raun.Mtp.csproj` and `Raun.Aspire.csproj` reference `Raun` (and Aspire references
`Raun.Mtp`) with `PrivateAssets="none"`. Verification is by inspection of the packed nuspecs and by a
scratch consumer (see Verification); this is not assumed.

**Entry-point gate.** The generator's emitted `Main` calls `Raun.Mtp.RaunTestApplication.RunAsync`.
With the generator in `Raun`, a project that references `Raun` without `Raun.Mtp` must not get a
`Main` that does not compile. The generator therefore emits the entry point only when
`RaunGenerateProgram` is not `false` **and** the compilation can resolve
`Raun.Mtp.RaunTestApplication` (`CompilationProvider` → `GetTypeByMetadataName`). The existing
tests keep their behaviour because the harness references `Raun.Mtp`; a new test proves that
without the reference no entry point is produced.

**Rejected: keeping the generator in `Raun.Mtp`.** Then `Raun` still cannot author a scenario (no
`[Scenario]` lowering) and the "core" claim stays false for anyone not on MTP.

### 3. The model carries the identity the adapters were re-deriving

`ScenarioDefinition` gains two init-only, non-required members with empty-string defaults:

```csharp
/// <summary>Namespace of the declaring type ("" for the global namespace).</summary>
public string Namespace { get; init; } = "";
/// <summary>Simple name of the declaring type (nested types joined with '+').</summary>
public string TypeName { get; init; } = "";
```

The emitter sets both from the symbols it already has (`ContainingNamespace`, `ContainingType`).
`ScenarioTestIdentity.Create` and `ScenarioNodePath.For` use them when non-empty and fall back to
the dotted split when empty — the fallback keeps an older generator's output working against a newer
runtime (see Compatibility), and `Split` stays as the fallback only.

The HTML report's `ReportStep.Label` becomes the `StepNumbering` label rather than the raw node
index, so the report and the MTP tree name a step the same way.

**Rejected: emitting `MethodName` differently.** It is a public model member and the span tag
`raun.scenario.method` already carries it; changing its shape breaks consumers of both.

### 4. Tests move with the code

Tests that exercise a moved type without any MTP import move to `test/Raun.Test` (which gains
`Microsoft.Extensions.DependencyInjection` for the scope tests and `Verify.XunitV3` for the report
model snapshot): `RunLoopTests` (all but the six end-to-end cases that construct
`RaunTestFramework`, which stay as `RunLoopFrameworkTests` in `Raun.Mtp.Test`), `PreflightTests`
(all but the discovery case, which stays), `NodeSelectorTests` (the uid half; the tree half stays),
`ScenarioStepNumberingTests`, `ScenarioAttributeTests`, `ServiceScopeTests`, `HtmlReportSinkTests`,
`HtmlReportModelBuilderTests` with its verified file. `test/Raun.Generator.Test` keeps its `Raun.Mtp`
reference for the entry-point bind test and gains the no-`Raun.Mtp` harness path for the gate test.

### 5. Surface tightening (trailing, separate commit)

Once the loop is in-assembly, `ContentionGate` becomes `internal` (only the loop uses it).
`ResourceLedger`, `TeardownLog`, `SimulatedClock` become `internal` (no caller outside `Raun` and
`Raun.Test`, which has `InternalsVisibleTo`). `ResourceClaim` (no caller anywhere) is deleted;
`StableId` (runtime reference implementation of the generator's `GenStableId`, no runtime caller)
becomes `internal` and keeps its test. `StepInputs.Get` throws `InvalidOperationException` naming the
producer when the output slot was never filled instead of returning a null cast; only a generator bug
reaches it, and it should be loud. The scheduler's `"scenario canceled"` literal becomes
`ScenarioScheduler.CanceledSkipReason`, and the loop compares against the constant.

## Layout after

```
src/Raun/
  Attributes/   ScenarioAttribute.cs  TeardownAttribute.cs  StepNameAttribute.cs
  Awaiters/     ScenarioAwaiters.cs
  Model/        ScenarioDefinition.cs (+Namespace, +TypeName)  ScenarioNode.cs  StepUid.cs  …
  Reporting/    IRunEventSink.cs  RunEvent.cs  RunEventBus.cs  RunEventSink.cs  StepNumbering.cs
  Reporting/Html/  HtmlReportSink.cs  HtmlReportModelBuilder.cs  HtmlReportModel.cs  report-template.html
  Running/      RunLoop.cs  Preflight.cs  NodeSelector.cs  RunStopSignal.cs
  Scheduling/   ScenarioScheduler.cs  ContentionGate.cs (internal)  …
  buildTransitive/Raun.props
src/Raun.Mtp/
  RaunTestApplication.cs  RaunTestFramework.cs  RaunDiscoverer.cs  MtpReportSink.cs
  ScenarioTestIdentity.cs  ScenarioParallelism.cs  RunOptionsProvider.cs  Capabilities.cs
  RaunExtension.cs  NodeDiagnostics.cs  Filtering/{ScenarioNodePath,TreeNodeSelector}.cs
  HtmlReport/{HtmlReportOptionsProvider,HtmlReportPath}.cs
  buildTransitive/Raun.Mtp.targets
```

## Compatibility

- **Source.** Authoring code (`using Raun;`, `[Scenario]`, `[Teardown]`, the DSL) is unchanged.
  `Raun.Mtp.RaunTestApplication.RunAsync` is unchanged, so hand-written `Program.cs` files and
  `Raun.Aspire` are unchanged.
- **Binary.** Types change assembly. No type forwarders: no version has been tagged, only
  `0.x.y-preview` builds exist, and the three packages move in lockstep. Recorded so nobody adds
  forwarders later out of caution.
- **Generator ↔ runtime.** `Raun.Mtp` depends on `Raun` with `>=`, so *older generator, newer
  runtime* is a legal resolution and *newer generator, older runtime* is not. The rules that follow,
  now written into `docs/RELEASING.md`: never add a `required` member to `ScenarioDefinition` or
  `ScenarioNode` (the new identity members are optional with defaults, and the adapters fall back);
  never rename a `ResourceContext` verb the emitter names by string; `Run`'s ordinals are frozen
  because the emitter writes an int cast.

## Verification

1. `dotnet build Raun.slnx` — 0 warnings, 0 errors.
2. `dotnet test Raun.slnx` — every project green, the one pre-existing skip only. The suite
   includes both samples.
3. Pack all three packages to `artifacts/`; unzip each `.nuspec`; assert `Raun.nupkg` contains
   `analyzers/dotnet/roslyn5.3/cs/Raun.Generator.dll` and `buildTransitive/Raun.props`,
   `Raun.Mtp.nupkg` contains no analyzer and its `Raun` dependency carries no
   `exclude="…Analyzers…"`.
4. A scratch consumer outside the repo with a `nuget.config` pointing at `artifacts/` and a
   `PackageReference` to `Raun.Mtp` only: one scenario, no `Program.cs`. `dotnet build` succeeds
   (generator ran transitively, entry point emitted), `dotnet run -- --list-tests` lists the steps.
5. A second scratch consumer referencing `Raun` only, with `OutputType=Library`: builds, and no
   `RaunProgram.g.cs` is generated.
6. Both reviewers re-read the diff (`2e9a568b..@-`) and their confirmed findings are addressed.

## Follow-ups (not in this change)

In the order the 2026-09-09 re-review recommends:

1. **Entry point via the platform's own hook — Patrik's decision, before the first tag.** The
   generator still knows one adapter by name (it probes for `Raun.Mtp.RaunTestApplication` and emits
   a call into it), and `Raun` ships an MTP-only MSBuild knob (`RaunGenerateProgram`) that becomes a
   consumer-visible contract once tagged. `Microsoft.Testing.Platform.MSBuild` offers a
   `TestingPlatformBuilderHook` item: an adapter ships a `buildTransitive/*.props` naming a static
   `AddExtensions(ITestApplicationBuilder, string[])`, and the platform's own generated entry point
   calls it. Everything `RaunTestApplication.RunAsync` does is builder work. Taking it deletes
   `EntryPointEmitter`, the gate, `RaunGenerateProgram`, `Raun.props` and `Raun.Mtp.targets`; the
   hand-written-`Program` path (Aspire, `simulateTime`) uses the platform's
   `GenerateTestingPlatformEntryPoint=false`. Cost: `Raun.Mtp` depends on
   `Microsoft.Testing.Platform.MSBuild`. Not done overnight: it changes a consumer-visible property.
2. **Factor the node-execution pipeline** (five `new StepResult { … }` sites, three copying the same
   block; `RunLoop.SkipScenarioAsync` is a third skip synthesizer) so teardown and preflight stop
   hand-assembling results. Prerequisite for external-process steps.
3. A `ScenarioScope` replacing the `Attach*` mutators on `ScenarioContext` — when the fifth attach
   (resource slot handles) arrives.
4. A `RunOptions` record in `Raun.Running` replacing the mirrored constructor / `RunAsync` / loop
   parameter lists — with the next run option, not before.
5. Analyzer statement walk derived from the parser — maintenance-only now that RAUN017 closes the
   safety gap; do it with the next construct (loops), not as standalone work.
6. When a second adapter exists: lift verdict classification and log formatting out of
   `MtpReportSink` as one `StepResult` extension.
