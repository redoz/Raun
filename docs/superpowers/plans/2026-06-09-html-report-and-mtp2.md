# HTML Run Report + MTP 2.x Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate the repo from Microsoft.Testing.Platform (MTP) 1.9.1 to 2.2.3, then add a self-contained `raun-report.html` run report (Gantt timeline + resource lane + click-to-drill detail), driven by a new runner-neutral run-event bus in Raun core.

**Architecture:** A typed pub/sub bus (`Raun.Reporting`) in core becomes the single event source for a run. The existing MTP reporter and the new HTML report are both *subscribers*. The scheduler stamps absolute `StartedAt` timestamps via an injected `TimeProvider`. The HTML sink accumulates a deterministic, snapshot-testable JSON model (lane-packed sink-side) and writes one self-contained HTML file at end of run, opt-in via `--report-html`.

**Tech Stack:** C# 14 / net10.0, Microsoft.Testing.Platform 2.2.3, xunit.v3 3.2.2 (via `xunit.v3.mtp-v2`), Verify.XunitV3 for JSON snapshots, vanilla JS/CSS embedded HTML template.

**Source design:** `docs/superpowers/specs/2026-06-07-html-report-design.md` (§ references below point at it).

**Pinned MTP 2.x facts (verified by reflecting `Microsoft.Testing.Platform` 2.2.1, API-identical to 2.2.3, and by a probe build/run of the whole solution):**

- `xunit.v3.mtp-v2` **3.2.2** is the drop-in meta-package that targets MTP **v2** (the default `xunit.v3` package targets MTP v1). Both already in the local NuGet cache.
- The **only** 1.x→2.x runtime break in the codebase: `Microsoft.Testing.Platform.TestHost.TestSessionContext` now has a single internal `.ctor(SessionUid sessionUid)` (1.9.1 had `.ctor(SessionUid, ClientInfo)`); `ClientInfo` now has internal `.ctor(string id, string version)`. `src/Raun.Mtp` itself needs **no** changes.
- Command-line option API for the report flag (namespaces in parens):
  - `ICommandLineOptionsProvider` (`Microsoft.Testing.Platform.Extensions.CommandLine`) **extends `IExtension`**. Members: `IReadOnlyCollection<CommandLineOption> GetCommandLineOptions()`, `Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions)`, `Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption, string[])`.
  - `CommandLineOption` (`…Extensions.CommandLine`) ctor: `(string name, string description, ArgumentArity arity, bool isHidden)`.
  - `ArgumentArity` (`…Extensions.CommandLine`) static fields: `Zero`, `ZeroOrOne`, `ExactlyOne`, `OneOrMore`, `ZeroOrMore`.
  - `ValidationResult` (`Microsoft.Testing.Platform.Extensions`): static `Valid()`, `Invalid(string)`; properties `ValidTask` (a `Task<ValidationResult>`), `InvalidTask(string)`.
  - `ICommandLineOptions` (`Microsoft.Testing.Platform.CommandLine`): `bool IsOptionSet(string optionName)`, `bool TryGetOptionArgumentList(string optionName, out string[] arguments)`. Option names are registered **without** the leading `--`.
  - Register a provider: `builder.CommandLine.AddProvider(() => new HtmlReportOptionsProvider())` (`ICommandLineManager.AddProvider(Func<ICommandLineOptionsProvider>)`, in `Microsoft.Testing.Platform.CommandLine`).
  - Read at runtime from the framework's `IServiceProvider` (`Microsoft.Testing.Platform.Services.ServiceProviderExtensions`): `serviceProvider.GetCommandLineOptions()` → `ICommandLineOptions`; `serviceProvider.GetConfiguration()` → `IConfiguration` whose indexer `config["platformOptions:resultDirectory"]` yields the resolved results directory (the constant `PlatformConfigurationConstants.PlatformResultDirectory` is internal — use the literal key).
  - `RegisterTestFramework(Func<IServiceProvider, ITestFrameworkCapabilities>, Func<ITestFrameworkCapabilities, IServiceProvider, ITestFramework>)` — the framework factory's 2nd arg is the `IServiceProvider`, so `RaunTestFramework` can be constructed with the resolved options.

---

## File Structure

**New (core — `src/Raun/Reporting/`):**
- `RunEvent.cs` — the `RunEvent` record hierarchy (`RunStarted`, `ScenarioStarted`, `StepStarted`, `StepFinished`, `ScenarioFinished`, `RunFinished`).
- `IRunEventSink.cs` — `IRunEventSink { ValueTask PublishAsync(RunEvent) }`.
- `RunEventSink.cs` — abstract base with virtual `On*Async` no-ops + sealed dispatch.
- `RunEventBus.cs` — serial fan-out with per-sink failure isolation + `Failures`.

**New (mtp — `src/Raun.Mtp/HtmlReport/`):**
- `HtmlReportModel.cs` — the serializable model (`HtmlReportModel`/`ReportScenario`/`ReportStep`/`ReportEffect`/`ReportResource`/`ReportResourceEvent`/`ReportSummary`) + the pure builder that lane-packs and rolls up resources.
- `HtmlReportSink.cs` — `RunEventSink` that accumulates the model and writes the file on `RunFinished`.
- `HtmlReportOptionsProvider.cs` — `ICommandLineOptionsProvider` registering `--report-html` + `--report-html-filename`.
- `report-template.html` — embedded resource; vanilla JS/CSS renderer with a single JSON `<script>` blob.

**New (mtp — `src/Raun.Mtp/`):**
- `MtpReportSink.cs` — session-scoped `RunEventSink` replacing `RaunStepReporter` (same emitted `TestNodeUpdateMessage`s).

**Changed (core):**
- `src/Raun/Model/StepResult.cs` — add `required DateTimeOffset StartedAt`.
- `src/Raun/Scheduling/ScenarioScheduler.cs` — ctor gains `TimeProvider`; stamp `StartedAt`; pass the clock into `ScenarioContext`.

**Changed (mtp):**
- `src/Raun.Mtp/RaunRunLoop.cs` — emit `RunEvent`s to an `IRunEventSink` instead of publishing to `IMessageBus` directly.
- `src/Raun.Mtp/RaunTestFramework.cs` — construct the sink list + bus, resolve the HTML path, log `bus.Failures`.
- `src/Raun.Mtp/RaunTestApplication.cs` — register `HtmlReportOptionsProvider`; pass the `IServiceProvider` into the framework.
- `src/Raun.Mtp/RaunStepReporter.cs` — **deleted** (logic moves to `MtpReportSink`).

**Changed (config):**
- `Directory.Packages.props` — MTP `2.2.3`; add `xunit.v3.mtp-v2`; add `Verify.XunitV3` is already present only as a version — reuse; add `System.Text.Json` is in-box (net10) — no package.
- `test/Raun.Test/Raun.Test.csproj`, `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj`, `test/Raun.Generator.Test/Raun.Generator.Test.csproj` — swap `xunit.v3` → `xunit.v3.mtp-v2`.
- `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj` — add `Verify.XunitV3` for the JSON snapshot.

**Changed (tests):**
- `test/Raun.Mtp.Test/RaunTestFrameworkTests.cs` — delete the reflective `RequestDispatch` tests + `MtpContextFactory` (Phase 0).
- `test/Raun.Mtp.Test/RunLoopTests.cs` — drive the loop via an `IRunEventSink` fake.
- `test/Raun.Mtp.Test/RaunStepReporterTests.cs` → rename to `MtpReportSinkTests.cs` — drive the sink via `RunEvent`s.
- New: `test/Raun.Test/Reporting/RunEventBusTests.cs`, `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`, `test/Raun.Mtp.Test/HtmlReportOptionsProviderTests.cs`, plus scheduler-timestamp tests in `test/Raun.Test/SchedulerTests.cs`.

---

## Phase 0 — Migrate to MTP 2.x

> Verified by a throwaway probe build+run of the entire solution: with these exact edits the solution builds with zero warnings/errors, all of `Raun.Test`, `Raun.Generator.Test`, and the `AppointmentTests` sample pass on the MTP v2 runner, and `Raun.Mtp.Test` passes once the reflective dispatch tests are removed (Task 0.2).

### Task 0.1: Swap MTP + xunit packages to the v2 line

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `test/Raun.Test/Raun.Test.csproj:15`
- Modify: `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj:23`
- Modify: `test/Raun.Generator.Test/Raun.Generator.Test.csproj:14`

- [ ] **Step 1: Bump the MTP pin and replace the stale comment** in `Directory.Packages.props`. Replace the comment block + version line (currently lines 13–20) with:

```xml
    <!--
      Microsoft.Testing.Platform v2. The xunit-based test projects opt into the MTP v2 runner by
      referencing `xunit.v3.mtp-v2` (xunit.v3 3.2.0+ selects its MTP major via package choice; the
      default `xunit.v3` package is MTP v1). This replaced the old 1.9.1 pin: the earlier 2.x attempt
      failed only because the test projects stayed on the v1 runner package while MTP unified to 2.x.
    -->
    <PackageVersion Include="Microsoft.Testing.Platform" Version="2.2.3" />
```

- [ ] **Step 2: Add the `xunit.v3.mtp-v2` version** directly under the `xunit.v3` line (currently line 32) in `Directory.Packages.props`:

```xml
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.mtp-v2" Version="3.2.2" />
```

- [ ] **Step 3: Switch each test project to the v2 runner package.** In all three of `test/Raun.Test/Raun.Test.csproj`, `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj`, `test/Raun.Generator.Test/Raun.Generator.Test.csproj`, change the reference:

```xml
    <PackageReference Include="xunit.v3.mtp-v2" />
```
(was `<PackageReference Include="xunit.v3" />`). Leave `xunit.runner.visualstudio`, `xunit.v3.assert`, and `xunit.v3.extensibility.core` untouched.

- [ ] **Step 4: Restore + build.**

Run: `dotnet build Raun.slnx -c Debug`
Expected: `Build succeeded.` with **0 Warning(s), 0 Error(s)**. (NuGet restores MTP 2.2.3 + `xunit.v3.mtp-v2` 3.2.2 on first build; requires network for 2.2.3.)

- [ ] **Step 5: Run the suite to see the single expected failure cluster.**

Run: `dotnet test Raun.slnx -c Debug --no-build`
Expected: `Raun.Test`, `Raun.Generator.Test`, and `AppointmentTests` **pass**; `Raun.Mtp.Test` reports exactly 3 failures, all `System.MissingMethodException : Constructor on type 'Microsoft.Testing.Platform.TestHost.TestSessionContext' not found.` — all from the reflective `RequestDispatch` tests, removed in Task 0.2. Do **not** commit yet.

### Task 0.2: Delete the reflective request-dispatch tests

The 3 `RequestDispatch` tests are the only code that constructs MTP's host-internal `TestSessionContext` (now an internal `.ctor(SessionUid)` — the request types it feeds have no public construction path). Rather than carry that reflection forward, delete them: the `ExecuteRequestAsync` routing they cover is a trivial type-switch already exercised end-to-end by the **real MTP host** when the `AppointmentTests` sample runs under `dotnet test` (the host issues genuine discover + run requests), and its `OnDiscoverAsync`/`OnExecuteAsync` targets are unit-tested directly via the `SessionUid`-keyed worker seam the framework was explicitly designed to expose.

