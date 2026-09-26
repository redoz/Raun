# Design: Self-contained HTML run report (timeline, logs, resource events)

- **Date:** 2026-06-07
- **Status:** Implemented (2026-06-09) on MTP 2.2.3.
- **Scope:** `src/Raun` (new `Reporting` event bus; scheduler timestamps; `StepResult`),
  `src/Raun.Mtp` (run-loop emission; reporter refactor; new HTML report sink + option provider),
  `test/Raun.Test`, `test/Raun.Mtp.Test`, `samples/AppointmentTests` (showcase).
- **Lineage:** Realizes section **B** of `docs/superpowers/plans/2026-06-06-roadmap-aspire-report-resources.md`.
  Builds on the resourcing **C1** effects already shipped (`ResourceEffect`, `ScenarioContext.Resources`).

## 1. Intent

After a run, emit a single shareable `raun-report.html`: a per-scenario **Gantt-style timeline**
that shows the DAG's parallelism (concurrent steps overlap), a **resource lane** of effect lifelines
(`create → read → edit → delete` per `Type:Key`), and click-to-drill **step detail** (logs, resource
effects, exception / skip reason) — all with real timing. Self-contained (embedded JSON + vanilla
JS/CSS, zero runtime deps), opt-in via an MTP command-line flag, written once at end of run.

## 2. Locked decisions (from brainstorming)

1. **A small typed run-event bus in Raun core** is the event source. The MTP reporter and the HTML
   report are both **subscribers**; future consumers (console, resource view, Aspire) just subscribe.
   The scheduler stays runner-neutral.
