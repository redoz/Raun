# Simulated timeline + report restyle — TDD implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking. Every task is test-first: write the failing behavioral test(s), watch them fail, implement, watch
> them pass, then verify a **0-warning** build (analyzers are errors: IDE0005, CA1822, CA1859, CA1308, …) and
> the named test slice. Commit per task with `git` and **no trailers** (no `Co-Authored-By` / "Generated with").

**Source spec:** `docs/superpowers/specs/2026-06-19-report-restyle-and-simulated-time-design.md` (§ refs below).

**Goal.** Ship two orthogonal things:
- **Task A — simulated, parallel-correct timeline** driving the sample report: a per-step `SimulatedClock`
  (TimeProvider) advanced from step bodies via `ScenarioContext.SimulateElapsed`, with the scheduler computing
  start offsets as `max` over dependency finishes so siblings overlap and joins never sum. Opt-in, sample-local,
  threaded `simulateTime` flag; **real mode byte-for-byte unchanged** (222 tests stay green).
- **Task B — restyle** `report-template.html` by porting the self-contained mockup at
  `.git/sdd/mockup/report-mockup.html` (auto light/dark, auto-scaling ruler, structured drill panel), preserving
  the JSON-injection contract and model field names.

**Invariants (hold across every task):**
- Real runs (`simulatedTime:false`, the default everywhere except the sample's `Program.cs`) keep today's
  `StartedAt = _timeProvider.GetUtcNow()` at launch + `Stopwatch.Elapsed` duration + skip stamping.
- `HtmlReportModel`/`ReportScenario`/`ReportStep`/… field names and `HtmlReportModelBuilder` logic are unchanged
  (no model renames). The Verify model snapshot
  (`HtmlReportModelBuilderTests…Builds_the_expected_json_model.verified.txt`) must stay green **unchanged**.
- 0-warning build; new public members carry XML docs; `SimulatedClock` overrides documented.
- Report stays fully self-contained: inline `<style>`/`<script>` only; zero external URLs/CDNs/web-fonts/imports;
  only `system-ui` / `ui-monospace` font stacks.

**Build/test commands:**
- Build: `dotnet build Raun.slnx -c Debug`
- Full suite: `dotnet test Raun.slnx -c Debug`
- Scoped: `dotnet test test/Raun.Test/Raun.Test.csproj -c Debug --filter "FullyQualifiedName~<Class>"`

**Grounding facts (verified against the tree):**
- `ScenarioContext` (`src/Raun/ScenarioContext.cs`) has a 4-arg ctor delegating to a 6-arg ctor
  `(stepId, displayName, services, resolver, timeProvider, cancellationToken)`; the 6-arg ctor forwards
  `timeProvider ?? TimeProvider.System` to `ResourceContext`, which stamps each effect with
  `_timeProvider.GetUtcNow()` (`src/Raun/Resources/ResourceContext.cs:107`).
- `ScenarioScheduler` (`src/Raun/Scheduling/ScenarioScheduler.cs`) already takes
  `(int maxParallelism = 0, TimeProvider? timeProvider = null)`; `RunNodeAsync` is an instance method that
  builds the `ScenarioContext` with `_timeProvider` and stamps `StartedAt`/`Duration`; `ApplySkipAsync` stamps
  `StartedAt = _timeProvider.GetUtcNow()`, `Duration` defaults `0`.
- `StepResult` (`src/Raun/Model/StepResult.cs`) has `required DateTimeOffset StartedAt` and `TimeSpan Duration`.
- Wiring chain: `RaunTestApplication.RunAsync(args, configure)` → `(_, sp) => new RaunTestFramework(sp)` →
  `OnExecuteAsync` builds `new RaunRunLoop(EnumerateRegisteredScenarios)` → `DefaultRunScenario` builds
  `new ScenarioScheduler()`. `RaunTestFramework` already has `()` and `(IServiceProvider)` ctors.
- Generator: `SymbolHelpers.WantsContext(method, suppliedArgCount)` is true when the last param type is
  `Raun.ScenarioContext` and the call site supplies all *other* params; `ScenarioParser.BuildCallText` then
  appends `__ctx`. The trailing `ScenarioContext` is excluded from resource-claim binding. **Capability exists;
  it has no dedicated test — Task 5 adds one and is the go/no-go gate for the sample.**
- Sample: `samples/AppointmentTests/Scenarios.cs` has 4 scenarios (`Booking`, `BookingWithParallelArrange`,
  `ImportUsers`, `ImportUsersViaLinq`); `AppointmentDsl.cs` steps are `await Task.Yield()` bodies.
- Existing helpers reused: `test/Raun.Test/SchedulerTests.cs` (`Node`/`Def`/`Pass`/`WithTimeout`),
  `test/Raun.Test/TestTimeProvider.cs` (advancing clock), `HtmlReportSinkTests` `TestTimeProviderUtc`
  (fixed clock).

---

## Phase 1 — `SimulatedClock` (A1)

Realizes spec §2.3. New thread-safe `TimeProvider` whose timestamp path is consistent with its advances.

### Task 1.1: `SimulatedClock : TimeProvider` (test-first)

**Files:**
- Create test: `test/Raun.Test/SimulatedClockTests.cs`
- Create: `src/Raun/Scheduling/SimulatedClock.cs`

- [ ] **Step 1: Write the failing tests** in `test/Raun.Test/SimulatedClockTests.cs` (namespace `Raun.Test`,
  `using Raun.Scheduling;`). Cover the spec §6 SimulatedClock cases:
  1. `GetUtcNow` returns `base` before any advance; `base + 50ms` after `Advance(50ms)`; accumulates across two
     advances (`base + 90ms` after +50, +40).
  2. Timestamp consistency: `GetElapsedTime(t0, t1)` where `t0 = GetTimestamp()` before and `t1 = GetTimestamp()`
     after `Advance(123ms)` equals `123ms` (proves `TimestampFrequency`/`GetTimestamp` track `Advance`).
  3. `Elapsed` (the total-advanced accessor) equals the sum of advances.
  4. Concurrency: `Parallel.For(0, 1000, _ => clock.Advance(TimeSpan.FromMilliseconds(1)))` ⇒
     `Elapsed == 1000ms` (mirrors `Logging_is_safe_under_concurrency`).
  5. Negative delta throws `ArgumentOutOfRangeException` (`Assert.Throws`).

- [ ] **Step 2: Run — red (compile failure, `SimulatedClock` missing).**
  `dotnet test test/Raun.Test/Raun.Test.csproj -c Debug --filter "FullyQualifiedName~SimulatedClockTests"`

- [ ] **Step 3: Implement** `src/Raun/Scheduling/SimulatedClock.cs`, `namespace Raun.Scheduling`,
  `public sealed class SimulatedClock : TimeProvider`. Per spec §2.3:
  - ctor `SimulatedClock(DateTimeOffset baseInstant)` stores `_base`; pick a fixed `_baseTimestamp` origin (e.g. `0`).
  - `private long _advancedTicks;` mutated only via `Interlocked.Add`.
  - `public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _advancedTicks));` (document this as the
    total-advanced accessor the scheduler reads for `Duration`).
  - `public void Advance(TimeSpan delta)`: `ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero)` then
    `Interlocked.Add(ref _advancedTicks, delta.Ticks)`.
  - `public override DateTimeOffset GetUtcNow() => _base + Elapsed;`
  - `public override long TimestampFrequency => TimeSpan.TicksPerSecond;`
  - `public override long GetTimestamp() => _baseTimestamp + Interlocked.Read(ref _advancedTicks);`
  - XML docs on the class and every public/overridden member.

- [ ] **Step 4: Run — green.** Same filter as Step 2 (5 tests pass).

- [ ] **Step 5: Build 0-warning + commit.**
  `dotnet build src/Raun/Raun.csproj -c Debug` (0/0), then
  `git add src/Raun/Scheduling/SimulatedClock.cs test/Raun.Test/SimulatedClockTests.cs`
  `git commit -m "feat(scheduling): thread-safe SimulatedClock TimeProvider"`

---

## Phase 2 — `ScenarioContext.SimulateElapsed` (A2)

Realizes spec §2.4. Store the effective `TimeProvider`; advance it only when it is a `SimulatedClock`.

### Task 2.1: `SimulateElapsed` (test-first)

**Files:**
- Modify test: `test/Raun.Test/ScenarioContextTests.cs`
- Modify: `src/Raun/ScenarioContext.cs`

- [ ] **Step 1: Add failing tests** to `ScenarioContextTests.cs` (`using Raun.Scheduling;`):
  1. `SimulateElapsed_advances_a_SimulatedClock`: construct the 6-arg ctor with
     `timeProvider: new SimulatedClock(T0)`; call `ctx.SimulateElapsed(TimeSpan.FromMilliseconds(50))`; observe
     the advance via a recorded effect timestamp — `await ctx.Resources.Read(new PlainWidget("w"))` then assert
     the single effect's `Timestamp == T0 + 50ms` (effects share the same per-step clock).
  2. `SimulateElapsed_is_a_no_op_without_a_SimulatedClock`: use the 4-arg ctor (provider is null ⇒
     `TimeProvider.System` inside `ResourceContext`); `ctx.SimulateElapsed(TimeSpan.FromSeconds(1))` must not throw
     and must not block (no real wait). Assert it returns and a subsequent log still works.

- [ ] **Step 2: Run — red** (`SimulateElapsed` missing).
  `--filter "FullyQualifiedName~ScenarioContextTests"`

- [ ] **Step 3: Implement** in `src/Raun/ScenarioContext.cs` per spec §2.4:
  - In the 6-arg ctor, after computing the effective provider, store it on a `private readonly TimeProvider
    _timeProvider;` field — **the same instance** passed to `ResourceContext` (so step timing and effect stamps
    share one clock). Use `timeProvider ?? TimeProvider.System` once; pass the field into `ResourceContext`.
  - Add `public void SimulateElapsed(TimeSpan delta)`: `if (_timeProvider is SimulatedClock clock)
    clock.Advance(delta);` else no-op. XML doc explaining the real-run no-op.
  - **Do not change either ctor's signature or the resolver-precedence logic.** The 4-arg path delegates to the
    6-arg with `timeProvider: null` ⇒ field is `TimeProvider.System` ⇒ `SimulateElapsed` is a no-op. Byte-for-byte
    behavior of both ctors otherwise preserved.

- [ ] **Step 4: Run — green** (existing `ScenarioContextTests` + 2 new pass).

- [ ] **Step 5: Build 0-warning + commit.**
  `dotnet build src/Raun/Raun.csproj -c Debug` (0/0), then
  `git add src/Raun/ScenarioContext.cs test/Raun.Test/ScenarioContextTests.cs`
  `git commit -m "feat(context): SimulateElapsed advances a per-step SimulatedClock"`

---

## Phase 3 — `ScenarioScheduler` simulated mode (A3)

Realizes spec §2.5. Opt-in `bool simulatedTime = false`; per-step clock + scheduler-computed start offsets.
The headline anti-regression: **join = max(dep finishes), never the sum.**

### Task 3.1: Sim-mode scheduler (test-first; case 9 is the red→green centerpiece)

**Files:**
- Modify test: `test/Raun.Test/SchedulerTests.cs`
- Create test helper (if needed): a fixed-base provider for sim tests (the existing `TestTimeProvider` advances per
  call; sim mode captures base once, so a non-advancing fixed provider keeps assertions clean — add a tiny local
  `FixedClock : TimeProvider` in `SchedulerTests.cs`, or reuse `new SimulatedClock(base)` as the injected base
  provider).
- Modify: `src/Raun/Scheduling/ScenarioScheduler.cs`

- [ ] **Step 1: Add failing sim-mode tests** to `SchedulerTests.cs`. Build nodes whose bodies call
  `ctx.SimulateElapsed(...)`; construct schedulers with `new ScenarioScheduler(simulatedTime: true,
  timeProvider: fixedBaseClock)`. Helpers `Node`/`Def`/`Pass`/`WithTimeout` already exist; add a body factory like
  `Sim(TimeSpan d, object? output = null) => (_, ctx) => { ctx.SimulateElapsed(d); return Task.FromResult(output); }`.
  Cases (spec §6.8–§6.12):
  - **8 — Duration = own advance:** single node `Sim(40ms)` ⇒ `results[0].Duration == 40ms` and
    `results[0].StartedAt == base`.
  - **9 — Siblings overlap, join = max not sum (CORE):** `clean = Sim(0ms? or 20ms)` → siblings `A = Sim(80ms)`,
    `B = Sim(55ms)` both depending on clean → `join` depending on `[A,B]`. Assert
    `A.StartedAt == B.StartedAt == base + cleanDur`; `join.StartedAt == base + cleanDur + 80ms`
    (`max(80,55)`); and explicitly `join.StartedAt != base + cleanDur + 135ms` (the scalar-clock bug).
  - **10 — Determinism under real parallelism:** run case 9 in a loop (e.g. 25×) and/or with unbounded parallelism;
    assert identical `StartedAt`/`Duration` for every node every iteration (no cross-step corruption).
  - **11 — Skip placement:** `A` fails (`(_, _) => throw new InvalidOperationException()` — note a failing body
    records its own sim duration up to the throw; for a clean assertion make `A` a node that advances then throws,
    or simply assert `B.StartedAt == A.StartedAt + A.Duration` and `B.Duration == TimeSpan.Zero`,
    `B.Status == Skipped`).
  - **12 — Real mode no-op (explicit):** a single node `Sim(40ms)` under `new ScenarioScheduler()` (no flag)
    reports `Duration` from the real `Stopwatch` (≈0, not 40ms) and `StartedAt` from `TimeProvider.System` — i.e.
    `SimulateElapsed` did nothing. Assert `Duration < 40ms` (real wall clock for a no-op body) to prove the sim
    path is inert when the flag is off. (The full 222-test suite is the broader guard.)

- [ ] **Step 2: Run — red** (case 9 fails: join sums, or `simulatedTime` ctor param/sim branch missing).
  `--filter "FullyQualifiedName~SchedulerTests"`

- [ ] **Step 3: Implement sim mode** in `ScenarioScheduler.cs` per spec §2.5. **Real branch must stay
  byte-for-byte; gate every change behind `_simulatedTime`.**
  - ctor: add `bool simulatedTime = false`; store `_simulatedTime`. Keep `maxParallelism`/`timeProvider`.
  - In `RunAsync`: when `_simulatedTime`, capture `var simBase = _timeProvider.GetUtcNow();` **once**; allocate
    `var simFinishOffset = new TimeSpan[count];`. Add a local
    `TimeSpan StartOffsetOf(ScenarioNode n) => n.DependsOn.Length == 0 ? TimeSpan.Zero :
    n.DependsOn.Max(d => simFinishOffset[d]);`
  - **Launch (step 2 of the loop):** when sim, compute `startOffset = StartOffsetOf(node)` at launch time (all
    deps are `Passed`, so their `simFinishOffset` is populated) and pass it into the run. The cleanest seam: make
    `RunNodeAsync` take an optional `TimeSpan? simStartOffset` and, when set, create
    `var stepClock = new SimulatedClock(simBase + startOffset);`, build the `ScenarioContext` with
    `timeProvider: stepClock` (so the body's `SimulateElapsed` and any `ctx.Resources.*` stamps share it), run the
    body **without** the `Stopwatch`/`_timeProvider.GetUtcNow()` path, then set
    `StartedAt = simBase + startOffset; Duration = stepClock.Elapsed;` on the result.
  - **Record finishes:** when an outcome is processed (loop step 3), set
    `simFinishOffset[index] = (result.StartedAt - simBase) + result.Duration;` (sim mode only).
  - **Skip path (`ApplySkipAsync`):** when sim, `var startOffset = StartOffsetOf(node);
    StartedAt = simBase + startOffset; Duration = TimeSpan.Zero; simFinishOffset[i] = startOffset;` (a skip sits at
    the trailing edge of its failed/skipped dep, matching the mockup's `s4-4`).
  - **Timeout in sim mode:** skip the real `Task.Delay` race (no body blocks); document as a sim-mode limitation
    (spec §2.5 / §4). The sample uses no `[StepTimeout]`.
  - XML-doc the new ctor param.

- [ ] **Step 4: Run — green** (existing scheduler tests + 5 new pass; case 9 green).
  `--filter "FullyQualifiedName~SchedulerTests"`

- [ ] **Step 5: Build 0-warning + commit.**
  `dotnet build src/Raun/Raun.csproj -c Debug` (0/0), then
  `git add src/Raun/Scheduling/ScenarioScheduler.cs test/Raun.Test/SchedulerTests.cs`
  `git commit -m "feat(scheduler): simulated-time mode with per-step clock and max-join offsets"`

---

## Phase 4 — Thread `simulateTime` wiring (A4)

Realizes spec §2.7. Default `false` through Application → Framework → RunLoop → Scheduler so production is
unaffected; only the sample's own `Program.cs` opts in.

### Task 4.1: Run-loop carries the flag (test-first)

**Files:**
- Modify test: `test/Raun.Mtp.Test/RunLoopTests.cs`
- Modify: `src/Raun.Mtp/RaunRunLoop.cs`

- [ ] **Step 1: Write the failing run-loop test.** Drive a scenario whose step body calls
  `ctx.SimulateElapsed(60ms)` through `new RaunRunLoop(scenarioSource, runScenario: null /* default */,
  simulateTime: true)` (default-scheduler path) and assert the emitted `StepFinished` result has
  `Duration == 60ms` (proof the loop built a `ScenarioScheduler(simulatedTime: true)`). Add a companion assertion
  (or a second test) that with `simulateTime: false` the same body yields `Duration < 60ms` (real path). Use the
  existing `RecordingSink : IRunEventSink` pattern in the file; build a definition whose `Invoke` calls
  `SimulateElapsed`.
  > Note: the `runScenario` test seam bypasses the scheduler; this test must use the **default** `runScenario`
  > (pass `null`) so the flag actually reaches `DefaultRunScenario`.

- [ ] **Step 2: Run — red** (`simulateTime` ctor param missing).
  `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug --filter "FullyQualifiedName~RunLoopTests"`

- [ ] **Step 3: Implement** in `RaunRunLoop.cs`: add a `bool simulateTime = false` ctor param (after
  `runScenario`), store it, and in `DefaultRunScenario` build `new ScenarioScheduler(simulatedTime: simulateTime)`.
  Because `DefaultRunScenario` is currently `static`, make it an instance method (it must read the field) — mirror
  how the scheduler made `RunNodeAsync` an instance method. Leave the injected-`runScenario` seam untouched.

- [ ] **Step 4: Run — green** (`--filter "FullyQualifiedName~RunLoopTests"`).

### Task 4.2: Framework + Application thread the flag

**Files:**
- Modify: `src/Raun.Mtp/RaunTestFramework.cs`
- Modify: `src/Raun.Mtp/RaunTestApplication.cs`
- Modify test: `test/Raun.Mtp.Test/RaunTestFrameworkTests.cs` (or `RunLoopTests.cs`) — one wiring assertion

- [ ] **Step 1: Add a framework wiring test.** Assert the new ctor overload exists and the flag flows: construct
  `new RaunTestFramework(services: null!, simulateTime: true)` is awkward (services may be needed), so instead add
  a focused test that registers a scenario whose body calls `SimulateElapsed(80ms)`, drives `OnExecute` on a
  framework built with the simulate-true overload against a `RecordingMessageBus`, and asserts the published
  passed node's `TimingProperty` duration is `80ms`. (Reuse the `Through_the_framework_run_request_…` pattern and
  `RecordingMessageBus` already in the MTP test project.) Keep the existing `()` / `(IServiceProvider)` ctor tests
  green.

- [ ] **Step 2: Run — red** (no `(IServiceProvider, bool)` ctor).

- [ ] **Step 3: Implement** per spec §2.7:
  - `RaunTestFramework`: add `private readonly bool _simulateTime;` and a ctor
    `public RaunTestFramework(IServiceProvider services, bool simulateTime) : this(services) => _simulateTime =
    simulateTime;` (keep `()` and `(IServiceProvider)` ctors). In `OnExecuteAsync`, build
    `new RaunRunLoop(EnumerateRegisteredScenarios, simulateTime: _simulateTime)` (named arg; the `runScenario`
    positional stays default).
  - `RaunTestApplication.RunAsync`: add a trailing parameter
    `RunAsync(string[] args, Action<ITestApplicationBuilder>? configure = null, bool simulateTime = false)`;
    capture `simulateTime` in the factory closure:
    `(_, serviceProvider) => new RaunTestFramework(serviceProvider, simulateTime)`. Update the XML `<param>` doc
    and the `<see cref="RunAsync(...)"/>` reference in the `<remarks>` (the generated `Program` calls the 2-arg
    form ⇒ default `false` ⇒ production unchanged).

- [ ] **Step 4: Run — green** for the MTP test project; then **full suite** to prove production paths unchanged.
  `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug` then `dotnet test Raun.slnx -c Debug`
  (`Passed!`, all 222+ green).

- [ ] **Step 5: Build 0-warning + commit.**
  `dotnet build Raun.slnx -c Debug` (0/0), then
  `git add src/Raun.Mtp/RaunRunLoop.cs src/Raun.Mtp/RaunTestFramework.cs src/Raun.Mtp/RaunTestApplication.cs test/Raun.Mtp.Test`
  `git commit -m "feat(mtp): thread opt-in simulateTime through application, framework, run loop"`

---

## Phase 5 — Sample opt-in (A5)

Realizes spec §2.8. **Gate first:** lock the generator capability with a dedicated test before touching the
sample. If the capability is absent, STOP and report the exact gap (do not hack around it).

### Task 5.1: Generator test — trailing `ScenarioContext` emits `__ctx` (gate)

**Files:**
- Modify test: add a fact to `test/Raun.Generator.Test/ResourceLoweringTests.cs` (or a new
  `ScenarioContextParameterTests.cs`); extend `test/Raun.Generator.Test/SampleSources.cs` with a DSL whose step
  declares a trailing `Raun.ScenarioContext`.

- [ ] **Step 1: Write the failing test.** Add a small DSL source where a step is e.g.
  `public static Task NotesSomething(string what, Raun.ScenarioContext ctx) { ctx.Log(what); return
  Task.CompletedTask; }` plus a `[Scenario]` calling `Given.NotesSomething("hi")` (call site supplies only
  `"hi"`). Use `GeneratorHarness.Run(...)`; `result.AssertCompiles()`; assert:
  - the generated source contains the call passing `__ctx` as the trailing argument
    (`Assert.Contains("__ctx", result.GeneratedSource)` — but more specifically that the *step call* includes it,
    e.g. `Assert.Contains("NotesSomething(\"hi\", __ctx)", result.GeneratedSource)`).
  - behaviorally: `await result.Definitions().Single().RunAsync()` runs without binding error and the step's logged
    line appears in `results[0].Logs` (proves the ctx was passed and the trailing param was excluded from
    resource/arg binding).
  > This is the explicit lock the spec §2.8 calls for (existing snapshots' `__ctx` is the lambda param, not a
  > declared trailing `ScenarioContext`).

- [ ] **Step 2: Run.** `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj -c Debug --filter
  "FullyQualifiedName~<NewTest>"`.
  - **If it PASSES** (expected — capability verified): the generator already supports it; this test simply locks
    it. Proceed to Task 5.2.
  - **If it FAILS to compile/bind:** STOP. The sample depends on this; report the exact diagnostic and which seam
    (`WantsContext` / `BuildCallText` / `BuildResourceClaims`) is missing. Do not work around it.

- [ ] **Step 3: Commit the gate.**
  `git add test/Raun.Generator.Test`
  `git commit -m "test(generator): lock trailing ScenarioContext parameter emits __ctx"`

### Task 5.2: Sample owns Main + simulated durations

**Files:**
- Modify: `samples/AppointmentTests/AppointmentTests.csproj`
- Create: `samples/AppointmentTests/Program.cs`
- Modify: `samples/AppointmentTests/AppointmentDsl.cs`

- [ ] **Step 1: csproj — stop generating the entry point.** Add
  `<RaunGenerateProgram>false</RaunGenerateProgram>` to the first `<PropertyGroup>` in
  `AppointmentTests.csproj` (alongside the existing `GenerateProgramFile`/`GenerateTestingPlatformEntryPoint`
  false). This suppresses `RaunProgram.g.cs` so the sample owns `Main`.

- [ ] **Step 2: Create `samples/AppointmentTests/Program.cs`** with a single top-level statement:
  ```csharp
  return await Raun.Mtp.RaunTestApplication.RunAsync(args, simulateTime: true);
  ```
  (Or an explicit `internal static class Program { static Task<int> Main(string[] args) => …; }` returning the
  `int`.) This is the only opt-in to simulated time.

- [ ] **Step 3: AppointmentDsl — trailing `ctx` + `SimulateElapsed`.** Give each step a trailing
  `ScenarioContext ctx` parameter, drop the `await Task.Yield()` bodies, and call
  `ctx.SimulateElapsed(TimeSpan.FromMilliseconds(...))` with the spec §2.8 durations (tweakable). Keep returning
  the real domain objects so asserts still pass. Durations:
  | step | SimulateElapsed |
  |---|---|
  | DatabaseIsClean | 60 ms |
  | PatientExists | 180 ms |
  | AvailableSlot | 350 ms |
  | UserExists | 160 ms |
  | CreateAppointment | 600 ms |
  | AppointmentExists | 120 ms |
  | ImportUsers | 240 ms |
  | ImportShouldContainUsers | 90 ms |

  Steps that today have an expression body (`DatabaseIsClean`, `AppointmentExists`, `ImportShouldContainUsers`)
  become block bodies that call `ctx.SimulateElapsed(...)` then return. Steps with `[return: Creates]` keep the
  attribute and return the entity. The `ScenarioContext` is the **last** parameter on every step (after any
  `[Reads]`/value params) so `WantsContext` binds it.

- [ ] **Step 4: Build the sample 0-warning.**
  `dotnet build samples/AppointmentTests/AppointmentTests.csproj -c Debug` (0/0). Fixes any analyzer issues
  (unused usings, etc.).

- [ ] **Step 5: Smoke-run the sample and inspect the report.** Run the sample under MTP with `--report-html`
  (e.g. `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -c Debug -- --report-html`) and
  open the written `raun-report.html` (path printed/under the results dir). Verify in the embedded JSON:
  - ≥1 scenario `durationMs` > ~1000 (the two booking scenarios ≈ 1.07s / 1.13s ⇒ seconds ruler),
  - the import scenarios ≈ 490ms (ms ruler),
  - parallel arrange steps share an `offsetMs` and overlap; the join's `offsetMs == max(dep finishes)`.
  This is a manual check, not an automated test (no new assertion required).

- [ ] **Step 6: Full suite green** (the sample runs under `dotnet test`; asserts still pass under sim time).
  `dotnet test Raun.slnx -c Debug` (`Passed!`).

- [ ] **Step 7: Commit.**
  `git add samples/AppointmentTests`
  `git commit -m "sample(appointments): own Main and drive a simulated parallel timeline"`

---

## Phase 6 — Restyle the report (B)

Realizes spec §3. Port the mockup CSS/JS into the embedded template; preserve the JSON token + model field
names; apply the three deviations.

### Task 6.1: Port the mockup into `report-template.html`

**Files:**
- Replace: `src/Raun.Mtp/HtmlReport/report-template.html`
- Modify test: `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`

- [ ] **Step 1: Re-read the contract before editing.** The current template
  (`src/Raun.Mtp/HtmlReport/report-template.html`) carries exactly one
  `<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>`; `HtmlReportSink` string-replaces
  that single token (`JsonToken`) with the serialized camelCase, indented model. The new template MUST keep
  exactly one such element whose body is **only** the token.

- [ ] **Step 2: Replace `report-template.html`** by porting `.git/sdd/mockup/report-mockup.html` verbatim, with
  these required edits (spec §3.1–§3.3):
  - **Model element = token only.** Replace the mockup's `<script id="model">…inline sample JSON…</script>` body
    with exactly `/*__RAUN_REPORT_JSON__*/` (drop the canned JSON). This is the JSON-injection contract.
  - **Drop the screenshot-only pre-expand.** Remove the mockup's
    `const pre = openers.find(o => o.scenarioId === "sc-4"); if (pre) pre.open("s4-3");` block — drill panels start
    closed (real runs have arbitrary ids).
  - **Blank the lane-row gutters.** Change the lane-row gutter from `g.textContent = "L" + lane;` to leaving it
    empty (`g.textContent = "";` or omit) — keep the gutter **column** so lane tracks stay aligned. The ruler
    gutter (`"ms"/"s"`) and resource gutters (`Type:Key`) stay.
  - **No dependency arrows** (mockup draws none — keep it).
  - **Self-contained check:** confirm zero external URLs/CDNs/`@import`/web-fonts; only `system-ui`/`ui-monospace`
    stacks (the mockup already complies — verify nothing slipped in).
  - Keep everything else: light/dark palettes + `:root[data-theme="light"|"dark"]` overrides, the
    `?theme=light|dark` query-param JS, `niceAxis`/`fmtTick` auto-scaling ruler, `TRACK = 900`, per-status bar
    colors (pass green / fail red / **skip grey**), phase glyph + ellipsis + `title`, resource lifelines + verb
    legend, structured drill panel (ordered `<ol>` logs; effects as `verb · Type:Key · +offset (data)`; monospace
    `<pre>` exception; grey skip note), single-open toggle behavior.

- [ ] **Step 3: Update `HtmlReportSinkTests.cs` asserted substrings** to match the new markup (spec §3.4). The
  three structural asserts in `Writes_a_self_contained_html_file_on_run_finished` should still hold; re-verify
  each against the rendered HTML and adjust only if the exact substring changed:
  - `Assert.Contains("books", html)` — scenario `displayName` rendered via `esc(sc.displayName)` into the card
    title. Keep.
  - `Assert.Contains("\"scenarioId\": \"scn\"", html)` — the indented camelCase JSON blob (serialization
    unchanged). Keep.
  - `Assert.DoesNotContain("__RAUN_REPORT_JSON__", html)` — exactly one token, replaced by the sink. Keep.
  `Empty_run_still_writes_a_valid_report` and `A_write_failure_is_recorded_on_the_bus_not_thrown` are
  template-shape-agnostic and stay as-is.

- [ ] **Step 4: Run the sink tests — green.**
  `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug --filter "FullyQualifiedName~HtmlReportSinkTests"`

- [ ] **Step 5: Confirm the model snapshot is untouched.** `HtmlReportModelBuilderTests` (the Verify snapshot on
  the model, not HTML) must stay green **without** accepting any `.received.` file.
  `--filter "FullyQualifiedName~HtmlReportModelBuilderTests"` (all pass, no pending snapshot).

- [ ] **Step 6: Optional visual check.** Open the sample's restyled `raun-report.html` (from Phase 5 Step 5) in a
  browser; confirm auto light/dark, the seconds ruler on a >1s scenario and ms ruler on a sub-second one,
  overlapping parallel bars, grey skip bar, the resource lane + legend, and click-to-expand drill. Try
  `?theme=light` / `?theme=dark`.

- [ ] **Step 7: Full suite green + 0-warning build + commit.**
  `dotnet build Raun.slnx -c Debug` (0/0), `dotnet test Raun.slnx -c Debug` (`Passed!`), then
  `git add src/Raun.Mtp/HtmlReport/report-template.html test/Raun.Mtp.Test/HtmlReportSinkTests.cs`
  `git commit -m "feat(report): restyle HTML template with auto light/dark and auto-scaling ruler"`

---

## Final verification

- [ ] `dotnet build Raun.slnx -c Debug` → **0 Warning(s), 0 Error(s)**.
- [ ] `dotnet test Raun.slnx -c Debug` → `Test run summary: Passed!` (222 existing + the new SimulatedClock,
  ScenarioContext, scheduler sim-mode, run-loop/framework wiring, and generator tests).
- [ ] Sample `raun-report.html` shows a realistic, overlapping, max-joined timeline with ≥1 scenario > ~1s and
  the restyled look; model Verify snapshot unchanged; real-run timing path byte-for-byte unchanged.