**Files:**
- Modify: `test/Raun.Mtp.Test/RaunTestFrameworkTests.cs`

- [ ] **Step 1: Delete the reflective pieces.** Remove three things from `RaunTestFrameworkTests.cs`:
  - the nested `public class RequestDispatch { … }` (the 3 tests, lines ~111–172);
  - the `private sealed class RecordingTestFramework : RaunTestFramework { … }` (lines ~178–209) — used only by `RequestDispatch`;
  - the `private static class MtpContextFactory { … }` (lines ~229–273) — the reflection helper itself.

  **Keep** the `public class SessionManagement { … }` and the `private sealed class SpyMessageBus : IMessageBus` (the unknown-session `SessionManagement` tests still use it).

- [ ] **Step 2: Remove the now-unused usings the build flags.** `IDE0005` (unnecessary using) is a build error in this repo, so the compiler names them precisely. After Step 1 these become unused: `using System.Reflection;`, `using Microsoft.Testing.Platform.Requests;`, and `using Microsoft.Testing.Platform.Extensions.TestFramework;`. Keep `Microsoft.Testing.Platform.Extensions.Messages`, `Microsoft.Testing.Platform.Messages`, `Microsoft.Testing.Platform.TestHost`, `Raun.Mtp`, and `Xunit` (still used by `SessionManagement`/`SpyMessageBus`).

