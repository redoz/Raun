# Tracing — Design

- **Date:** 2026-09-06
- **Status:** Built (core, run loop, MTP output, HTML report). Aspire sample wiring in the same day.
- **Supersedes:** `2026-09-04-otel-step-correlation-design.md` (shelved). That design tried to
  *receive* the system under test's telemetry and stitch it back into steps — a collector problem
  (batching, late arrival, held publication). This one makes Raun a *producer*: the SUT joins Raun's
  traces through ordinary W3C propagation, and the stitching happens wherever telemetry already lands.
- **Scope:** `src/Raun` (`RaunTelemetry`, spans in the scheduler, events from the context),
  `src/Raun.Mtp` (run and scenario spans, `[trace]` output line, report fields), the Aspire sample.
- **Non-goals:** exporting (Raun emits; the consumer subscribes), an OTLP receiver, sampling policy,
  correlating incoming logs.

## Span tree

```
raun.run                          root, tiny: raun.run=<id>, raun.scenario.count
 ↖ link
<scenario display name>           ROOT per scenario, linked to the run span
 ├─ <step display name>           child, Activity.Current while the step body runs
 │   └─ (SUT spans)               joined via traceparent on outgoing HttpClient calls
 ├─ <step display name>
 └─ Teardown                      child
```

**Why the scenario is the trace boundary.** A scenario is one causal unit of work: the steps are its
parts, parallel steps overlap as siblings, and the SUT's spans hang under the step that provoked
them. That is the picture to open in a viewer, one trace id per scenario. Making the *run* the root
would put every scenario, step, and SUT span into one trace: a long sequential chain, thousands of
spans, and head sampling deciding for the whole suite at once. So the run gets its own small root
span, each scenario span **links** to it, and `raun.run` on every scenario span makes "everything
from this run" a query instead of a giant waterfall.

**Why parent-child, not links, between step and scenario.** Links would give one trace per step and
force hopping through span details to reassemble the story.

**Skipped and not-taken steps** get no span. A span for work that never ran is noise in a trace
viewer; the scenario span records a `raun.step.skipped` event with the step, status, and reason.

## Attributes and events

OpenTelemetry test semantic conventions where they exist; `raun.*` for the rest.

| On | Attribute | Value |
|---|---|---|
| scenario | `test.suite.name` | scenario display name |
| scenario | `test.suite.run.status` | `success`, `failure` (any step failed), `aborted` (cancelled), `skipped` (preflight failed) |
| scenario | `raun.scenario`, `raun.run`, `raun.scenario.method`, `code.file.path`, `code.line.number` | |
| step | `test.case.name` | step display name |
| step | `test.case.result.status` | `pass`, `fail`, `skipped` (cancelled mid-step) |
| step | `test.suite.name`, `raun.scenario`, `raun.step`, `raun.step.phase`, `raun.step.operation`, `code.file.path`, `code.line.number` | |

Failure: `ActivityStatusCode.Error` with the exception message, plus the exception recorded via
`Activity.AddException`. Teardown: same shape, one exception per failed cleanup.

| Event | On | Tags |
|---|---|---|
| `log` | step | `message` |
| `raun.resource` | step | `verb`, `identity`, and `conflict` (the ledger's message) when the claim was refused |
| `raun.step.skipped` | scenario | `step`, `raun.step`, `status`, `reason` |

Events are added only when `IsAllDataRequested` is true, so an unsampled span costs nothing.

## Mechanics

- `RaunTelemetry.Source` is a single `ActivitySource("Raun", <informational version>)`. With no
  listener, `StartActivity` returns null and every call site is a null check.
- Run loop: starts the run span, then sets `Activity.Current = null` so nothing parents under it.
  Each scenario span is started with `parentContext: default` and Current null, so it is a root, and
  with an `ActivityLink` to the run span's context. It is `using`-scoped around the whole scenario
  including result publication.
- Scheduler: `RunNodeAsync` starts the step span (parents to the ambient scenario span), attaches it to
  the `ScenarioContext`, and sets result tags before the `using` disposes it. Teardown does the same
  in `RunTeardownAsync`. `ApplyTerminalAsync` adds the skipped event to `Activity.Current`.
- `ScenarioContext.Log` mirrors the line as a `log` event; resource events arrive through the
  `ResourceContext` observer and become `raun.resource` events (and `[resource] …` log lines).
- Spans use real time even in simulated-time mode; a trace viewer wants wall time. The report's
  timeline stays on the simulated clock.
- `StepResult.TraceId`/`SpanId` are set when a span was recorded. MTP output ends with
  `[trace] <traceId> span <spanId>`; the HTML report shows a Trace line under the step's logs.

## Consuming

Raun never exports. A consumer subscribes:

```csharp
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .ConfigureResource(r => r.AddService("MySuite"))
    .AddSource(RaunTelemetry.SourceName)
    .AddHttpClientInstrumentation()   // client spans between a step and the server
    .AddOtlpExporter()                // OTEL_EXPORTER_OTLP_ENDPOINT
    .Build();
```

With the SUT also exporting to the same collector, one trace shows the step span, the HTTP client
span, and the server's spans.

### Aspire sample

`DistributedApplicationTestingBuilder` disables the dashboard by default, and with it the OTLP
endpoint Aspire would otherwise hand to resources. The sample therefore exports **when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set**, on both sides: the test process builds a tracer provider for
the Raun source and HttpClient, and the AppHost forwards the variable to the API, which exports its
ASP.NET Core spans. Point both at a standalone dashboard:

```bash
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard:latest
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj
```

Open the dashboard's Traces page: one trace per scenario, API spans under the step that called them.

**Follow-up, not done:** running the dashboard *inside* the test run (`DisableDashboard = false` on
the testing builder) and discovering its OTLP endpoint from the built application, so no external
process is needed. Needs an experiment against the testing builder's port randomization.

## Alternatives considered

- **Receive OTLP and correlate back into steps** (the shelved design): a collector's job; cost was
  losing live per-step progress.
- **Run span as root of everything:** giant traces, all-or-nothing sampling.
- **Links from step to scenario:** one trace per step, story scattered.
- **Spans for skipped steps:** noise; an event on the scenario span carries the same information.
- **Simulated timestamps on spans:** a viewer would show 2026-06-19 for a run made today.

## Amendment 2026-09-06 — concurrent scenarios

Scenario spans gain `raun.scenario.uses` (`Type:Mode` list, absent when none), and, when admission
held the scenario back behind a contended resource, `raun.scenario.waited_ms` and
`raun.scenario.waited_for`. Scenario spans stay roots linked to the run span; concurrent scenarios
are separate traces (`RunLoopTests.Concurrent_scenarios_keep_their_own_traces_and_step_parents`).
See `2026-09-06-concurrent-scenarios-design.md`.
