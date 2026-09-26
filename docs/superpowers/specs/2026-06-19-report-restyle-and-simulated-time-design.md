# Simulated timeline + report restyle — design spec

Date: 2026-06-19
Status: proposed
Scope: two independent deliverables shipped together —
(A) a simulated, parallel-correct timeline driving the sample report, and
(B) a modern auto light/dark restyle of the embedded HTML report.

---

## 1. Intent

The HTML report's per-scenario Gantt is the headline feature, but the sample
(`samples/AppointmentTests`) produces sub-millisecond, meaningless bars: every DSL step is
`await Task.Yield()`, so durations are noise and the timeline is a row of dots. We want the sample to
render a *realistic* timeline — overlapping parallel arrange steps, a join that waits for the slowest
dependency, one scenario long enough (~1s+) to exercise the ruler's auto-scaling into seconds — and we
want it **without real waiting** (fast, deterministic tests) and **correct under parallelism**.

Separately, the report's look is a bare-bones prototype. A polished, fully self-contained reference
mockup already exists at `.git/sdd/mockup/report-mockup.html`; we port its CSS/JS into the embedded
template, preserving the JSON-injection contract and the model field names.

The two tasks are orthogonal: A changes *when* bars land on the timeline (runtime/scheduler + sample);
B changes *how* the timeline is drawn (one template file + its tests). Neither touches the
`HtmlReportModel` record shapes or `HtmlReportModelBuilder` — simulated times land on the same single,
consistent timeline the builder already reduces to ms offsets, so the builder stays timeline-agnostic.

---

## 2. Task A — simulated timeline

### 2.1 Why a single shared scalar clock is wrong for a parallel DAG

The naive design is "one mutable clock per scenario; each step body calls `Advance(delta)`; the
scheduler measures a step's duration as `clockAfter - clockBefore`." This is **incorrect** the moment two
steps run concurrently, which is exactly what the scheduler does for independent ready nodes
(`ScenarioScheduler` launches all dependency-satisfied nodes up to `maxParallelism`, and the sample's
arrange steps are independent siblings).

Two concrete failures:

1. **Durations corrupt each other.** Sibling steps A and B run concurrently. Each does
   `start = clock.Now; …; Advance(myDelta); …; dur = clock.Now - start`. Because `Advance` mutates the
   *shared* clock, B's advances inflate A's measured span and vice-versa. The measured duration is no
   longer the step's own contribution — it is the sum of whatever advances happened to interleave. Worse,
   the interleaving is nondeterministic, so the durations (and therefore the snapshot-tested model) become
   nondeterministic — defeating the entire point of *simulated* time.

2. **Joins sum instead of max.** With one shared clock, after siblings A (+40ms) and B (+55ms) both run,
   the clock reads `base + 95` (the advances *added up*), not `base + 55` (the wall position where both
   have finished). A join step depending on A and B would then start at +95 instead of +55. The DAG's
   parallelism is erased: concurrent work appears serialized, and the Gantt — whose whole job is to *show*
   the parallel structure — would show a lie.

The root cause: a scalar clock conflates "elapsed in this step" with "wall position in the scenario."
In a DAG those are different quantities. A step's duration is local to the step; a step's start is
`max` over its dependencies' finishes; siblings share a start and overlap.

### 2.2 The fix — per-step clock + scheduler-computed start offsets

Give **each step its own clock**, seeded by the scheduler at that step's computed start offset. A step
only ever advances *its own* clock, so durations can't corrupt each other regardless of interleaving. The
scheduler — which already owns the DAG and knows each node's dependencies — computes start offsets as the
`max` over dependency finishes, so joins are correct and siblings overlap by construction.

Quantities tracked in sim mode, all as `TimeSpan` offsets from a single captured `base` instant:

- `simStartOffset[node]` = `node.DependsOn.Any ? max(simFinishOffset[dep] for dep in deps) : TimeSpan.Zero`
- the node's per-step clock is seeded at `base + simStartOffset[node]`
- `StartedAt = base + simStartOffset[node]`
- `Duration` = that step clock's *total advanced* (read after the body completes)
- `simFinishOffset[node]` = `simStartOffset[node] + Duration`