Run: `dotnet build test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug`
Expected: `Build succeeded.` 0 warnings, 0 errors. (If the build flags a using I didn't list, remove exactly what it names — do not add `#pragma` suppressions.)

- [ ] **Step 3: Run the full suite — everything green.**

Run: `dotnet test Raun.slnx -c Debug`
Expected: all four test projects + sample pass; `Test run summary: Passed!`. (`RaunTestFrameworkTests` now contains only `SessionManagement`.)

- [ ] **Step 4: Commit.**

```bash
git add Directory.Packages.props test/Raun.Test/Raun.Test.csproj test/Raun.Mtp.Test/Raun.Mtp.Test.csproj test/Raun.Generator.Test/Raun.Generator.Test.csproj test/Raun.Mtp.Test/RaunTestFrameworkTests.cs
git commit -m "build: migrate to Microsoft.Testing.Platform 2.2.3 (xunit.v3.mtp-v2 runner)"
```

---

## Phase 1 — Run-event bus in core (`Raun.Reporting`)

Realizes design §3.A. Pure core types, no MTP/runner dependency. Tested in `Raun.Test`.

### Task 1.1: Event records + sink interface + ergonomic base

**Files:**
- Create: `src/Raun/Reporting/RunEvent.cs`
- Create: `src/Raun/Reporting/IRunEventSink.cs`
- Create: `src/Raun/Reporting/RunEventSink.cs`
- Test: `test/Raun.Test/Reporting/RunEventBusTests.cs` (added in Task 1.2)

- [ ] **Step 1: Create the event records** in `src/Raun/Reporting/RunEvent.cs`:

```csharp
using Raun.Model;
using Raun.Scheduling;

namespace Raun.Reporting;

/// <summary>Base type for the runner-neutral run-event stream (design §3.A).</summary>
public abstract record RunEvent;

/// <summary>Raised once at the start of a run, before any scenario.</summary>
public sealed record RunStarted(int ScenarioCount) : RunEvent;

/// <summary>Raised when a scenario begins; carries the definition so a session-scoped sink can
/// attribute every following step to its scenario.</summary>
public sealed record ScenarioStarted(ScenarioDefinition Definition) : RunEvent;

/// <summary>Raised when a step is about to run (or, for a skipped step, just before its finish).</summary>
public sealed record StepStarted(ScenarioDefinition Definition, StepContext Context) : RunEvent;

/// <summary>Raised when a step reaches a terminal status; the result is self-contained (carries
/// <see cref="StepResult.StartedAt"/>, duration, logs, effects, exception/skip reason).</summary>
public sealed record StepFinished(ScenarioDefinition Definition, StepResult Result) : RunEvent;

/// <summary>Raised when a scenario's steps have all reached terminal status.</summary>
public sealed record ScenarioFinished(
    ScenarioDefinition Definition, IReadOnlyList<StepResult> Results) : RunEvent;

/// <summary>Raised once at the end of a run, after the last scenario.</summary>
public sealed record RunFinished : RunEvent;
```

- [ ] **Step 2: Create the sink interface** in `src/Raun/Reporting/IRunEventSink.cs`:

```csharp
namespace Raun.Reporting;

/// <summary>A subscriber to the run-event stream. The bus awaits each call serially.</summary>
public interface IRunEventSink
{
    /// <summary>Handle one event. May be async; the bus awaits it before the next sink/event.</summary>
    ValueTask PublishAsync(RunEvent evt);
}
```

- [ ] **Step 3: Create the ergonomic base** in `src/Raun/Reporting/RunEventSink.cs`:

```csharp
namespace Raun.Reporting;

/// <summary>
/// Base sink with virtual no-op handlers and sealed pattern-match dispatch, so a concrete sink
/// overrides only the events it cares about.
/// </summary>
public abstract class RunEventSink : IRunEventSink
{
    public ValueTask PublishAsync(RunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt switch
        {
            RunStarted e => OnRunStartedAsync(e),
            ScenarioStarted e => OnScenarioStartedAsync(e),
            StepStarted e => OnStepStartedAsync(e),
            StepFinished e => OnStepFinishedAsync(e),
            ScenarioFinished e => OnScenarioFinishedAsync(e),
            RunFinished e => OnRunFinishedAsync(e),
            _ => default,
        };
    }

    protected virtual ValueTask OnRunStartedAsync(RunStarted e) => default;
    protected virtual ValueTask OnScenarioStartedAsync(ScenarioStarted e) => default;
    protected virtual ValueTask OnStepStartedAsync(StepStarted e) => default;
    protected virtual ValueTask OnStepFinishedAsync(StepFinished e) => default;
    protected virtual ValueTask OnScenarioFinishedAsync(ScenarioFinished e) => default;
    protected virtual ValueTask OnRunFinishedAsync(RunFinished e) => default;
}
```

- [ ] **Step 4: Build core.**

Run: `dotnet build src/Raun/Raun.csproj -c Debug`
Expected: `Build succeeded.` 0 warnings, 0 errors.

### Task 1.2: `RunEventBus` — serial fan-out with failure isolation

**Files:**
- Create: `src/Raun/Reporting/RunEventBus.cs`
- Test: `test/Raun.Test/Reporting/RunEventBusTests.cs`

- [ ] **Step 1: Write the failing tests** in `test/Raun.Test/Reporting/RunEventBusTests.cs`:

```csharp
using Raun.Reporting;
using Xunit;

namespace Raun.Test.Reporting;

public class RunEventBusTests
{
    private sealed class RecordingSink : IRunEventSink
    {
        public List<RunEvent> Seen { get; } = [];
        public ValueTask PublishAsync(RunEvent evt) { Seen.Add(evt); return default; }
    }

    private sealed class ThrowingSink : IRunEventSink
    {
        public int Calls { get; private set; }
        public ValueTask PublishAsync(RunEvent evt) { Calls++; throw new InvalidOperationException("boom"); }
    }

    [Fact]
    public async Task Fans_out_to_each_sink_in_registration_order()
    {
        var order = new List<string>();
        var a = new DelegateSink(_ => order.Add("a"));
        var b = new DelegateSink(_ => order.Add("b"));
        var bus = new RunEventBus([a, b]);

        await bus.PublishAsync(new RunStarted(1));

        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public async Task A_throwing_sink_is_isolated_and_siblings_still_receive_every_event()
    {
        var bad = new ThrowingSink();
        var good = new RecordingSink();
        var bus = new RunEventBus([bad, good]);

        await bus.PublishAsync(new RunStarted(1));
        await bus.PublishAsync(new RunFinished());

        Assert.Equal(2, good.Seen.Count);          // sibling got both events
        Assert.Equal(2, bad.Calls);                 // bus kept calling the bad sink too
        var failure = Assert.Single(bus.Failures);  // first error per sink recorded
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public async Task Records_one_failure_per_sink_not_per_event()
    {
        var bad = new ThrowingSink();
        var bus = new RunEventBus([bad]);

        await bus.PublishAsync(new RunStarted(1));
        await bus.PublishAsync(new RunFinished());

        Assert.Single(bus.Failures); // first error only; the sink is not re-reported each event
    }

    private sealed class DelegateSink(Action<RunEvent> onEvent) : IRunEventSink
    {
        public ValueTask PublishAsync(RunEvent evt) { onEvent(evt); return default; }
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~RunEventBusTests"`
Expected: FAIL — `RunEventBus` does not exist (compile error).

- [ ] **Step 3: Implement the bus** in `src/Raun/Reporting/RunEventBus.cs`:

```csharp
namespace Raun.Reporting;

/// <summary>
/// Fans a <see cref="RunEvent"/> out to child sinks serially, in registration order, awaiting each.
/// A throwing sink is isolated: the bus records its first error in <see cref="Failures"/> and keeps
/// delivering to the remaining sinks and to that sink on later events. A broken report sink must
/// never fail the run or starve the MTP reporter (design §3.A "Failure isolation").
/// </summary>
public sealed class RunEventBus : IRunEventSink
{
    private readonly IReadOnlyList<IRunEventSink> _sinks;
    private readonly Exception?[] _firstError;
    private readonly List<Exception> _failures = [];

    public RunEventBus(IReadOnlyList<IRunEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
        _firstError = new Exception?[sinks.Count];
    }

    /// <summary>The first error each failed sink raised, in sink order; empty when all sinks held.</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    public async ValueTask PublishAsync(RunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        for (var i = 0; i < _sinks.Count; i++)
        {
            try
            {
                await _sinks[i].PublishAsync(evt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_firstError[i] is null)
                {
                    _firstError[i] = ex;
                    _failures.Add(ex);
                }
            }
        }
    }
}
```

- [ ] **Step 4: Run tests — green.**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~RunEventBusTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit.**

```bash
git add src/Raun/Reporting test/Raun.Test/Reporting
git commit -m "feat(reporting): runner-neutral run-event bus with failure isolation"
```

---

## Phase 2 — Scheduler timestamps (`StartedAt` + injected clock)

Realizes design §3.B. Tested in `Raun.Test`.

### Task 2.1: Add `StartedAt` to `StepResult`

**Files:**
- Modify: `src/Raun/Model/StepResult.cs`
- Modify: `src/Raun/Scheduling/ScenarioScheduler.cs`

- [ ] **Step 1: Add the property** to `src/Raun/Model/StepResult.cs`, immediately after the `Status` property (line 13):

```csharp
    /// <summary>Absolute wall-clock instant the step began (stamped scheduler-side via an injected
    /// <see cref="TimeProvider"/>). Skipped steps carry the instant at skip time; <see cref="Duration"/>
    /// stays zero. <c>FinishedAt</c> is derived (<c>StartedAt + Duration</c>), not stored.</summary>
    public required DateTimeOffset StartedAt { get; init; }
```

- [ ] **Step 2: Build core to surface every construction site that must now set `StartedAt`.**

Run: `dotnet build src/Raun/Raun.csproj -c Debug`
Expected: FAIL — `error CS9035: Required member 'StepResult.StartedAt' must be set` at the three `new StepResult { … }` sites in `ScenarioScheduler.cs` (the Passed result ~line 226, the `Outcome` local ~line 251, and `ApplySkipAsync` ~line 166). This compile error is the checklist for Step 3.

- [ ] **Step 3: Stamp `StartedAt` at every scheduler construction site.** Edits in `src/Raun/Scheduling/ScenarioScheduler.cs`:

(a) Field + ctor — add a `TimeProvider` (design §3.B). Replace the field/ctor (lines 16–22) with:

```csharp
    private readonly int _maxParallelism;
    private readonly TimeProvider _timeProvider;

    /// <param name="maxParallelism">Maximum steps running at once; 0 (default) means unbounded.</param>
    /// <param name="timeProvider">Clock for step <see cref="StepResult.StartedAt"/> stamps and step
    /// resource effects; defaults to <see cref="TimeProvider.System"/>.</param>
    public ScenarioScheduler(int maxParallelism = 0, TimeProvider? timeProvider = null)
    {
        _maxParallelism = maxParallelism;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }
```

(b) `ApplySkipAsync` — stamp at skip time. In the `new StepResult { … }` inside `ApplySkipAsync` (after `Status = StepStatus.Skipped,`), add:

```csharp
                StartedAt = _timeProvider.GetUtcNow(),
```

(c) `RunNodeAsync` — make it an **instance** method so it can read `_timeProvider`, capture the start instant, and share the clock with `ScenarioContext`. Change its signature from `private static async Task<NodeOutcome> RunNodeAsync(` to `private async Task<NodeOutcome> RunNodeAsync(`, then at the top of the method body replace the first two lines:

```csharp
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(scenarioToken);
        var context = new ScenarioContext(
            node.StepId, displayName, services, resolver: null, _timeProvider, stepCts.Token);
```

(d) In the Passed-result `new StepResult { … }` (after `Status = StepStatus.Passed,`) add `StartedAt = startedAt,`. In the `Outcome` local function's `new StepResult { … }` (after `Status = statusValue,`) add `StartedAt = startedAt,`.

- [ ] **Step 4: Build core — clean.**

Run: `dotnet build src/Raun/Raun.csproj -c Debug`
Expected: `Build succeeded.` 0 warnings, 0 errors.

### Task 2.2: Behavioral tests for the stamped clock

**Files:**
- Modify: `test/Raun.Test/SchedulerTests.cs`
- Create: `test/Raun.Test/TestTimeProvider.cs`

- [ ] **Step 1: Add a controllable clock** in `test/Raun.Test/TestTimeProvider.cs` (dependency-free; no `Microsoft.Extensions.TimeProvider.Testing` package):

```csharp
namespace Raun.Test;

/// <summary>A deterministic <see cref="TimeProvider"/> for tests: returns a fixed base instant,
/// advanced by a fixed step on every <see cref="GetUtcNow"/> call so concurrently-stamped steps get
/// distinct, ordered <c>StartedAt</c> values without real time.</summary>
internal sealed class TestTimeProvider(DateTimeOffset start, TimeSpan? perCall = null) : TimeProvider
{
    private readonly TimeSpan _step = perCall ?? TimeSpan.FromMilliseconds(10);
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        var now = new DateTimeOffset(_ticks, TimeSpan.Zero);
        _ticks += _step.Ticks;
        return now;
    }
}
```

- [ ] **Step 2: Add the failing tests** to `test/Raun.Test/SchedulerTests.cs` (before the closing brace / `RecordingObserver`). They use the existing `Def`/`Node`/`Pass`/`WithTimeout` helpers:

```csharp
    [Fact]
    public async Task StartedAt_comes_from_the_injected_time_provider()
    {
        var baseInstant = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(baseInstant);
        var def = Def(Node(0, Pass()));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(baseInstant, results[0].StartedAt);
    }

    [Fact]
    public async Task Concurrent_group_steps_get_distinct_overlapping_windows()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var def = Def(
            Node(0, Pass()),
            Node(1, Pass(), [0]),
            Node(2, Pass(), [0]));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        // Each concurrent sibling got its own StartedAt from the advancing clock (no shared anchor).
        Assert.NotEqual(results[1].StartedAt, results[2].StartedAt);
    }

    [Fact]
    public async Task Skipped_step_carries_started_at_and_zero_duration()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero));
        var def = Def(
            Node(0, (_, _) => throw new InvalidOperationException("boom")),
            Node(1, Pass(), [0]));

        var results = await WithTimeout(new ScenarioScheduler(timeProvider: clock).RunAsync(def));

        Assert.Equal(StepStatus.Skipped, results[1].Status);
        Assert.NotEqual(default, results[1].StartedAt);
        Assert.Equal(TimeSpan.Zero, results[1].Duration);
    }
```

- [ ] **Step 3: Run — green.**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS (existing scheduler tests + 3 new). Existing tests are unaffected — they never read `StartedAt` and the default `TimeProvider.System` keeps real behavior.

- [ ] **Step 4: Commit.**

```bash
git add src/Raun/Model/StepResult.cs src/Raun/Scheduling/ScenarioScheduler.cs test/Raun.Test/SchedulerTests.cs test/Raun.Test/TestTimeProvider.cs
git commit -m "feat(scheduler): stamp absolute StartedAt via injected TimeProvider"
```

---

## Phase 3 — Reporter → sink, run-loop emitter, framework wiring

Realizes design §3.C/§3.E (the bus wiring; the HTML sink itself lands in Phase 4–6). After this phase the MTP messages are unchanged (state/output/attachments identical; the timing window becomes `StartedAt`-anchored, an accuracy improvement that existing assertions — which check only `Duration` — still satisfy).

### Task 3.1: `MtpReportSink` (session-scoped) replacing `RaunStepReporter`

**Files:**
- Create: `src/Raun.Mtp/MtpReportSink.cs`
- Delete: `src/Raun.Mtp/RaunStepReporter.cs`
- Rename + rewrite: `test/Raun.Mtp.Test/RaunStepReporterTests.cs` → `test/Raun.Mtp.Test/MtpReportSinkTests.cs`

- [ ] **Step 1: Create `MtpReportSink`** in `src/Raun.Mtp/MtpReportSink.cs`. It is the old `RaunStepReporter` body, lifted onto `RunEventSink`: it caches per-scenario labels on `ScenarioStarted` and reads the scenario `definition` off each event instead of from a ctor field. Copy `MapState`/`MapFailure`/`IsAssertionException`/`AddOutput`/`AddAttachments`/`CreateAttachmentDirectory`/`SanitizeFileName` **verbatim** from `RaunStepReporter.cs` (lines 100–251) into this class; only the node-building seams change:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;

namespace Raun.Mtp;

/// <summary>
/// Session-scoped sink that bridges the run-event stream onto the Microsoft.Testing.Platform message
/// bus: one <see cref="TestNodeUpdateMessage"/> per step lifecycle event, so each scenario step is a
/// first-class MTP node. Replaces the per-scenario <c>RaunStepReporter</c>; identical messages, but
/// keyed off the <see cref="ScenarioDefinition"/> carried on each event so one instance serves the
/// whole run. The step-numbering labels are computed once per scenario (on <see cref="ScenarioStarted"/>)
/// and cached by <see cref="ScenarioDefinition.ScenarioId"/>.
/// </summary>
internal sealed class MtpReportSink : RunEventSink
{
    private readonly SessionUid _sessionUid;
    private readonly IMessageBus _messageBus;
    private readonly IDataProducer _producer;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, string>> _labels =
        new(StringComparer.Ordinal);

    public MtpReportSink(SessionUid sessionUid, IMessageBus messageBus, IDataProducer producer)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(producer);
        _sessionUid = sessionUid;
        _messageBus = messageBus;
        _producer = producer;
    }

    protected override ValueTask OnScenarioStartedAsync(ScenarioStarted e)
    {
        _labels[e.Definition.ScenarioId] = ScenarioStepNumbering.Compute(e.Definition);
        return default;
    }

    protected override async ValueTask OnStepStartedAsync(StepStarted e)
    {
        var testNode = BuildNode(e.Definition, e.Context.Node, e.Context.DisplayName);
        testNode.Properties.Add(InProgressTestNodeStateProperty.CachedInstance);
        await Publish(testNode).ConfigureAwait(false);
    }

    protected override async ValueTask OnStepFinishedAsync(StepFinished e)
    {
        var result = e.Result;
        var testNode = BuildNode(e.Definition, result.Node, result.DisplayName);
        testNode.Properties.Add(MapState(result));

        // Absolute window from the scheduler-stamped StartedAt (design §3.B bonus): accurate even
        // for concurrent steps, replacing the old finish-anchored (UtcNow - Duration) approximation.
        testNode.Properties.Add(new TimingProperty(
            new TimingInfo(result.StartedAt, result.StartedAt + result.Duration, result.Duration)));

        AddOutput(testNode, result);
        AddAttachments(testNode, e.Definition, result);
        await Publish(testNode).ConfigureAwait(false);
    }

    private TestNode BuildNode(ScenarioDefinition definition, ScenarioNode node, string displayName)
    {
        var labels = _labels.TryGetValue(definition.ScenarioId, out var cached)
            ? cached
            : ScenarioStepNumbering.Compute(definition); // defensive: step before its ScenarioStarted

        var testNode = new TestNode
        {
            Uid = RaunDiscoverer.MakeUid(definition.ScenarioId, node.StepId),
            DisplayName = ScenarioStepNumbering.Format(labels, node, displayName),
        };

        testNode.Properties.Add(ScenarioTestIdentity.Create(
            definition.MethodName, definition.DisplayName, definition.ClassDisplayName));

        if (!string.IsNullOrEmpty(node.SourceFile) && node.SourceLine > 0)
        {
            var position = new LinePosition(node.SourceLine, 0);
            testNode.Properties.Add(new TestFileLocationProperty(
                node.SourceFile, new LinePositionSpan(position, position)));
        }

        return testNode;
    }

    // --- copied verbatim from RaunStepReporter (MapState, MapFailure, IsAssertionException, AddOutput,
    //     SanitizeFileName) — UNCHANGED. Two methods take `definition` instead of a field: ---

    private static void AddOutput(TestNode testNode, StepResult result) { /* verbatim from RaunStepReporter.AddOutput */ }

    private void AddAttachments(TestNode testNode, ScenarioDefinition definition, StepResult result)
    {
        // verbatim from RaunStepReporter.AddAttachments, except CreateAttachmentDirectory(definition, result)
    }

    private static string CreateAttachmentDirectory(ScenarioDefinition definition, StepResult result)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "raun-mtp",
            SanitizeFileName(RaunDiscoverer.MakeUid(definition.ScenarioId, result.Node.StepId)));
        Directory.CreateDirectory(path);
        return path;
    }

    private Task Publish(TestNode testNode)
    {
        NodeDiagnostics.Log("run", testNode);
        return _messageBus.PublishAsync(_producer, new TestNodeUpdateMessage(_sessionUid, testNode));
    }
}
```

> Implementation note: copy the bodies of `MapState`, `MapFailure`, `IsAssertionException`, `AddOutput`, and `SanitizeFileName` exactly from `RaunStepReporter.cs` (no behavior change). `StandardOutputProperty` is **stable** in MTP 2.x — do **not** wrap it in `#pragma warning disable TPEXP` (that suppression is now unnecessary and `IDE0079` would fail the build).