2. **Gantt timeline** is the v1 layout (parallelism is Raun's differentiator), with a resource lane
   and click-to-drill detail.
3. **Absolute timestamps, stamped scheduler-side.** `StepResult` gains `StartedAt`; the scheduler
   stamps it via an injected `TimeProvider`. Chosen over subscriber-observed timing because the bus
   fans out serially *and the scheduler awaits it*, so a subscriber's stamp absorbs the preceding
   sink's `PublishAsync` latency (meaningless for sub-ms steps). Absolute (not relative offsets)
   because resource effects already carry an absolute `Timestamp`; one absolute axis overlays steps
   and effects without a separate anchor (`scenarioStart = min(StartedAt)`).
4. **Enable via `--report-html`** (+ `--report-html-filename`, default `raun-report.html`), written
   under MTP's `--results-directory`. Mirrors the built-in `--report-trx`. Off by default; the HTML
   sink only attaches to the bus when the flag is present. (Generic option name — low collision risk
   since Raun owns the framework and the loaded extension set; namespace later if it ever clashes.)

## 3. Architecture

### 3.A Run-event bus — `Raun.Reporting` (core, new)

Runner-neutral pub/sub. Event payloads carry the `ScenarioDefinition` so a single **session-scoped**
subscriber can attribute every step to its scenario (today's reporter is per-scenario *only* because
`StepResult` lacks the scenario id).

```csharp
namespace Raun.Reporting;

public abstract record RunEvent;
public sealed record RunStarted(int ScenarioCount) : RunEvent;
public sealed record ScenarioStarted(ScenarioDefinition Definition) : RunEvent;
public sealed record StepStarted(ScenarioDefinition Definition, StepContext Context) : RunEvent;
public sealed record StepFinished(ScenarioDefinition Definition, StepResult Result) : RunEvent;
public sealed record ScenarioFinished(
    ScenarioDefinition Definition, IReadOnlyList<StepResult> Results) : RunEvent;
public sealed record RunFinished : RunEvent;

public interface IRunEventSink
{
    ValueTask PublishAsync(RunEvent evt);
}

// Ergonomic base: virtual no-op handlers + sealed dispatch, so a sink overrides only what it needs.
public abstract class RunEventSink : IRunEventSink
{
    public ValueTask PublishAsync(RunEvent evt) => evt switch { /* dispatch to On* */ };
    protected virtual ValueTask OnRunStartedAsync(RunStarted e) => default;
    protected virtual ValueTask OnScenarioStartedAsync(ScenarioStarted e) => default;
    protected virtual ValueTask OnStepStartedAsync(StepStarted e) => default;
    protected virtual ValueTask OnStepFinishedAsync(StepFinished e) => default;
    protected virtual ValueTask OnScenarioFinishedAsync(ScenarioFinished e) => default;
    protected virtual ValueTask OnRunFinishedAsync(RunFinished e) => default;
}

public sealed class RunEventBus : IRunEventSink   // fans out to child sinks serially, in order
{
    public RunEventBus(IReadOnlyList<IRunEventSink> sinks);
    public IReadOnlyList<Exception> Failures { get; }  // first error per sink, surfaced post-run
    public ValueTask PublishAsync(RunEvent evt);        // a throwing sink is isolated; others continue
}
```

**Failure isolation.** `RunEventBus.PublishAsync` calls each sink in registration order and `await`s
it; if a sink throws, the bus records the exception in `Failures` and continues to the remaining
sinks and remaining events. The framework inspects `Failures` after the run and logs them as MTP
diagnostics (a broken HTML report must never fail the test run, and must not starve the MTP reporter).

**Ordering contract** (per run; cross-scenario sequential in v1):
`RunStarted` → for each scenario `ScenarioStarted`, then for each executed/skipped step
`StepStarted`→`StepFinished` (serial, scheduler order), then `ScenarioFinished` → … → `RunFinished`.

### 3.B Timestamps — scheduler (core)

- `ScenarioScheduler` ctor gains `TimeProvider? timeProvider = null` → `TimeProvider.System`.
- `StepResult` gains `public required DateTimeOffset StartedAt { get; init; }` (skipped steps use the
  stamp at skip time; `Duration` stays zero).
- `RunNodeAsync` stamps `StartedAt = timeProvider.GetUtcNow()` at the invoke boundary; `FinishedAt`
  is derived (`StartedAt + Duration`), not stored. The scheduler passes `timeProvider` into the
  `ScenarioContext` 6-arg ctor (already supported) so step effects and step timing share one clock.
- Bonus: `RaunStepReporter`'s `TimingProperty` uses the real `StartedAt` instead of anchoring the
  window at finish (`UtcNow - Duration`).

`StepContext` (carried on `StepStarted`) is unchanged — live "in progress" needs no absolute time;
the absolute timeline data lives on the terminal `StepResult` (self-contained `StepFinished`).

### 3.C Run-loop emission + reporter refactor — `Raun.Mtp`

`RaunRunLoop` becomes the bus **emitter**. Its `RunAsync` takes an `IRunEventSink bus` in place of
`(messageBus, producer)`; the MTP-specific reporter is constructed by the framework (§3.E) and handed
in as a sink, so the loop no longer references `IMessageBus`/`IDataProducer` directly. For each run:

1. `RunStarted(selected.Count)`.
2. Per scenario: `ScenarioStarted(def)`; run the scheduler with a thin internal `IStepObserver`
   adapter that republishes `StepStarted`/`StepFinished` onto the bus tagged with `def`; collect the
   scheduler's returned results; `ScenarioFinished(def, results)`.
3. `RunFinished`.

The per-scenario `CancellationTokenSource` ownership and "stop launching after cancel" logic are
unchanged.

`RaunStepReporter` → **`MtpReportSink : RunEventSink`** (session-scoped, constructed once per run):
- `OnScenarioStartedAsync`: compute `ScenarioStepNumbering` labels for `def`, cache keyed by
  `ScenarioId`.
- `OnStepStartedAsync`: publish the in-progress `TestNodeUpdateMessage` (as today).
- `OnStepFinishedAsync`: publish the terminal node (state, timing, output, attachments — as today).

The emitted `TestNodeUpdateMessage`s are **byte-for-byte unchanged**, so existing reporter/run-loop
tests hold (adjusted only for the new construction/wiring seam).

### 3.D HTML report sink + rendering — `Raun.Mtp`

**`HtmlReportSink : RunEventSink`** (constructed only when `--report-html` is set):
- Accumulates an in-memory model from `ScenarioStarted` + `StepFinished` (self-contained results).
- On `RunFinished`: builds the JSON model (§4), injects it into the embedded HTML template, writes the
  file to the resolved path. Best-effort I/O: a write failure is recorded (surfaced via the bus
  `Failures` path), never thrown into the run.

**Renderer** (embedded `report-template.html`, vanilla JS/CSS, single `<script>` JSON blob):
- **Per-scenario Gantt:** `scenarioStart = min(step.StartedAt)`; each step is a bar at
  `(StartedAt - scenarioStart)` of width `Duration`. Steps are laid into lanes so overlapping
  (concurrent group) steps render on separate rows; status drives colour. Phase letter (G/W/T) and
  numbering label prefix each row.
- **Resource lane:** effects grouped by `Type:Key` into one lifeline each, markers placed at
  `(Timestamp - scenarioStart)`, styled by verb (`create`/`read`/`edit`/`delete`).
- **Drill-down:** clicking a bar opens a detail panel — logs (in order), resource effects
  (`verb · Type:Key · +offset` with best-effort `Data.ToString()`), and exception / skip reason.
- **Summary header:** counts (passed/failed/skipped), total wall-clock, per-scenario status.

### 3.E Enablement & wiring — `Raun.Mtp`

- **`HtmlReportOptionsProvider : ICommandLineOptionsProvider`** registers `--report-html` (flag) and
  `--report-html-filename` (single arg, default `raun-report.html`). Registered in
  `RaunTestApplication.RunAsync` via `builder.CommandLine.AddProvider(...)`, so the generated
  `Program.cs` gets it automatically.
- **`RaunTestFramework.OnExecuteAsync`** builds the sink list and the bus:
  ```
  sinks = [ new MtpReportSink(sessionUid, messageBus, this) ]
  if (--report-html present) sinks.Add(new HtmlReportSink(resolvedPath))
  bus = new RunEventBus(sinks)
  await new RaunRunLoop(EnumerateRegisteredScenarios).RunAsync(uids, bus, cancellationToken)
  // after: if (bus.Failures.Count > 0) log each as an MTP diagnostic/warning
  ```
  The flag, filename, and `--results-directory` are read from MTP's `ICommandLineOptions` /
  configuration, captured via the `IServiceProvider` handed to the `RegisterTestFramework` factory.
  *(Exact MTP service/API names — `ICommandLineOptions`, results-directory accessor — to be pinned in
  the plan against the referenced MTP version; behaviour is contract-tested regardless.)*

## 4. JSON model (embedded, and the snapshot surface)

```jsonc
{
  "generatedAtUtc": "…",                  // stamped by the sink (injected TimeProvider)
  "summary": { "passed": 4, "failed": 0, "skipped": 0, "totalMs": 234.1 },
  "scenarios": [{
    "scenarioId": "…", "displayName": "customer books an appointment",
    "classDisplayName": "Appointment booking", "methodName": "Booking",
    "startedAtUtc": "…", "durationMs": 234.1, "status": "passed",
    "steps": [{
      "stepId": "…", "index": 0, "label": "1", "phase": "Given",
      "displayName": "Given patient Jane exists", "status": "passed",
      "offsetMs": 0.0, "durationMs": 42.0, "lane": 0,
      "dependsOn": [], "groupId": null,
      "logs": ["…"],
      "effects": [{ "verb": "Create", "type": "Patient", "key": "Jane",
                    "offsetMs": 1.2, "data": "Patient { Name = Jane }" }],
      "exception": null, "skipReason": null
    }],
    "resources": [{ "type": "Patient", "key": "Jane",
                    "events": [{ "verb": "Create", "offsetMs": 1.2, "stepId": "…" }] }]
  }]
}
```

`lane` and the per-scenario `resources` rollup are **computed by the sink** (not the renderer) so the
layout is deterministic and snapshot-testable. Times are pre-reduced to `ms` offsets from
`scenarioStart` (the renderer does no clock math).

## 5. Testing plan (TDD, behavioural-first)

- **Bus** (`Raun.Test`): fan-out hits sinks in registration order across a full event sequence; a
  throwing sink is isolated (siblings still receive every event; `Failures` records it; run completes).
- **Scheduler** (`Raun.Test`): `StartedAt` comes from an injected `FakeTimeProvider`; two concurrent
  group steps yield overlapping `[StartedAt, StartedAt+Duration)` windows; skipped step carries a
  `StartedAt` and zero `Duration`.
- **Run loop** (`Raun.Mtp.Test`): a fake sink records the exact event ordering for a linear scenario,
  a parallel-group scenario, and a failure-with-skips scenario.
- **HTML sink** (`Raun.Mtp.Test`): from a synthesized event stream, **Verify-snapshot the JSON model**
  (deterministic — fake clock, fixed durations) and assert key HTML structure (one Gantt section per
  scenario, a bar per step, a lifeline per resource identity, drill-down contains logs + exception).
  Assert **no file is written when the flag is absent**, and the file lands at the resolved path when
  present.
- **Option provider** (`Raun.Mtp.Test`): `--report-html` recognized; `--report-html-filename`
  overrides; default resolves under `--results-directory`.
- **Regression:** existing reporter/run-loop/framework tests stay green (unchanged MTP messages).
- **Sample:** `samples/AppointmentTests` already exercises logs + effects; a manual `--report-html`
  run is the end-to-end smoke check (a real Gantt with overlapping `PatientExists`/`AvailableSlot`).

## 6. Out of scope for v1

- Streaming/incremental write (end-of-run only).
- Binary / file-path attachments (string attachments only, as today) — coordinate with roadmap A/C.
- Rendering arbitrary `ResourceEffect.Data` beyond best-effort `ToString()`.
- Resource **locking** visuals (Wounded/Retried/Contended) — that's resourcing **C2**.
- Auto-opening the report; theming/configuration knobs.
- Moving `RaunRunLoop` to core (it becomes nearly runner-neutral, but `SelectScenarios` still uses
  the MTP uid format via `RaunDiscoverer.MakeUid`); leave it in `Raun.Mtp`.

## 7. Risks / open points

- **MTP API specifics** for option reading + results-directory resolution must be pinned against the
  referenced MTP version during the plan (contract-tested behaviour insulates us).
- **Reporter refactor churn**: per-scenario → session-scoped sink touches run-loop wiring and its
  tests; mitigated by keeping emitted messages identical.
- **Lane packing** for the Gantt is a small interval-layout algorithm; keep it in the sink (tested)
  rather than the renderer.
- **Sample timings are sub-ms** (`Task.Yield`), so the sample Gantt bars are tiny; the layout is
  validated by tests with synthetic durations, and the feature earns its keep on real (I/O) workloads.

## 8. File-by-file change list

**New (core):** `src/Raun/Reporting/RunEvent.cs`, `IRunEventSink.cs`, `RunEventSink.cs`,
`RunEventBus.cs`.
**New (mtp):** `src/Raun.Mtp/HtmlReport/HtmlReportSink.cs`, `HtmlReportModel.cs`,
`HtmlReportOptionsProvider.cs`, `report-template.html` (embedded resource).
**Changed (core):** `ScenarioScheduler.cs` (TimeProvider + stamp), `Model/StepResult.cs` (`StartedAt`).
**Changed (mtp):** `RaunRunLoop.cs` (emit to bus), `RaunStepReporter.cs` → `MtpReportSink.cs`
(reporter→sink), `RaunTestFramework.cs` (build bus/sinks, log `Failures`), `RaunTestApplication.cs`
(register option provider).
**Tests:** new bus/scheduler tests in `Raun.Test`; new run-loop/HTML-sink/option tests +
snapshots in `Raun.Mtp.Test`.