Because nodes are launched only after all dependencies are `Passed` (and skips are resolved only after
all dependencies are terminal), every dependency's `simFinishOffset` is already known when a node's offset
is computed — no extra ordering machinery is needed; it rides the scheduler's existing readiness gate.

The earliest node in any DAG has no dependencies ⇒ `simStartOffset = Zero` ⇒ `StartedAt = base`. So the
scenario's `start` anchor (which `HtmlReportModelBuilder` computes as `min(StartedAt)`) is exactly `base`,
and every step/effect offset the builder derives (`StartedAt - start`, `effect.Timestamp - start`) is the
clean simulated offset. The builder is unchanged.

### 2.3 `SimulatedClock` (A1)

New file `src/Raun/Scheduling/SimulatedClock.cs`, `public sealed class SimulatedClock : TimeProvider`,
namespace `Raun.Scheduling`. Thread-safe via `Interlocked`.

- ctor `SimulatedClock(DateTimeOffset baseInstant)` captures the seed instant.
- internal field `long _advancedTicks` (the total advanced, in `TimeSpan` ticks), mutated only via
  `Interlocked.Add`.
- `public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _advancedTicks));` (the
  total-advanced accessor the scheduler reads for `Duration`). Name it `Elapsed` (alias `Advanced` if
  preferred — pick one, document it).
- `public void Advance(TimeSpan delta)`: rejects negative deltas (`ArgumentOutOfRangeException`), then
  `Interlocked.Add(ref _advancedTicks, delta.Ticks)`.
- `public override DateTimeOffset GetUtcNow() => _base + Elapsed;`
- `public override long TimestampFrequency => TimeSpan.TicksPerSecond;` and
  `public override long GetTimestamp() => _baseTimestamp + Interlocked.Read(ref _advancedTicks);`
  (with `_baseTimestamp` an arbitrary fixed origin, e.g. 0). With frequency = `TicksPerSecond`,
  `GetElapsedTime(t0, t1)` reduces to the tick delta as a `TimeSpan`, so `GetElapsedTime` is consistent
  with `Advance` for any user code that calls it. (The scheduler itself reads `Elapsed` directly for
  duration; `GetTimestamp` consistency is required by the task and is the correct contract for a
  `TimeProvider`.)

### 2.4 `ScenarioContext.SimulateElapsed` (A2)

`ScenarioContext` already receives a `TimeProvider?` via its 6-arg constructor and forwards it to
`ResourceContext` (effect timestamps come from it). Add:

- store the effective `TimeProvider` on a private field (the same instance passed to `ResourceContext`,
  so step timing and resource-effect stamps share one clock).
- `public void SimulateElapsed(TimeSpan delta)`: if the stored provider is a `SimulatedClock`, call
  `Advance(delta)`; otherwise no-op. So in real runs (provider is `TimeProvider.System`) the sample's
  `SimulateElapsed` calls do nothing — real timing is untouched.