- [ ] **Step 2: Delete `src/Raun.Mtp/RaunStepReporter.cs`.**

- [ ] **Step 3: Rewrite the reporter tests as sink tests.** `git mv test/Raun.Mtp.Test/RaunStepReporterTests.cs test/Raun.Mtp.Test/MtpReportSinkTests.cs`, rename the class to `MtpReportSinkTests`, and apply this mechanical transform to **every** existing test (the assertions on the produced `TestNode` are unchanged):
  - Construct the sink once: `var sink = new MtpReportSink(new SessionUid("sess"), bus, producer);`
  - Before driving a step, prime the scenario: `await sink.PublishAsync(new ScenarioStarted(def));`
  - Replace `reporter.OnStepStartingAsync(new StepContext { Node = n, DisplayName = d })` with `sink.PublishAsync(new StepStarted(def, new StepContext { Node = n, DisplayName = d }))`.
  - Replace `reporter.OnStepFinishedAsync(result)` with `sink.PublishAsync(new StepFinished(def, result))`.
  - Every `new StepResult { … }` gains `StartedAt = TestInstant,` where `private static readonly DateTimeOffset TestInstant = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);` is added to the test class.

  Worked example — the converted `Passed_step_publishes_passed_state`:

```csharp
    private static readonly DateTimeOffset TestInstant = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    private static (MtpReportSink Sink, RecordingMessageBus Bus) NewSink()
    {
        var bus = new RecordingMessageBus();
        return (new MtpReportSink(new SessionUid("sess"), bus, new StubProducer()), bus);
    }

    [Fact]
    public async Task Passed_step_publishes_passed_state()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (sink, bus) = NewSink();

        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            StartedAt = TestInstant,
            Duration = TimeSpan.FromMilliseconds(5),
        }));

        var node = Assert.Single(bus.Nodes);
        Assert.NotEmpty(node.Properties.OfType<PassedTestNodeStateProperty>());
    }
```

  Keep `StubProducer`, `RecordingMessageBus`, `GatedMessageBus`, `Node`, `Definition` helpers. Drop the two `#pragma warning disable TPEXP` blocks around `StandardOutputProperty` assertions (now stable). For `OnStepFinishedAsync_awaits_the_publish_instead_of_blocking`, drive via `sink.PublishAsync(new StepFinished(def, …))` against the `GatedMessageBus` and assert the returned `ValueTask` is not completed (`Assert.False(task.IsCompleted)`).

- [ ] **Step 4: Build (run-loop/framework still reference the deleted reporter — expected to fail here; fixed in 3.2/3.3).**

Run: `dotnet build src/Raun.Mtp/Raun.Mtp.csproj -c Debug`
Expected: FAIL — `RaunRunLoop.cs` references `RaunStepReporter`. Proceed to Task 3.2 (do not commit mid-refactor).

### Task 3.2: `RaunRunLoop` emits `RunEvent`s to an `IRunEventSink`

**Files:**
- Modify: `src/Raun.Mtp/RaunRunLoop.cs`
- Rewrite: `test/Raun.Mtp.Test/RunLoopTests.cs`

- [ ] **Step 1: Rewrite `RunAsync` + `RunSelectedAsync` + `RunOneAsync`** in `src/Raun.Mtp/RaunRunLoop.cs` to take an `IRunEventSink bus` instead of `(sessionUid, messageBus, producer)`, emit the event envelope, and let an internal observer adapter republish step events tagged with the scenario. `SelectScenarios` and the cancellation logic are unchanged. Replace lines 30–160 (the class body below the doc-comment) with:

```csharp
internal sealed class RaunRunLoop
{
    /// <summary>Runs one scenario to completion and returns its step results. Tests substitute this
    /// to observe how many runs the loop issues; the default drives a real <see cref="ScenarioScheduler"/>.</summary>
    public delegate Task<IReadOnlyList<StepResult>> RunScenario(
        ScenarioDefinition definition,
        IStepObserver observer,
        CancellationToken cancellationToken);

    private readonly Func<IEnumerable<ScenarioDefinition>> scenarioSource;
    private readonly RunScenario runScenario;

    public RaunRunLoop(
        Func<IEnumerable<ScenarioDefinition>> scenarioSource,
        RunScenario? runScenario = null)
    {
        ArgumentNullException.ThrowIfNull(scenarioSource);
        this.scenarioSource = scenarioSource;
        this.runScenario = runScenario ?? DefaultRunScenario;
    }

    public static IReadOnlyList<ScenarioDefinition> SelectScenarios(
        IEnumerable<ScenarioDefinition> scenarios, ISet<string>? uids)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        if (uids is null)
        {
            return scenarios.ToList();
        }

        var selected = new List<ScenarioDefinition>();
        foreach (var definition in scenarios)
        {
            foreach (var step in definition.Nodes)
            {
                if (uids.Contains(RaunDiscoverer.MakeUid(definition.ScenarioId, step.StepId)))
                {
                    selected.Add(definition);
                    break;
                }
            }
        }

        return selected;
    }

    /// <summary>Runs every scenario the <paramref name="uids"/> filter selects (or all when null),
    /// emitting the run-event envelope (<see cref="RunStarted"/> → per scenario
    /// <see cref="ScenarioStarted"/>/steps/<see cref="ScenarioFinished"/> → <see cref="RunFinished"/>).</summary>
    public async ValueTask RunAsync(ISet<string>? uids, IRunEventSink bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bus);

        var selected = SelectScenarios(scenarioSource(), uids);
        await bus.PublishAsync(new RunStarted(selected.Count)).ConfigureAwait(false);

        var started = false;
        foreach (var definition in selected)
        {
            if (started && cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await RunOneAsync(definition, bus, cancellationToken).ConfigureAwait(false);
            started = true;
        }

        await bus.PublishAsync(new RunFinished()).ConfigureAwait(false);
    }

    private async ValueTask RunOneAsync(
        ScenarioDefinition definition, IRunEventSink bus, CancellationToken cancellationToken)
    {
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await bus.PublishAsync(new ScenarioStarted(definition)).ConfigureAwait(false);

        var observer = new BusObserver(definition, bus);
        var results = await runScenario(definition, observer, runCts.Token).ConfigureAwait(false);

        await bus.PublishAsync(new ScenarioFinished(definition, results)).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StepResult>> DefaultRunScenario(
        ScenarioDefinition definition, IStepObserver observer, CancellationToken cancellationToken)
        => await new ScenarioScheduler().RunAsync(
            definition, services: null, observer: observer, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Republishes the scheduler's per-step callbacks onto the bus, tagged with the scenario.</summary>
    private sealed class BusObserver(ScenarioDefinition definition, IRunEventSink bus) : IStepObserver
    {
        public Task OnStepStartingAsync(StepContext context)
            => bus.PublishAsync(new StepStarted(definition, context)).AsTask();

        public Task OnStepFinishedAsync(StepResult result)
            => bus.PublishAsync(new StepFinished(definition, result)).AsTask();
    }
}
```

Update the `using` block at the top of the file to: `using Raun.Model; using Raun.Reporting; using Raun.Scheduling;` (drop the MTP message/`TestHost` usings — the loop no longer touches them).

- [ ] **Step 2: Rewrite `RunLoopTests.cs`** to drive the loop through an `IRunEventSink` fake instead of `IMessageBus`. Replace the `RecordingBus : IMessageBus`/`StubProducer` helpers with a recording sink, and update the end-to-end tests to assert on emitted events:

```csharp
using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using Xunit;

namespace Raun.Mtp.Test;

public class RunLoopTests
{
    // Node/Definition/Uid helpers unchanged from the original file.

    private sealed class RecordingSink : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];
        public ValueTask PublishAsync(RunEvent evt) { lock (Events) { Events.Add(evt); } return default; }

        public IEnumerable<StepResult> FinishedResults =>
            Events.OfType<StepFinished>().Select(e => e.Result);

        public IEnumerable<string> PassedUids => FinishedResults
            .Where(r => r.Status == StepStatus.Passed)
            .Select(r => Uid(/* scenario */ "", r.Node.StepId)); // see note
    }

    // ... selection tests (SelectScenarios) are pure and unchanged ...
}
```

  Mechanical transform for the end-to-end tests:
  - `await loop.RunAsync(new SessionUid("s"), uids, bus, new StubProducer(), ct)` → `await loop.RunAsync(uids, sink, ct)`.
  - `bus.PassedUids` assertions become assertions over `sink.Events.OfType<StepFinished>()`: a step "passed" when `e.Result.Status == StepStatus.Passed`, and its uid is `RaunDiscoverer.MakeUid(e.Definition.ScenarioId, e.Result.Node.StepId)`. Add a sink helper `PassedUids` that maps each `StepFinished` to that uid (use `e.Definition.ScenarioId`, not a placeholder).
  - `bus.SkippedUids` / `bus.Nodes` likewise map off `StepFinished` events (`Status == StepStatus.Skipped`) and `ScenarioStarted`/`StepStarted` events.
  - The `RunScenario` stub now returns results: `runScenario: (_, _, _) => Task.FromResult<IReadOnlyList<StepResult>>([])`.
  - `Through_the_framework_run_request_executes_the_registered_scenario` moves to Task 3.3 (it exercises `OnExecute`, which now builds the bus internally).

  Concretely, the helper:

```csharp
    private static string PassedUid(StepFinished e) =>
        RaunDiscoverer.MakeUid(e.Definition.ScenarioId, e.Result.Node.StepId);
```
  and a test asserts e.g. `Assert.Contains(Uid("chain", "z"), sink.Events.OfType<StepFinished>().Where(e => e.Result.Status == StepStatus.Passed).Select(PassedUid));`

- [ ] **Step 3: Do not build yet** — the framework (`RaunTestFramework.OnExecuteAsync`) still calls the old `RunAsync` signature. Fixed in Task 3.3.

### Task 3.3: `RaunTestFramework` builds the sink list + bus; `RaunTestApplication` passes the service provider

**Files:**
- Modify: `src/Raun.Mtp/RaunTestFramework.cs`
- Modify: `src/Raun.Mtp/RaunTestApplication.cs`
- Modify: `test/Raun.Mtp.Test/RunLoopTests.cs` (re-home the framework end-to-end test)
- Modify: `test/Raun.Mtp.Test/RaunTestFrameworkTests.cs`

- [ ] **Step 1: Give the framework an optional `IServiceProvider`** and build the bus in `OnExecuteAsync`. In `src/Raun.Mtp/RaunTestFramework.cs`:

(a) Add a field + constructors near the top of the class (after the `sessions` field, line 47):

```csharp
    private readonly IServiceProvider? _services;

    /// <summary>Parameterless ctor for tests and the default registration path.</summary>
    public RaunTestFramework() { }

    /// <summary>Production ctor: the MTP <see cref="IServiceProvider"/> supplies command-line options
    /// (the <c>--report-html</c> flag) and the resolved results directory.</summary>
    public RaunTestFramework(IServiceProvider services) => _services = services;
```

(b) Replace `OnExecuteAsync` (lines 214–227) with the bus-building version:

```csharp
    protected virtual async ValueTask OnExecuteAsync(
        SessionUid sessionUid,
        ITestExecutionFilter? filter,
        IMessageBus messageBus,
        Action operationComplete,
        CancellationToken cancellationToken)
    {
        var uids = ReadUidFilter(filter);

        var sinks = new List<IRunEventSink> { new MtpReportSink(sessionUid, messageBus, this) };
        if (HtmlReportPath.Resolve(_services) is { } reportPath)
        {
            sinks.Add(new HtmlReport.HtmlReportSink(reportPath, TimeProvider.System));
        }

        var bus = new RunEventBus(sinks);
        var loop = new RaunRunLoop(EnumerateRegisteredScenarios);
        await loop.RunAsync(uids, bus, cancellationToken).ConfigureAwait(false);

        foreach (var failure in bus.Failures)
        {
            NodeDiagnostics.Log("report-sink-failure", failure.ToString());
        }

        operationComplete();
    }
```

> `HtmlReportPath.Resolve` and `HtmlReportSink` are added in Phases 4–6; until then this references types that don't exist. To keep Phase 3 building independently, in this task add a **temporary stub** `internal static class HtmlReportPath { public static string? Resolve(IServiceProvider? services) => null; }` in `RaunTestFramework.cs` and the `if (... is { } reportPath)` block referencing `HtmlReportSink` is added later — for Phase 3, write only `var sinks = new List<IRunEventSink> { new MtpReportSink(sessionUid, messageBus, this) };` (no HTML branch). Phase 6 replaces this with the real resolver + branch.

  **Phase 3 form (no HTML yet):**

```csharp
        var uids = ReadUidFilter(filter);
        var bus = new RunEventBus([new MtpReportSink(sessionUid, messageBus, this)]);
        var loop = new RaunRunLoop(EnumerateRegisteredScenarios);
        await loop.RunAsync(uids, bus, cancellationToken).ConfigureAwait(false);

        foreach (var failure in bus.Failures)
        {
            NodeDiagnostics.Log("report-sink-failure", failure.ToString());
        }

        operationComplete();
```

(c) Add `using Raun.Reporting;` to the file's usings.

- [ ] **Step 2: Confirm `NodeDiagnostics.Log` has a `(string, string)` overload;** if not, add one in `src/Raun.Mtp/NodeDiagnostics.cs` (read the file first):

```csharp
    public static void Log(string phase, string message)
    {
        // mirror the existing TestNode overload's sink (debug listener / env-gated trace)
    }
```
  (Match the existing logging mechanism in that file; this is best-effort diagnostics only.)

- [ ] **Step 3: Register the framework with the service provider** in `src/Raun.Mtp/RaunTestApplication.cs`. Replace the `RegisterTestFramework` call (lines 39–41) with:

```csharp
        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(),
            (_, serviceProvider) => new RaunTestFramework(serviceProvider));
```

- [ ] **Step 4: Re-home the framework end-to-end test.** Add to `RunLoopTests.cs` (it used `OnExecute`, which now builds the bus internally and still publishes via the MTP message bus). It needs a real `IMessageBus` recorder — reuse a minimal `RecordingMessageBus` (copy from `MtpReportSinkTests`), since `OnExecute` → `MtpReportSink` → message bus:

```csharp
    [Fact]
    public async Task Through_the_framework_run_request_executes_the_registered_scenario()
    {
        var method = $"Raun.Mtp.Test.RunLoop.{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => Definition("fw-scn", "fw scenario",
            Node(0, "a", "a"), Node(1, "b", "b", dependsOn: [0])));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("fw-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnExecute(uid, filter: null, bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);
        var passed = bus.Nodes
            .Where(n => n.Properties.OfType<PassedTestNodeStateProperty>().Length != 0)
            .Select(n => n.Uid.Value).ToList();
        Assert.Contains("fw-scn:a", passed);
        Assert.Contains("fw-scn:b", passed);
    }
```

- [ ] **Step 5: Build + run the whole MTP test project.**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug`
Expected: PASS — `MtpReportSinkTests`, `RunLoopTests`, `RaunTestFrameworkTests`, discovery tests all green. 0 build warnings.

- [ ] **Step 6: Full solution green.**

Run: `dotnet test Raun.slnx -c Debug`
Expected: `Test run summary: Passed!`.

- [ ] **Step 7: Commit.**

```bash
git add src/Raun.Mtp test/Raun.Mtp.Test
git rm src/Raun.Mtp/RaunStepReporter.cs
git commit -m "refactor(mtp): reporter -> session-scoped MtpReportSink driven by the run-event bus"
```

---

## Phase 4 — HTML report model + sink (JSON, lane-packed, snapshot-tested)

Realizes design §3.D (model) + §4. The model and its builder are pure and deterministic so the JSON is Verify-snapshottable.

### Task 4.1: Add Verify to the MTP test project

**Files:**
- Modify: `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj`
- Create: `test/Raun.Mtp.Test/VerifyConfig.cs`

- [ ] **Step 1: Add the Verify reference** to `test/Raun.Mtp.Test/Raun.Mtp.Test.csproj` (the version `31.19.0` is already pinned centrally):

```xml
    <PackageReference Include="Verify.XunitV3" />
```

- [ ] **Step 2: Add the Verify module initializer** in `test/Raun.Mtp.Test/VerifyConfig.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace Raun.Mtp.Test;

public static class VerifyConfig
{
    [ModuleInitializer]
    public static void Initialize() => Environment.SetEnvironmentVariable("DiffEngine_Disabled", "true");
}
```

- [ ] **Step 3: Build the test project.**

Run: `dotnet build test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Debug`
Expected: `Build succeeded.`

### Task 4.2: The report model types

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`

- [ ] **Step 1: Define the serializable model** in `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs` (shapes mirror design §4; `System.Text.Json` is in-box on net10):

```csharp
namespace Raun.Mtp.HtmlReport;

/// <summary>The full, self-contained report payload embedded into the HTML (design §4). All times are
/// pre-reduced to millisecond offsets from each scenario's start so the renderer does no clock math.</summary>
public sealed record HtmlReportModel
{
    public required string GeneratedAtUtc { get; init; }
    public required ReportSummary Summary { get; init; }
    public required IReadOnlyList<ReportScenario> Scenarios { get; init; }
}

public sealed record ReportSummary
{
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required double TotalMs { get; init; }
}

public sealed record ReportScenario
{
    public required string ScenarioId { get; init; }
    public required string DisplayName { get; init; }
    public string? ClassDisplayName { get; init; }
    public required string MethodName { get; init; }
    public required string StartedAtUtc { get; init; }
    public required double DurationMs { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<ReportStep> Steps { get; init; }
    public required IReadOnlyList<ReportResource> Resources { get; init; }
}

public sealed record ReportStep
{
    public required string StepId { get; init; }
    public required int Index { get; init; }
    public required string Label { get; init; }
    public required string Phase { get; init; }
    public required string DisplayName { get; init; }
    public required string Status { get; init; }
    public required double OffsetMs { get; init; }
    public required double DurationMs { get; init; }
    public required int Lane { get; init; }
    public required IReadOnlyList<int> DependsOn { get; init; }
    public string? GroupId { get; init; }
    public required IReadOnlyList<string> Logs { get; init; }
    public required IReadOnlyList<ReportEffect> Effects { get; init; }
    public string? Exception { get; init; }
    public string? SkipReason { get; init; }
}

public sealed record ReportEffect
{
    public required string Verb { get; init; }
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required double OffsetMs { get; init; }
    public string? Data { get; init; }
}

public sealed record ReportResource
{
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<ReportResourceEvent> Events { get; init; }
}

public sealed record ReportResourceEvent
{
    public required string Verb { get; init; }
    public required double OffsetMs { get; init; }
    public required string StepId { get; init; }
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build src/Raun.Mtp/Raun.Mtp.csproj -c Debug`
Expected: `Build succeeded.`

### Task 4.3: The model builder — lane packing + resource rollup

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`
- Test: `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`

- [ ] **Step 1: Write the failing tests** in `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`. They synthesize `ScenarioStarted` + `StepFinished` events (fake clock, fixed durations) and Verify-snapshot the JSON model, plus assert lane packing directly:

```csharp
using System.Text.Json;
using Raun;
using Raun.Model;
using Raun.Reporting;
using Raun.Scheduling;
using VerifyXunit;
using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportModelBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    private static ScenarioNode Node(int index, string stepId, string phase, string template,
        int[]? dependsOn = null, string? group = null) => new()
    {
        Index = index, StepId = stepId, Phase = phase, OperationName = $"Op{index}",
        DisplayNameTemplate = template, DependsOn = dependsOn ?? [], GroupId = group,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn", DisplayName = "customer books", MethodName = "Ns.Booking",
        ClassDisplayName = "Appointment booking", Nodes = nodes,
    };

    private static StepResult Result(ScenarioNode node, DateTimeOffset startedAt, double ms,
        StepStatus status = StepStatus.Passed, IReadOnlyList<ResourceEffect>? effects = null,
        IReadOnlyList<string>? logs = null) => new()
    {
        Node = node, DisplayName = node.DisplayNameTemplate, Status = status,
        StartedAt = startedAt, Duration = TimeSpan.FromMilliseconds(ms),
        Effects = effects ?? [], Logs = logs ?? [],
    };

    [Fact]
    public Task Builds_the_expected_json_model()
    {
        var n0 = Node(0, "p", "Given", "Given patient Jane exists");
        var n1 = Node(1, "s", "Given", "Given an available slot exists");
        var n2 = Node(2, "c", "When", "When creating an appointment", dependsOn: [0, 1]);
        var def = Def(n0, n1, n2);

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 40, effects:
        [
            new ResourceEffect
            {
                Verb = LifecycleVerb.Create, Identity = new ResourceIdentity(typeof(string), "Jane"),
                StepId = "p", Timestamp = T0.AddMilliseconds(1),
            },
        ]));
        builder.OnStepFinished(def, Result(n1, T0, 30));                 // concurrent with n0 → lane 1
        builder.OnStepFinished(def, Result(n2, T0.AddMilliseconds(40), 50));

        var model = builder.Build(generatedAtUtc: "2026-06-09T12:00:01Z");
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        return Verify(json);
    }

    [Fact]
    public void Overlapping_steps_are_packed_into_separate_lanes()
    {
        var n0 = Node(0, "a", "Given", "a");
        var n1 = Node(1, "b", "Given", "b");
        var def = Def(n0, n1);

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 100));               // [0,100)
        builder.OnStepFinished(def, Result(n1, T0.AddMilliseconds(10), 50)); // [10,60) overlaps → lane 1

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(0, scenario.Steps[0].Lane);
        Assert.Equal(1, scenario.Steps[1].Lane);
    }

    [Fact]
    public void Sequential_steps_reuse_lane_zero()
    {
        var n0 = Node(0, "a", "Given", "a");
        var n1 = Node(1, "b", "When", "b", dependsOn: [0]);
        var def = Def(n0, n1);

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 50));                // [0,50)
        builder.OnStepFinished(def, Result(n1, T0.AddMilliseconds(50), 50)); // [50,100) no overlap → lane 0

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(0, scenario.Steps[0].Lane);
        Assert.Equal(0, scenario.Steps[1].Lane);
    }

    [Fact]
    public void Resource_effects_roll_up_into_one_lifeline_per_identity()
    {
        var n0 = Node(0, "a", "Given", "a");
        var def = Def(n0);
        var id = new ResourceIdentity(typeof(string), "Jane");

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, effects:
        [
            new ResourceEffect { Verb = LifecycleVerb.Create, Identity = id, StepId = "a", Timestamp = T0.AddMilliseconds(2) },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        var resource = Assert.Single(scenario.Resources);
        Assert.Equal("String", resource.Type);
        Assert.Equal("Jane", resource.Key);
        Assert.Equal("Create", Assert.Single(resource.Events).Verb);
    }
}
```

- [ ] **Step 2: Run to verify failure.**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportModelBuilderTests"`
Expected: FAIL — `HtmlReportModelBuilder` does not exist.