Both existing constructors stay byte-for-byte: the 4-arg delegates to the 6-arg with
`timeProvider: null` (today's behavior — `ResourceContext` then uses `TimeProvider.System`); the 6-arg
keeps its parameter list and resolver-precedence logic. We only add a field assignment and one method.
`SimulateElapsed` is a no-op when `timeProvider` is null or non-simulated, so the 4-arg path is safe.

### 2.5 `ScenarioScheduler` simulated mode (A3)

Add an opt-in ctor parameter: `ScenarioScheduler(int maxParallelism = 0, TimeProvider? timeProvider = null, bool simulatedTime = false)`.
Store `_simulatedTime`. When false, **every existing code path is unchanged** (today's
`StartedAt = _timeProvider.GetUtcNow()` at launch + `Stopwatch.Elapsed` duration; skip path
`StartedAt = _timeProvider.GetUtcNow()`). This keeps the 222 existing tests byte-identical — they all
construct the scheduler without the flag.

When `simulatedTime: true`:

- capture `var simBase = _timeProvider.GetUtcNow();` **once** at the start of `RunAsync` (the injected
  provider supplies the base instant; tests inject a fixed provider for determinism).
- maintain `TimeSpan[] simFinishOffset` (or a dictionary keyed by node index), filled as nodes finish.
- helper `TimeSpan StartOffsetOf(ScenarioNode node)` = `node.DependsOn` empty ? `Zero` :
  `node.DependsOn.Max(d => simFinishOffset[d])`.
- in `RunNodeAsync` (sim branch): `var startOffset = StartOffsetOf(node);` create
  `var stepClock = new SimulatedClock(simBase + startOffset);` build the `ScenarioContext` with
  `timeProvider: stepClock` (so the body's `SimulateElapsed` and any `ctx.Resources.*` stamps share this
  clock). Run the body. `StartedAt = simBase + startOffset; Duration = stepClock.Elapsed;` and record
  `simFinishOffset[node] = startOffset + stepClock.Elapsed`. No `Stopwatch`, no `_timeProvider.GetUtcNow()`
  per step. (Per-step timeout handling: in sim mode a `node.Timeout` is compared against simulated
  elapsed; v1 may simply skip the real `Task.Delay` race in sim mode since no body actually blocks — the
  sample uses no `[StepTimeout]`. Document this as a sim-mode limitation rather than wiring a simulated
  timeout race.)
- skip path (`ApplySkipAsync`, sim branch): a skip is resolved only after all deps are terminal, so
  `StartOffsetOf(node)` is computable. Set `StartedAt = simBase + StartOffsetOf(node)`, `Duration` stays
  `0` (default), and `simFinishOffset[node] = StartOffsetOf(node)`. This places a skipped step at the
  trailing edge of its failed/skipped dependency (matching the mockup's skipped `s4-4` sitting at the
  failed step's finish) and lets its own dependents compute a sane offset.

Result: parallel siblings (same `simStartOffset`) overlap; a join starts at `max(dep finishes)`, never the
sum; durations are each step's own advance and can't corrupt across threads. The single seed instant means
all offsets land on one consistent timeline.

### 2.6 Effect placement note

The generator inserts auto `ctx.Resources.{Verb}(…)` calls *after* the user's step call statement (see
`ScenarioEmitter` — "the line-mapped CALL statement stays FIRST; any `ctx.Resources.*` calls are inserted
AFTER"). So for steps whose effects come from `[return: Creates]` / `[Reads]` role attributes, the effect
is stamped after the body returns — i.e. at `startOffset + fullStepDuration`, the bar's trailing edge.
That is correct and consistent; finer mid-bar effect placement (as hand-authored in the mockup) would
require the author to interleave explicit `await ctx.Resources.X(...)` calls between `SimulateElapsed`
calls inside the body. The sample relies on the role-attribute path, so its effects sit at bar ends; this
is acceptable and should be noted, not worked around.

### 2.7 Opt-in wiring (A4) — sample-local, default false everywhere

The flag threads through four seams, defaulting `false` so real apps using the generated `Program` are
unaffected:

1. `RaunTestApplication.RunAsync(string[] args, Action<ITestApplicationBuilder>? configure = null, bool simulateTime = false)`
   — capture `simulateTime` in the `RegisterTestFramework` factory closure:
   `(_, sp) => new RaunTestFramework(sp, simulateTime)`.
2. `RaunTestFramework` — add a `(IServiceProvider services, bool simulateTime)` ctor that stores the flag;
   keep the parameterless ctor (tests) and the `(IServiceProvider)` ctor (delegates with
   `simulateTime: false`). Thread the flag into the `RaunRunLoop` it constructs in `OnExecuteAsync`.
3. `RaunRunLoop` — accept the flag (ctor param, default false) and use it in `DefaultRunScenario` so it
   builds `new ScenarioScheduler(simulatedTime: simulateTime)`. The injected-`RunScenario` test seam is
   unchanged (tests that substitute `runScenario` never touch the scheduler).
4. `ScenarioScheduler` — the `simulatedTime` ctor param from A3.

The generated `RaunProgram.g.cs` (emitted when `RaunGenerateProgram` is true) calls the 2-arg
`RunAsync(args)` ⇒ `simulateTime` defaults false ⇒ production behavior unchanged.

### 2.8 Sample changes (A5)

Generator prerequisite — **verified, supported**: `SymbolHelpers.WantsContext(method, suppliedArgCount)`
returns true when a step method's last parameter is `Raun.ScenarioContext` and the call site supplies all
*other* parameters; `ScenarioParser.BuildCallText` then appends `__ctx` (the step-invoke lambda's context
parameter) as the trailing argument. The trailing `ScenarioContext` is excluded from resource-claim
binding (`ScenarioAnalyzer`: "the `Raun.ScenarioContext` param is naturally excluded"). So adding a
trailing `ScenarioContext ctx` to each DSL step compiles and binds correctly. (Gap to close in the TDD
plan: there is no *dedicated* generator test asserting "a step method declaring a trailing
`ScenarioContext` parameter emits a call passing `__ctx`"; the `__ctx` token in existing snapshots is the
lambda parameter, always present. Add one to lock the capability the sample now depends on.)

Concrete sample edits:

- `samples/AppointmentTests/AppointmentTests.csproj`: add
  `<RaunGenerateProgram>false</RaunGenerateProgram>` so we own `Main`.
- New `samples/AppointmentTests/Program.cs`:
  ```csharp
  return await Raun.Mtp.RaunTestApplication.RunAsync(args, simulateTime: true);
  ```
  (top-level statement file, or explicit `Main` returning the `int`).
- `samples/AppointmentTests/AppointmentDsl.cs`: give each step a trailing `ScenarioContext ctx`, drop the
  `await Task.Yield()` bodies, and call `ctx.SimulateElapsed(TimeSpan.FromMilliseconds(...))`. Durations are
  fixed per step method (steps are shared across scenarios), chosen so the dependency *critical path*
  produces a good spread. Example, tweakable:

  | step                     | SimulateElapsed |
  |--------------------------|-----------------|
  | DatabaseIsClean          | 60 ms           |
  | PatientExists            | 180 ms          |
  | AvailableSlot            | 350 ms          |
  | UserExists               | 160 ms          |
  | CreateAppointment        | 600 ms          |
  | AppointmentExists        | 120 ms          |
  | ImportUsers              | 240 ms          |
  | ImportShouldContainUsers | 90 ms           |

  Resulting scenario totals (critical path; independent siblings overlap):
  - "customer books an appointment": `max(180,350)+600+120` ≈ **1.07 s** (ruler auto-scales to seconds).
  - "customer books with parallel arrange": `60+max(180,350)+600+120` ≈ **1.13 s** (seconds).
  - "bulk user import": `max(160,160)+240+90` ≈ **490 ms** (ruler in ms).
  - "bulk user import via LINQ": `max(160,160,160)+240+90` ≈ **490 ms** (ms).

  This guarantees ≥1 scenario over ~1s (seconds ruler) *and* sub-second scenarios (ms ruler), exercising
  the auto-scaling both ways, with overlapping parallel arrange in every scenario. Bodies keep returning
  real domain objects so all asserts still pass under simulated mode.

`SimulateElapsed` is a no-op when the sample is run by anything that doesn't pass `simulateTime: true`
(e.g. plugged into another harness), so the change is safe.

---

## 3. Task B — restyle the report

Replace `src/Raun.Mtp/HtmlReport/report-template.html` by porting `.git/sdd/mockup/report-mockup.html`,
which was authored against the exact `HtmlReportModel` field names and is fully self-contained.

### 3.1 Hard constraints preserved

- **Self-contained**: inline `<style>` and `<script>` only. Zero external URLs / CDNs / web-fonts /
  `@import`. Fonts are `system-ui …` and `ui-monospace …` stacks only. (The mockup already complies;
  verify no stray asset slips in during the port.)
- **JSON-injection contract**: keep exactly one
  `<script id="model" type="application/json">…</script>` whose body is the literal token
  `/*__RAUN_REPORT_JSON__*/`. `HtmlReportSink` string-replaces that token (`JsonToken` constant) with the
  serialized, camelCase, indented model. The mockup ships an inline sample JSON *inside* that element for
  standalone preview — in the template that element's body must be **only** the token (no sample JSON), or
  the sink's single `Replace` would leave the sample behind. This is the one deliberate edit when porting
  the mockup's `<script id="model">` block.
- **Model field names**: the renderer reads exactly `generatedAtUtc`; `summary{passed,failed,skipped,totalMs}`;
  `scenarios[{scenarioId,displayName,classDisplayName,methodName,startedAtUtc,durationMs,status,
  steps[{stepId,index,label,phase,displayName,status,offsetMs,durationMs,lane,dependsOn,groupId,logs,
  effects[{verb,type,key,offsetMs,data}],exception,skipReason}],resources[{type,key,
  events[{verb,offsetMs,stepId}]}]}]`. These match `HtmlReportModel` under camelCase serialization. Do not
  rename anything.

### 3.2 Design carried over from the mockup

- Dashboard app bar: brand + generated line; stat chips for passed / failed / skipped counts, total ms
  ("wall clock"), and generatedAt.
- Scenario cards with a status-accent left edge (`--edge` = pass green / fail red / skip amber-grey).
- Per-scenario Gantt with an **auto-scaling ruler**: `niceAxis(maxMs)` picks a nice step (1/2/5/10 ×10ⁿ)
  and `axisMax`; the track is a fixed `TRACK = 900px` and `px = TRACK/axisMax` scales each scenario
  independently so a ~300ms and a ~1.8s scenario both fit. `fmtTick` switches ms↔s by `axisMax`.
- One row per lane; overlapping parallel bars colored by status (pass green, fail red, **skip grey/amber**),
  each carrying a phase glyph (`phase[0]`) + step name with ellipsis + `title` hover for long names.
- A resource lane: one verb-colored lifeline per `Type:Key` with markers per event, plus a small verb
  legend (create / read / edit / delete).
- Click a bar → expand a **structured** drill panel: ordered `<ol>` logs; effects rendered as
  "verb · Type:Key · +offset (data)"; monospace `<pre>` exception; grey skip note. Single-open behavior
  (opening one closes others).
- `?theme=light|dark` override: inline JS reads the query param and sets
  `document.documentElement.dataset.theme`; CSS `:root[data-theme="light"|"dark"]` blocks override the
  `prefers-color-scheme` palette, so screenshots are deterministic.

### 3.3 Deviations from the mockup to apply during the port

- **Drop numeric lane gutters.** The mockup sets `g.textContent = "L" + lane;` for lane rows. The
  requirement says do *not* label rows L0/L1. In the port, leave the lane-row gutter blank (keep the
  gutter *column* so lane tracks stay aligned with the ruler "ms"/"s" gutter and the resource `Type:Key`
  gutters — just emit no text). Ruler gutter ("ms"/"s") and resource gutters (`Type:Key`) stay.
- **No dependency arrows.** The mockup draws none; keep it that way (`dependsOn` stays in the model for
  future use but is not visualized).
- **Remove the screenshot-only pre-expand.** The mockup auto-opens `sc-4`'s failing step
  (`pre.open("s4-3")`) for its canned data. The template renders real runs with arbitrary ids, so drop the
  hard-coded pre-expand (or guard it). Drill panels start closed.
- **Model element body = token only**, per §3.1.

### 3.4 Test updates

`test/Raun.Mtp.Test/HtmlReportSinkTests.cs` asserts substrings against the rendered HTML. After the
restyle the structural asserts still hold and need no change in spirit, but re-verify each against the new
markup:

- `Assert.Contains("books", html)` — the scenario `displayName` ("books") still appears verbatim (rendered
  via `esc(sc.displayName)` into the card title). ✓ keep.
- `Assert.Contains("\"scenarioId\": \"scn\"", html)` — asserts the indented camelCase JSON blob is present;
  unchanged because serialization is unchanged. ✓ keep.
- `Assert.DoesNotContain("__RAUN_REPORT_JSON__", html)` — token replaced; the new template still carries
  exactly one token and the sink still replaces it. ✓ keep. (This is the assertion that would break if the
  port accidentally left the mockup's sample JSON in the model element — that sample JSON contains the
  token-free literal text, but the real risk is leaving the token un-replaced or duplicated; the port must
  keep exactly one token and nothing else in the element.)

The Verify snapshot in `HtmlReportModelBuilderTests.cs`
(`…Builds_the_expected_json_model.verified.txt`) is on the **model**, not the HTML — it must remain
passing **unchanged**. Neither Task A nor Task B alters the model record shapes or the builder, so this
snapshot is untouched. (Task A changes *runtime* timings in the sample only; the builder's unit test feeds
synthetic `StepResult`s, so it is unaffected.)

---

## 4. Invariants / non-goals

- Real runs (`simulatedTime: false`, the default everywhere except the sample's own `Program.cs`) are
  **byte-for-byte unchanged**: same `StartedAt = _timeProvider.GetUtcNow()` at launch, same
  `Stopwatch.Elapsed` duration, same skip stamping. The 222 existing tests stay green.
- `HtmlReportModel` / `ReportScenario` / `ReportStep` / … field names and `HtmlReportModelBuilder` logic
  are unchanged. No model field renames.
- 0-warning build (analyzers are errors: IDE0005, CA1822, CA1859, CA1308, …). New public members carry
  XML docs; `SimulatedClock` overrides are documented.
- Non-goal: simulated per-step *timeouts* (no body actually blocks in sim mode); simulated cross-scenario
  scheduling (loop stays sequential across scenarios). Sample uses neither.

---

## 5. File-by-file change list

Task A (runtime + wiring + sample):
- `src/Raun/Scheduling/SimulatedClock.cs` — **new**. `SimulatedClock : TimeProvider`, `Interlocked`,
  `Advance`, `Elapsed`, `GetUtcNow`/`GetTimestamp`/`TimestampFrequency`.
- `src/Raun/ScenarioContext.cs` — store the `TimeProvider`; add `public void SimulateElapsed(TimeSpan)`;
  both ctors otherwise unchanged.
- `src/Raun/Scheduling/ScenarioScheduler.cs` — add `bool simulatedTime = false` ctor param; sim branch in
  `RunAsync`/`RunNodeAsync`/`ApplySkipAsync` (base capture, `simFinishOffset`, `StartOffsetOf`, per-step
  clock, `StartedAt`/`Duration` from sim quantities). Real branch unchanged.
- `src/Raun.Mtp/RaunTestApplication.cs` — add `bool simulateTime = false` to `RunAsync`; pass into the
  framework factory.
- `src/Raun.Mtp/RaunTestFramework.cs` — add `(IServiceProvider, bool)` ctor + `_simulateTime` field;
  thread into `RaunRunLoop`. Keep `()` and `(IServiceProvider)` ctors.
- `src/Raun.Mtp/RaunRunLoop.cs` — accept `simulateTime` (default false); `DefaultRunScenario` builds
  `new ScenarioScheduler(simulatedTime: simulateTime)`.
- `samples/AppointmentTests/AppointmentTests.csproj` — `<RaunGenerateProgram>false</RaunGenerateProgram>`.
- `samples/AppointmentTests/Program.cs` — **new**; `Main` → `RunAsync(args, simulateTime: true)`.
- `samples/AppointmentTests/AppointmentDsl.cs` — trailing `ScenarioContext ctx` per step; replace yields
  with `ctx.SimulateElapsed(...)`.

Task B (template + tests):
- `src/Raun.Mtp/HtmlReport/report-template.html` — **replaced** by the ported mockup (token-only model
  element; lane gutters blanked; pre-expand removed). Stays an `EmbeddedResource` (csproj already includes
  it; no csproj change).
- `test/Raun.Mtp.Test/HtmlReportSinkTests.cs` — re-verify/keep the three substring asserts against the new
  markup.

New tests:
- `test/Raun.Test/SimulatedClockTests.cs` — **new**.
- `test/Raun.Test/ScenarioContextTests.cs` — add `SimulateElapsed` cases.
- `test/Raun.Test/SchedulerTests.cs` — add sim-mode cases.
- `test/Raun.Generator.Test/...` — add a trailing-`ScenarioContext` emit assertion.

---

## 6. TDD test plan (behavioral, test-first)

**SimulatedClock** (`test/Raun.Test/SimulatedClockTests.cs`):
1. `GetUtcNow` returns `base` before any advance; `base + delta` after one `Advance`; accumulates across
   advances.
2. `GetElapsedTime(GetTimestamp_before, GetTimestamp_after)` equals the total advanced (consistency of the
   timestamp path with `Advance`).
3. `Elapsed` equals the sum of advances.
4. Concurrency: `Parallel.For(0, 1000, _ => clock.Advance(1ms))` ⇒ `Elapsed == 1000ms` (Interlocked
   correctness — mirrors the existing `Logging_is_safe_under_concurrency` style).
5. Negative delta throws `ArgumentOutOfRangeException`.

**ScenarioContext.SimulateElapsed** (extend `ScenarioContextTests.cs`):
6. With a `SimulatedClock` provider (6-arg ctor), `SimulateElapsed(50ms)` advances it (observe via the
   clock / via a recorded effect timestamp).
7. With `TimeProvider.System` (or null/4-arg ctor), `SimulateElapsed` is a no-op (no throw, real time
   untouched).

**Scheduler sim mode** (extend `SchedulerTests.cs`, injecting a fixed-base `TimeProvider`):
8. **Duration = own advance**: a single step whose body calls `ctx.SimulateElapsed(40ms)` reports
   `Duration == 40ms` and `StartedAt == base`.
9. **Siblings overlap, join = max not sum**: clean → (A advances 80ms ∥ B advances 55ms) → join. Assert
   A.Started == B.Started == base+cleanDur; join.Started == base + cleanDur + max(80,55); and
   join.Started ≠ base + cleanDur + (80+55). This is the core anti-regression for the scalar-clock bug.
10. **Determinism under real parallelism**: run case 9 many times (or with `maxParallelism` unbounded) and
    assert identical durations/offsets every time (no cross-step corruption).
11. **Skip placement**: A fails (advances some), B depends on A ⇒ B skipped with `StartedAt == base +
    A.finishOffset`, `Duration == 0`.
12. **Real mode unchanged**: constructing the scheduler without `simulatedTime` leaves `StartedAt`/
    `Duration` on the System/Stopwatch path — guarded implicitly by all 222 existing tests staying green;
    optionally one explicit test that `SimulateElapsed` in a body is a no-op under real mode.

**Generator** (`test/Raun.Generator.Test`):
13. A step method declaring a trailing `ScenarioContext` parameter (with the call site supplying the other
    args) emits an invoke that passes `__ctx` as the final argument and excludes the context from resource
    claims. (Snapshot or substring assertion.)

**Report sink** (`HtmlReportSinkTests.cs`): the three substring asserts pass against the new template;
`Empty_run_still_writes_a_valid_report` and the write-failure test are template-shape-agnostic and stay
green.

**Model snapshot** (`HtmlReportModelBuilderTests.cs`): unchanged, must stay green (proves the model/builder
were not touched).

**Sample smoke**: `dotnet build` of `samples/AppointmentTests` is 0-warning; running it writes
`raun-report.html` whose JSON shows non-trivial, overlapping, max-joined durations with ≥1 scenario
> ~1s. Full suite `dotnet test Raun.slnx -c Debug` green.

Sequence: write SimulatedClock tests → clock; context tests → `SimulateElapsed`; scheduler sim tests →
scheduler sim branch (case 9 is the headline red→green); generator test → confirm capability; then wire
A4; then sample (A5); then template port (B) + re-verify sink asserts. Commit per task, no trailers.