- [ ] **Step 3: Implement the builder** in `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`. It accumulates per-scenario state, computes `scenarioStart = min(StartedAt)`, reduces every time to a ms offset, lane-packs steps by their `[offset, offset+duration)` intervals, and rolls effects up by `Type:Key`:

```csharp
using System.Globalization;
using Raun.Model;

namespace Raun.Mtp.HtmlReport;

/// <summary>
/// Builds the deterministic <see cref="HtmlReportModel"/> from the run-event stream. All layout
/// (lane packing, resource rollup, ms-offset reduction) happens here — not in the renderer — so the
/// JSON is snapshot-testable (design §4). Drive it with <see cref="OnScenarioStarted"/> then one
/// <see cref="OnStepFinished"/> per terminal step, in scheduler order, then <see cref="Build"/>.
/// </summary>
internal sealed class HtmlReportModelBuilder
{
    private readonly List<ScenarioAccumulator> _scenarios = [];
    private ScenarioAccumulator? _current;

    public void OnScenarioStarted(ScenarioDefinition definition)
    {
        _current = new ScenarioAccumulator(definition);
        _scenarios.Add(_current);
    }

    public void OnStepFinished(ScenarioDefinition definition, StepResult result)
    {
        var acc = _scenarios.LastOrDefault(s => s.Definition.ScenarioId == definition.ScenarioId)
                  ?? throw new InvalidOperationException(
                      $"StepFinished for '{definition.ScenarioId}' before its ScenarioStarted.");
        acc.Add(result);
    }

    public HtmlReportModel Build(string generatedAtUtc)
    {
        var scenarios = _scenarios.Select(s => s.Build()).ToList();
        var summary = new ReportSummary
        {
            Passed = scenarios.Count(s => s.Status == "passed"),
            Failed = scenarios.Count(s => s.Status == "failed"),
            Skipped = scenarios.Count(s => s.Status == "skipped"),
            TotalMs = scenarios.Count == 0 ? 0 : scenarios.Max(s => s.DurationMs),
        };

        return new HtmlReportModel
        {
            GeneratedAtUtc = generatedAtUtc,
            Summary = summary,
            Scenarios = scenarios,
        };
    }

    private sealed class ScenarioAccumulator(ScenarioDefinition definition)
    {
        private readonly List<StepResult> _results = [];
        public ScenarioDefinition Definition { get; } = definition;

        public void Add(StepResult result) => _results.Add(result);

        public ReportScenario Build()
        {
            var start = _results.Count == 0
                ? DateTimeOffset.UnixEpoch
                : _results.Min(r => r.StartedAt);

            var ordered = _results.OrderBy(r => r.Node.Index).ToList();
            var lanes = PackLanes(ordered, start);

            var steps = new List<ReportStep>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                steps.Add(new ReportStep
                {
                    StepId = r.Node.StepId,
                    Index = r.Node.Index,
                    Label = r.Node.Index.ToString(CultureInfo.InvariantCulture),
                    Phase = r.Node.Phase,
                    DisplayName = r.DisplayName,
                    Status = StatusText(r.Status),
                    OffsetMs = Ms(r.StartedAt - start),
                    DurationMs = Ms(r.Duration),
                    Lane = lanes[i],
                    DependsOn = r.Node.DependsOn,
                    GroupId = r.Node.GroupId,
                    Logs = r.Logs,
                    Effects = r.Effects.Select(e => new ReportEffect
                    {
                        Verb = e.Verb.ToString(),
                        Type = e.Identity.Type.Name,
                        Key = e.Identity.Key.ToString(),
                        OffsetMs = Ms(e.Timestamp - start),
                        Data = e.Data?.ToString(),
                    }).ToList(),
                    Exception = r.Exception?.ToString(),
                    SkipReason = r.SkipReason,
                });
            }

            var resources = ordered
                .SelectMany(r => r.Effects)
                .GroupBy(e => (e.Identity.Type.Name, Key: e.Identity.Key.ToString()))
                .Select(g => new ReportResource
                {
                    Type = g.Key.Name,
                    Key = g.Key.Key,
                    Events = g.Select(e => new ReportResourceEvent
                    {
                        Verb = e.Verb.ToString(),
                        OffsetMs = Ms(e.Timestamp - start),
                        StepId = e.StepId ?? string.Empty,
                    }).ToList(),
                })
                .ToList();

            var status = steps.Any(s => s.Status == "failed") ? "failed"
                : steps.Any(s => s.Status == "skipped") ? "skipped"
                : "passed";

            var durationMs = steps.Count == 0 ? 0 : steps.Max(s => s.OffsetMs + s.DurationMs);

            return new ReportScenario
            {
                ScenarioId = Definition.ScenarioId,
                DisplayName = Definition.DisplayName,
                ClassDisplayName = Definition.ClassDisplayName,
                MethodName = Definition.MethodName,
                StartedAtUtc = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Status = status,
                Steps = steps,
                Resources = resources,
            };
        }

        // Greedy interval packing: a step takes the first lane whose last bar ended at/before its start.
        private static int[] PackLanes(IReadOnlyList<StepResult> ordered, DateTimeOffset start)
        {
            var laneEnds = new List<double>();
            var lanes = new int[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = Ms(ordered[i].StartedAt - start);
                var e = s + Ms(ordered[i].Duration);
                var lane = -1;
                for (var l = 0; l < laneEnds.Count; l++)
                {
                    if (laneEnds[l] <= s) { lane = l; break; }
                }

                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(e); }
                else { laneEnds[lane] = e; }

                lanes[i] = lane;
            }

            return lanes;
        }

        private static double Ms(TimeSpan span) => span.TotalMilliseconds;

        private static string StatusText(StepStatus status) => status switch
        {
            StepStatus.Passed => "passed",
            StepStatus.Failed => "failed",
            StepStatus.Skipped => "skipped",
            _ => status.ToString().ToLowerInvariant(),
        };
    }
}
```

- [ ] **Step 4: Run — the snapshot test fails the first time (no `.verified.` file).**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportModelBuilderTests"`
Expected: the 3 lane/resource asserts PASS; `Builds_the_expected_json_model` FAILS producing a `.received.` file (Verify's first-run behavior).

- [ ] **Step 5: Accept the snapshot** after eyeballing the `.received.` JSON for correctness (offsets relative to `T0`, lane 1 on the concurrent slot step, one `String:Jane` resource lifeline):

Run: `Move-Item -Force test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.received.txt test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt`
Then re-run the filter; Expected: PASS (4 tests).

- [ ] **Step 6: Commit.**

```bash
git add src/Raun.Mtp/HtmlReport test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs test/Raun.Mtp.Test/*.verified.txt test/Raun.Mtp.Test/VerifyConfig.cs test/Raun.Mtp.Test/Raun.Mtp.Test.csproj
git commit -m "feat(report): deterministic HTML report model with lane packing + resource rollup"
```

---

## Phase 5 — HTML renderer template + sink file write

Realizes design §3.D (renderer) + the file-write half of the sink.

### Task 5.1: Embedded HTML template

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/report-template.html`
- Modify: `src/Raun.Mtp/Raun.Mtp.csproj` (embed it)

- [ ] **Step 1: Create the template** `src/Raun.Mtp/HtmlReport/report-template.html` — self-contained, vanilla JS/CSS, with a placeholder token the sink replaces with the JSON blob. Keep it minimal but functional (summary header, one Gantt section per scenario with lane rows + a resource lane, click-to-drill detail panel):

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>Raun run report</title>
<style>
  :root { --pass:#2e7d32; --fail:#c62828; --skip:#9e9e9e; --bg:#fafafa; --ink:#222; }
  body { font:14px/1.4 system-ui, sans-serif; color:var(--ink); margin:0; background:var(--bg); }
  header { padding:12px 16px; background:#fff; border-bottom:1px solid #ddd; }
  .summary span { margin-right:16px; font-weight:600; }
  .scenario { margin:16px; background:#fff; border:1px solid #ddd; border-radius:6px; padding:12px; }
  .gantt { position:relative; }
  .row { position:relative; height:22px; margin:2px 0; }
  .bar { position:absolute; height:18px; border-radius:3px; color:#fff; font-size:11px;
         white-space:nowrap; overflow:hidden; padding:0 4px; cursor:pointer; box-sizing:border-box; }
  .bar.passed { background:var(--pass); } .bar.failed { background:var(--fail); } .bar.skipped { background:var(--skip); }
  .res { fill-opacity:.9; }
  .reslane { position:relative; height:22px; margin-top:8px; border-top:1px dashed #ccc; }
  .marker { position:absolute; width:10px; height:10px; top:6px; border-radius:50%; transform:translateX(-50%); }
  .marker.create { background:#1565c0; } .marker.read,.marker.load { background:#00897b; }
  .marker.edit { background:#ef6c00; } .marker.delete { background:#6a1b9a; }
  .detail { margin-top:8px; padding:8px; background:#f4f4f4; border-radius:4px; display:none; white-space:pre-wrap; }
  .detail.open { display:block; }
  h2 { font-size:15px; margin:0 0 8px; } .muted { color:#777; font-weight:400; }
</style>
</head>
<body>
<header><div class="summary" id="summary"></div></header>
<main id="report"></main>
<script id="model" type="application/json">/*__RAUN_REPORT_JSON__*/</script>
<script>
  const model = JSON.parse(document.getElementById('model').textContent);
  const PX_PER_MS = 4, MIN_W = 24;
  const sum = model.summary;
  document.getElementById('summary').innerHTML =
    `<span style="color:var(--pass)">✓ ${sum.passed}</span>` +
    `<span style="color:var(--fail)">✗ ${sum.failed}</span>` +
    `<span style="color:var(--skip)">∅ ${sum.skipped}</span>` +
    `<span class="muted">${sum.totalMs.toFixed(1)} ms · ${model.generatedAtUtc}</span>`;
  const root = document.getElementById('report');
  for (const sc of model.scenarios) {
    const el = document.createElement('section'); el.className = 'scenario';
    el.innerHTML = `<h2>${esc(sc.displayName)} <span class="muted">${esc(sc.classDisplayName||'')} · ${sc.status}</span></h2>`;
    const gantt = document.createElement('div'); gantt.className = 'gantt';
    const laneCount = sc.steps.reduce((m,s)=>Math.max(m,s.lane),0)+1;
    for (let lane=0; lane<laneCount; lane++) {
      const row = document.createElement('div'); row.className='row';
      for (const s of sc.steps.filter(s=>s.lane===lane)) row.appendChild(bar(s, sc));
      gantt.appendChild(row);
    }
    if (sc.resources.length) {
      const rl = document.createElement('div'); rl.className='reslane';
      for (const r of sc.resources) for (const ev of r.events) {
        const m=document.createElement('div'); m.className='marker '+ev.verb.toLowerCase();
        m.style.left=(ev.offsetMs*PX_PER_MS)+'px'; m.title=`${ev.verb} ${r.type}:${r.key} @ ${ev.offsetMs}ms`;
        rl.appendChild(m);
      }
      gantt.appendChild(rl);
    }
    el.appendChild(gantt);
    root.appendChild(el);
  }
  function bar(s, sc) {
    const b=document.createElement('div'); b.className='bar '+s.status;
    b.style.left=(s.offsetMs*PX_PER_MS)+'px'; b.style.width=Math.max(MIN_W,s.durationMs*PX_PER_MS)+'px';
    b.textContent=`${s.phase[0]} ${s.label}`; b.title=s.displayName;
    const detail=document.createElement('div'); detail.className='detail';
    detail.textContent=drill(s);
    b.onclick=()=>{ detail.classList.toggle('open'); };
    const wrap=document.createElement('div'); wrap.appendChild(b); wrap.appendChild(detail);
    return wrap;
  }
  function drill(s){
    let t=`${s.displayName}\n${s.status} · ${s.durationMs}ms\n`;
    if (s.logs.length) t+='\nLogs:\n'+s.logs.map(l=>'  '+l).join('\n');
    if (s.effects.length) t+='\nEffects:\n'+s.effects.map(e=>`  ${e.verb} ${e.type}:${e.key} +${e.offsetMs}ms`+(e.data?` (${e.data})`:'')).join('\n');
    if (s.exception) t+='\nException:\n'+s.exception;
    if (s.skipReason) t+='\nSkipped: '+s.skipReason;
    return t;
  }
  function esc(x){ const d=document.createElement('div'); d.textContent=x; return d.innerHTML; }
</script>
</body>
</html>
```

- [ ] **Step 2: Embed the template** by adding to `src/Raun.Mtp/Raun.Mtp.csproj` (new `ItemGroup`):

```xml
  <ItemGroup>
    <EmbeddedResource Include="HtmlReport\report-template.html" />
  </ItemGroup>
```

- [ ] **Step 3: Build.**

Run: `dotnet build src/Raun.Mtp/Raun.Mtp.csproj -c Debug`
Expected: `Build succeeded.` (The resource logical name defaults to `Raun.Mtp.HtmlReport.report-template.html`.)

### Task 5.2: `HtmlReportSink` — accumulate + render + write

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/HtmlReportSink.cs`
- Test: `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`

- [ ] **Step 1: Write the failing tests** in `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`. They drive the sink through the event stream and assert: a file is written at the resolved path on `RunFinished`; it contains one Gantt section per scenario and the embedded JSON; and **no file is written if `RunFinished` never carries any scenario** (empty run still writes a valid, empty report). Use a temp dir:

```csharp
using Raun;
using Raun.Model;
using Raun.Reporting;
using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportSinkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raun-report-test-" + Guid.NewGuid().ToString("N"));

    public HtmlReportSinkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private static ScenarioNode Node(int i, string id, string phase, string t) => new()
    {
        Index = i, StepId = id, Phase = phase, OperationName = "Op" + i, DisplayNameTemplate = t,
        DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def() => new()
    {
        ScenarioId = "scn", DisplayName = "books", MethodName = "Ns.Booking",
        Nodes = [Node(0, "a", "Given", "Given patient Jane exists")],
    };

    private static StepResult Passed(ScenarioNode n) => new()
    {
        Node = n, DisplayName = n.DisplayNameTemplate, Status = StepStatus.Passed,
        StartedAt = T0, Duration = TimeSpan.FromMilliseconds(42),
    };

    [Fact]
    public async Task Writes_a_self_contained_html_file_on_run_finished()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReport.HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();

        await sink.PublishAsync(new RunStarted(1));
        await sink.PublishAsync(new ScenarioStarted(def));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));

        Assert.False(File.Exists(path)); // not written until RunFinished

        await sink.PublishAsync(new RunFinished());

        Assert.True(File.Exists(path));
        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("customer".Length >= 0 ? "books" : "", html, StringComparison.Ordinal); // scenario name present
        Assert.Contains("\"scenarioId\": \"scn\"", html.Replace(" ", ""), StringComparison.Ordinal) ;
        Assert.DoesNotContain("__RAUN_REPORT_JSON__", html, StringComparison.Ordinal); // token replaced
    }

    [Fact]
    public async Task Empty_run_still_writes_a_valid_report()
    {
        var path = Path.Combine(_dir, "raun-report.html");
        var sink = new HtmlReport.HtmlReportSink(path, new TestTimeProviderUtc(T0));

        await sink.PublishAsync(new RunStarted(0));
        await sink.PublishAsync(new RunFinished());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_write_failure_is_swallowed_not_thrown()
    {
        // Target a path whose directory does not exist and cannot be created (a file as a dir segment).
        var fileAsDir = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(fileAsDir, "x");
        var badPath = Path.Combine(fileAsDir, "nested", "report.html");
        var sink = new HtmlReport.HtmlReportSink(badPath, new TestTimeProviderUtc(T0));

        await sink.PublishAsync(new RunStarted(0));
        var ex = await Record.ExceptionAsync(async () => await sink.PublishAsync(new RunFinished()));

        Assert.Null(ex); // best-effort I/O: the run must never fail because the report could not be written
    }

    private sealed class TestTimeProviderUtc(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
```

- [ ] **Step 2: Run — fails (sink absent).**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportSinkTests"`
Expected: FAIL — `HtmlReportSink` does not exist.

- [ ] **Step 3: Implement the sink** in `src/Raun.Mtp/HtmlReport/HtmlReportSink.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using Raun.Model;
using Raun.Reporting;

namespace Raun.Mtp.HtmlReport;

/// <summary>
/// Subscribes to the run-event stream, accumulates the <see cref="HtmlReportModel"/>, and on
/// <see cref="RunFinished"/> renders the embedded template with the model's JSON and writes one
/// self-contained HTML file. Best-effort I/O: a write failure is swallowed (a broken report must
/// never fail the run); the bus also isolates any throw. Constructed only when <c>--report-html</c>
/// is set (design §3.D/§3.E).
/// </summary>
internal sealed class HtmlReportSink : RunEventSink
{
    private const string JsonToken = "/*__RAUN_REPORT_JSON__*/";
    private const string ResourceName = "Raun.Mtp.HtmlReport.report-template.html";

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly HtmlReportModelBuilder _builder = new();

    public HtmlReportSink(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _path = path;
        _timeProvider = timeProvider;
    }

    protected override ValueTask OnScenarioStartedAsync(ScenarioStarted e)
    {
        _builder.OnScenarioStarted(e.Definition);
        return default;
    }

    protected override ValueTask OnStepFinishedAsync(StepFinished e)
    {
        _builder.OnStepFinished(e.Definition, e.Result);
        return default;
    }

    protected override async ValueTask OnRunFinishedAsync(RunFinished e)
    {
        var generatedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var model = _builder.Build(generatedAt);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
        var html = LoadTemplate().Replace(JsonToken, json, StringComparison.Ordinal);

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(_path, html).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: surface via the bus Failures path by rethrowing into the bus? No — the bus
            // would record it; but to keep the report strictly non-fatal AND test it in isolation, we
            // swallow here. (The bus also isolates throws; either path keeps the run alive.)
        }
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(HtmlReportSink).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded report template '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

> Note on the swallow-vs-bus decision: design §3.D says the write failure is "recorded (surfaced via the bus `Failures` path), never thrown into the run." Implement it by **letting the I/O exception propagate out of `OnRunFinishedAsync`** so the `RunEventBus` records it in `Failures` (and the framework logs it). To make `A_write_failure_is_swallowed_not_thrown` reflect that, change that test to drive the sink **through a `RunEventBus`** and assert `bus.Failures` has one entry and no exception escaped — rather than catching inside the sink. Pick one model and keep the sink and its test consistent; the bus-recorded path matches the design, so prefer it: remove the `try/catch` from `OnRunFinishedAsync` and update the test to wrap the sink in `new RunEventBus([sink])`.

- [ ] **Step 4: Reconcile the sink and its failure test** per the note (bus-recorded path): drop the `try/catch`, and rewrite `A_write_failure_is_swallowed_not_thrown` to:

```csharp
    [Fact]
    public async Task A_write_failure_is_recorded_on_the_bus_not_thrown()
    {
        var fileAsDir = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(fileAsDir, "x");
        var badPath = Path.Combine(fileAsDir, "nested", "report.html");
        var bus = new RunEventBus([new HtmlReport.HtmlReportSink(badPath, new TestTimeProviderUtc(T0))]);

        await bus.PublishAsync(new RunStarted(0));
        var ex = await Record.ExceptionAsync(async () => await bus.PublishAsync(new RunFinished()));

        Assert.Null(ex);                 // never thrown into the run
        Assert.Single(bus.Failures);     // recorded for the framework to log
    }
```

- [ ] **Step 5: Run — green.**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportSinkTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit.**

```bash
git add src/Raun.Mtp/HtmlReport test/Raun.Mtp.Test/HtmlReportSinkTests.cs src/Raun.Mtp/Raun.Mtp.csproj
git commit -m "feat(report): embedded HTML renderer + best-effort report file sink"
```

---

## Phase 6 — Enablement: `--report-html` option provider + framework wiring

Realizes design §3.E. After this phase the report is produced end-to-end when the flag is present, and never otherwise.

### Task 6.1: `HtmlReportOptionsProvider`

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/HtmlReportOptionsProvider.cs`
- Test: `test/Raun.Mtp.Test/HtmlReportOptionsProviderTests.cs`

- [ ] **Step 1: Write the failing tests** in `test/Raun.Mtp.Test/HtmlReportOptionsProviderTests.cs`:

```csharp
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportOptionsProviderTests
{
    [Fact]
    public void Registers_the_flag_and_filename_options()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var names = provider.GetCommandLineOptions().Select(o => o.Name).ToList();

        Assert.Contains("report-html", names);
        Assert.Contains("report-html-filename", names);
    }

    [Fact]
    public void The_flag_takes_no_argument_and_the_filename_takes_exactly_one()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var byName = provider.GetCommandLineOptions().ToDictionary(o => o.Name);

        Assert.Equal(ArgumentArity.Zero, byName["report-html"].Arity);
        Assert.Equal(ArgumentArity.ExactlyOne, byName["report-html-filename"].Arity);
    }

    [Fact]
    public async Task Filename_argument_must_be_non_empty()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var filename = provider.GetCommandLineOptions().Single(o => o.Name == "report-html-filename");

        var ok = await provider.ValidateOptionArgumentsAsync(filename, ["report.html"]);
        var bad = await provider.ValidateOptionArgumentsAsync(filename, [""]);

        Assert.True(ok.IsValid);
        Assert.False(bad.IsValid);
    }
}
```

- [ ] **Step 2: Run — fails (provider absent).**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportOptionsProviderTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement the provider** in `src/Raun.Mtp/HtmlReport/HtmlReportOptionsProvider.cs`:

```csharp
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Raun.Mtp.HtmlReport;

/// <summary>
/// Registers Raun's HTML-report command-line options with Microsoft.Testing.Platform:
/// <c>--report-html</c> (a flag) and <c>--report-html-filename &lt;name&gt;</c> (default
/// <c>raun-report.html</c>). The report is written under MTP's <c>--results-directory</c>
/// (design §3.E). Generic names — Raun owns its loaded extension set, so collision risk is low.
/// </summary>
internal sealed class HtmlReportOptionsProvider : ICommandLineOptionsProvider
{
    internal const string EnableOption = "report-html";
    internal const string FilenameOption = "report-html-filename";
    internal const string DefaultFilename = "raun-report.html";

    public string Uid => "raun.mtp.htmlreport";
    public string Version => "1.0.0";
    public string DisplayName => "Raun HTML report";
    public string Description => "Writes a self-contained raun-report.html (Gantt timeline + resource lane).";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new CommandLineOption(EnableOption,
            "Write a self-contained HTML run report under the results directory.",
            ArgumentArity.Zero, isHidden: false),
        new CommandLineOption(FilenameOption,
            $"Filename for the HTML report (default '{DefaultFilename}').",
            ArgumentArity.ExactlyOne, isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
    {
        if (commandOption.Name == FilenameOption
            && (arguments.Length != 1 || string.IsNullOrWhiteSpace(arguments[0])))
        {
            return ValidationResult.InvalidTask($"'--{FilenameOption}' requires a non-empty filename.");
        }

        return ValidationResult.ValidTask;
    }

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
        => ValidationResult.ValidTask;
}
```

- [ ] **Step 4: Run — green.**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportOptionsProviderTests"`
Expected: PASS (3 tests).

### Task 6.2: Resolve the report path and wire the sink into the framework

**Files:**
- Create: `src/Raun.Mtp/HtmlReport/HtmlReportPath.cs`
- Modify: `src/Raun.Mtp/RaunTestFramework.cs`
- Modify: `src/Raun.Mtp/RaunTestApplication.cs`
- Test: `test/Raun.Mtp.Test/HtmlReportPathTests.cs`

- [ ] **Step 1: Write failing tests** for path resolution in `test/Raun.Mtp.Test/HtmlReportPathTests.cs`. Resolution is pure over two inputs (is-the-flag-set, filename-or-null, results-dir), so test it directly without MTP plumbing:

```csharp
using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportPathTests
{
    [Fact]
    public void Returns_null_when_the_flag_is_absent()
        => Assert.Null(HtmlReport.HtmlReportPath.Resolve(enabled: false, filename: null, resultsDirectory: @"C:\r"));

    [Fact]
    public void Defaults_the_filename_under_the_results_directory()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(enabled: true, filename: null, resultsDirectory: @"C:\r");
        Assert.Equal(Path.Combine(@"C:\r", "raun-report.html"), path);
    }

    [Fact]
    public void Honors_an_explicit_filename()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(enabled: true, filename: "run.html", resultsDirectory: @"C:\r");
        Assert.Equal(Path.Combine(@"C:\r", "run.html"), path);
    }

    [Fact]
    public void Falls_back_to_current_directory_when_results_directory_is_unknown()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(enabled: true, filename: null, resultsDirectory: null);
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "raun-report.html"), path);
    }
}
```

- [ ] **Step 2: Run — fails.**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~HtmlReportPathTests"`
Expected: FAIL — type absent.

- [ ] **Step 3: Implement path resolution** in `src/Raun.Mtp/HtmlReport/HtmlReportPath.cs` with a pure core + an `IServiceProvider` adapter:

```csharp
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp.HtmlReport;

/// <summary>Resolves the absolute HTML report path from MTP's command-line options + configuration,
/// or returns <see langword="null"/> when <c>--report-html</c> is absent (design §3.E).</summary>
internal static class HtmlReportPath
{
    // MTP's well-known results-directory configuration key (PlatformConfigurationConstants is internal).
    private const string ResultsDirectoryKey = "platformOptions:resultDirectory";

    /// <summary>Pure resolution from the three inputs. Falls back to the current directory when MTP
    /// did not supply a results directory.</summary>
    public static string? Resolve(bool enabled, string? filename, string? resultsDirectory)
    {
        if (!enabled)
        {
            return null;
        }

        var dir = string.IsNullOrEmpty(resultsDirectory) ? Directory.GetCurrentDirectory() : resultsDirectory;
        var name = string.IsNullOrWhiteSpace(filename) ? HtmlReportOptionsProvider.DefaultFilename : filename;
        return Path.Combine(dir, name);
    }

    /// <summary>Reads the inputs off the framework's MTP service provider.</summary>
    public static string? Resolve(IServiceProvider? services)
    {
        if (services is null)
        {
            return null;
        }

        ICommandLineOptions options = services.GetCommandLineOptions();
        if (!options.IsOptionSet(HtmlReportOptionsProvider.EnableOption))
        {
            return null;
        }

        string? filename = options.TryGetOptionArgumentList(HtmlReportOptionsProvider.FilenameOption, out var args)
            && args.Length > 0
            ? args[0]
            : null;

        IConfiguration configuration = services.GetConfiguration();
        return Resolve(enabled: true, filename, resultsDirectory: configuration[ResultsDirectoryKey]);
    }
}
```

- [ ] **Step 4: Wire the HTML branch into `OnExecuteAsync`.** In `src/Raun.Mtp/RaunTestFramework.cs`, change the Phase-3 sink-list construction to add the HTML sink when the path resolves:

```csharp
        var uids = ReadUidFilter(filter);

        var sinks = new List<IRunEventSink> { new MtpReportSink(sessionUid, messageBus, this) };
        if (HtmlReport.HtmlReportPath.Resolve(_services) is { } reportPath)
        {
            sinks.Add(new HtmlReport.HtmlReportSink(reportPath, TimeProvider.System));
        }

        var bus = new RunEventBus(sinks);
        var loop = new RaunRunLoop(EnumerateRegisteredScenarios);
        await loop.RunAsync(uids, bus, cancellationToken).ConfigureAwait(false);

        foreach (var failure in bus.Failures)
        {
            NodeDiagnostics.Log("report-sink-failure", failure.ToString());
        }

        operationComplete();
```

- [ ] **Step 5: Register the option provider** in `src/Raun.Mtp/RaunTestApplication.cs`, before `RegisterTestFramework` (after `configure?.Invoke(builder);`, line 37):

```csharp
        builder.CommandLine.AddProvider(() => new HtmlReport.HtmlReportOptionsProvider());
```

- [ ] **Step 6: Full solution green.**

Run: `dotnet test Raun.slnx -c Debug`
Expected: `Test run summary: Passed!` — all projects, including the new option/path/sink/model tests.

- [ ] **Step 7: Commit.**

```bash
git add src/Raun.Mtp test/Raun.Mtp.Test/HtmlReportOptionsProviderTests.cs test/Raun.Mtp.Test/HtmlReportPathTests.cs
git commit -m "feat(report): --report-html option provider + framework wiring"
```

---

## Phase 7 — End-to-end smoke on the sample

Realizes design §5 "Sample" + §6.

### Task 7.1: Generate and eyeball a real report

**Files:** (no source changes; a manual verification gate)

- [ ] **Step 1: Run the sample with the flag.**

Run: `dotnet run --project samples/AppointmentTests -c Debug -- --report-html`
Expected: the run passes; an MTP results directory (default `samples/AppointmentTests/bin/Debug/net10.0/TestResults` or the platform default) contains `raun-report.html`.

- [ ] **Step 2: Locate the file.**

Run: `Get-ChildItem -Recurse -Filter raun-report.html samples/AppointmentTests | Select-Object FullName`
Expected: one path printed.

- [ ] **Step 3: Open it and confirm visually** (or assert structurally): the `customer books with parallel arrange` scenario shows `PatientExists` and `AvailableSlot` on **two lanes** (overlapping bars), each step bar drills into its logs/effects, and the resource lane shows `Patient:Jane` / `Slot:1` create markers. Sub-ms `Task.Yield` bars will be tiny — that's expected (design §7); the layout is validated by the synthetic-duration unit tests.

- [ ] **Step 4: Confirm the flag is off by default.**

Run: `dotnet run --project samples/AppointmentTests -c Debug` then `Get-ChildItem -Recurse -Filter raun-report.html samples/AppointmentTests`
Expected: no new `raun-report.html` written by this run (the sink only attaches when `--report-html` is present).

- [ ] **Step 5: Final full verification.**

Run: `dotnet test Raun.slnx -c Debug`
Expected: `Test run summary: Passed!`.

- [ ] **Step 6: Commit any sample/doc tweaks** (only if Step 3 required a sample change to surface effects; otherwise skip). Then update the design doc status:

```bash
# In docs/superpowers/specs/2026-06-07-html-report-design.md, change the Status line to:
#   **Status:** Implemented (2026-06-09) on MTP 2.2.3.
git add docs/superpowers/specs/2026-06-07-html-report-design.md
git commit -m "docs: mark HTML report design implemented"
```

---

## Self-Review

**Spec coverage (design §3–§8):**
- §3.A run-event bus → Phase 1. §3.B timestamps → Phase 2. §3.C reporter→sink + run-loop emitter → Phase 3. §3.D HTML sink + renderer → Phases 4–5. §3.E option provider + wiring → Phase 6. §4 JSON model → Phase 4 (snapshot). §5 testing plan → mapped per phase (bus, scheduler, run loop, HTML sink snapshot, option provider, regression, sample). §6 out-of-scope honored (end-of-run write only, string data via `ToString()`, no locking visuals). §8 file list → File Structure section.
- **Added beyond the design:** Phase 0 (MTP 2.x migration), required because the design's §3.E command-line API is MTP-version-specific and the repo was on 1.9.1.

**Deviations from the design (deliberate, noted inline):**
- `StepResult.StartedAt` is `required` (design §3.B) → existing reporter-test `StepResult` constructions gain `StartedAt = TestInstant` (Phase 3 mechanical transform). The scheduler always stamps it.
- The reporter's `TimingProperty` becomes `StartedAt`-anchored (design §3.B "bonus"); the only existing timing assertion checks `Duration`, so it still holds — "byte-for-byte unchanged" is true for state/output/attachments, accurate-er for the timing window.
- Report-write failure uses the **bus `Failures`** path (per §3.D) rather than an in-sink swallow; the sink's failure test drives it through a `RunEventBus` (Phase 5, Step 4).

**Type consistency:** `IRunEventSink.PublishAsync` returns `ValueTask` everywhere; `RunScenario` delegate returns `Task<IReadOnlyList<StepResult>>` (Phase 3) and the default + stub both honor it; `HtmlReportModelBuilder.Build(string)` and `HtmlReportSink`/`HtmlReportPath`/`HtmlReportOptionsProvider` names are used identically across tasks; option constants (`EnableOption`/`FilenameOption`/`DefaultFilename`) are referenced from both the provider and `HtmlReportPath`.

**Coverage change (Task 0.2):** the 3 reflective `RequestDispatch` unit tests are deleted, not replaced 1:1. The `ExecuteRequestAsync` routing they covered is now covered only by the real-host E2E (the `AppointmentTests` sample under `dotnet test`, which issues genuine discover + run requests) plus the direct `OnDiscover`/`OnExecute` worker-method tests. This is a deliberate trade: a fast unit test of trivial glue, for the removal of reflection into MTP host internals that breaks across versions.

**Open risk to watch during execution:** `NodeDiagnostics.Log(string, string)` overload may not exist — Task 3.3 Step 2 adds it against the file's existing mechanism (read the file first). Everything else is pinned against MTP 2.2.x reflection or proven by the Phase-0 probe.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-09-html-report-and-mtp2.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
