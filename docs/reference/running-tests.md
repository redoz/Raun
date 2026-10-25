# Running tests

[Start here](../../README.md) · [Writing scenarios](scenario-authoring.md) · [Resources](resources.md)

## Install and run

Use **.NET SDK 10.0.300 or later**. Create a console project and add Raun:

```bash
dotnet new console -n MyScenarios
cd MyScenarios
dotnet add package Raun.Mtp --prerelease
dotnet add package xunit.v3.assert
```

Delete the template's `Program.cs`; Raun generates the entry point. Add your scenario and step classes.
Keep scenarios in the executable project. Referenced libraries can contain shared steps and domain types.

```bash
dotnet run -- --report-html --results-directory "$PWD/out"
```

Use an absolute results path, as above. MTP resolves a relative results path from the test executable directory.

For `dotnet test`, select MTP in the solution's `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

## Concurrent scenarios and shared capacity

Scenarios run concurrently, up to the processor count by default. Each gets its own DI scope and trace.
Scenario scopes are disposed asynchronously after teardown, including async-only scoped services.
Resource admission waits for disposal to finish. Disposal failures surface as framework errors after
already-running scenarios drain; they do not change an already-reported teardown result.
Use isolated test data. Start an existing suite with `--max-parallel-scenarios 1`.
Clamp independent steps with `--max-parallel-steps 8`. This limit applies **per scenario**, not to the
whole run; four active scenarios with eight steps each can run up to 32 steps concurrently.
`0` (default) leaves steps unbounded; `1` runs one step at a time within each scenario. Dependencies
and resource-conflict rules still apply. Work started inside a step is outside this scheduler limit.
Set the code default with `RaunTestApplication.RunAsync(args, maxParallelSteps: 8)`,
`RunLoop(..., maxParallelSteps: 8)`, or `AspireRunOptions.MaxParallelSteps`. The CLI value overrides it.
Declare shared infrastructure with resource tokens:

```csharp
[SharedResource] public sealed class Database : IContendedResource;
[ExclusiveResource] public sealed class SerialPort : IContendedResource;
[PooledResource(3)] public sealed class Smtp : IContendedResource;

// Put this on a scenario that needs the database to itself:
[Uses<Database>(LockMode.Exclusive)]
```

Put `[Uses<T>]` on steps, scenarios, classes, or assemblies. Shared use permits concurrent holders; a pool limits their number.
Exclusive use waits for all holders to finish. Raun acquires the full set before Setup and releases it after teardown.
The report shows time spent waiting.

## Reports and test selection

`--report-html` writes a self-contained report. Its Scenarios view shows step results, timing, a resource matrix, logs, attachments, and errors.
Select a resource reference to highlight its uses; click outside the matrix or press Esc to clear the selection.
The IDE reports each step as a test. Selecting a step runs its dependencies, branch conditions, merge sources, and cleanup, but no later steps.

```bash
dotnet run -- --treenode-filter '/*/*/*/customer books an appointment/*'
dotnet run -- --treenode-filter '/*/*/*/*/*[Phase=When]'
dotnet run -- --treenode-filter '/**[area=users]'
dotnet run -- --treenode-filter '/**[(area=booking)&(tier!=smoke)]'
dotnet run -- --max-parallel-scenarios 4 --maximum-failed-tests 1
```

[Tags](scenario-authoring.md#tags) are filter properties on every step of their scenario. A key with
several values matches when any value does; `[key!=value]` matches only when none does. Combine with
`&` and `|`, and use `*` wildcards in values. Matching ignores case. Use `!=` to exclude: the
platform's `!(...)` does not negate reliably. In the HTML report, select tags in the bar above the
scenarios or on a scenario's chips to show scenarios with any of them. The selection is kept in the
URL (`?tag=area:users&tag=area:lifecycle`), so a filtered report can be reloaded or shared. A tag every
scenario carries is offered in the bar but left off the chips.

Paths are `/{assembly}/{namespace}/{class}/{scenario}/{step}`.
Remove numbering prefixes from `--list-tests` step names when filtering. Get scenario names from `[Scenario]` or the report.
Escape `%` as `%25`, `/` as `%2F`, and literal filter operators with a backslash.
IDEs use `--filter-uid`. MTP does not accept `--filter` or `--nologo` here.
`--maximum-failed-tests` stops new scenarios; running scenarios finish.

Expanded scenarios include a small dependency pixel matrix next to the status marks. Each column
represents a step in narrative order. A hollow square marks the current step; colored cells name its
dependencies. Diamonds mark branch conditions, hollow dependency cells mark alternative merge inputs,
and short bars mark ordering constraints. Untaken paths stay present but muted. Hover or focus a cell
to name the connection and highlight its two steps. Wide matrices scroll sideways with all rows aligned.

### Source navigation

Resource lanes collapse to a compact activity column when they do not fit. Click its symbol to open a wider resource-lane overlay. It matches the expanded scenario’s height, keeps each lane aligned with its step, and moves with the report’s vertical scroll. Scroll horizontally or use the arrow controls to see more lanes. Escape or a click outside closes it. Type and method names appear in italics when you hover over or focus a group or scenario heading.

To link those names to source, pass a file-view base URL that includes your branch or commit:

```sh
dotnet run -- --report-html --report-source-base-url "https://github.com/owner/repo/blob/commit/"
# Azure DevOps:
dotnet run -- --report-html --report-source-base-url "https://dev.azure.com/org/project/_git/repo?version=GCcommit"
```

Raun makes paths relative to the source file's Git checkout root. Set `--report-source-root <build-checkout-root>` when the build checkout is unavailable at report time. Files outside that root and locations without a known line remain plain text. For a custom host, pass `HtmlReportSourceOptions` as the fourth argument to `HtmlReportSink`. Source links select the class declaration for groups and the scenario method for scenarios. Omitting the base URL keeps the names as plain text.


## Preflight, extensions, and coverage

For a custom `Program.cs`, set `<RaunGenerateProgram>false</RaunGenerateProgram>` in the project file.
Build and dispose your service provider there. Pass it to Raun and register MTP extensions:

```csharp
return await RaunTestApplication.RunAsync(args,
    services: services,
    preflight: ctx => StartEnvironment(ctx.CancellationToken),
    configure: builder => builder.AddTrxReportProvider());
```

Preflight runs once and reports as a test node. Failure skips all scenarios.
The TRX example needs `Microsoft.Testing.Extensions.TrxReport` and `using Microsoft.Testing.Extensions;`; enable it with `--report-trx`.
The generated entry point does not register optional extensions.

Coverlet or Microsoft Code Coverage records which code ran.
Raun.Coverage maps those execution hits to source branches and classifies their purpose.
The report shows coverage for logical branches and for all branches.

Every branch has one outcome: covered, partly covered, not covered, or can't tell. With Microsoft coverage XML, an argument check counts as covered once a test triggers it. With Coverlet, it needs both the passing and the rejecting path. Microsoft coverage XML can show whether expressions such as `??` and `?:` ran, but not which path they took.

Classified coverage is optional. Add a `Raun.Coverage` package reference alongside a compatible collector.
`Raun.Mtp` and `Raun.Aspire` do not depend on coverage or its Roslyn/MSBuild analysis assets.
Generated entry points enable the integration when the leaf package is referenced. For a custom `Main`,
register `builder.UseClassifiedCoverage()` in `RaunTestApplication.RunAsync`'s `configure` callback
(or Aspire's `ConfigureTestApplication`), using namespace `Raun.Mtp`.
Middleware registration also enables the integration. The middleware API retains its source namespace
and ships in `Raun.Coverage`.

Register a compatible collector for the report's Coverage view.
Use `--report-html --coverage --coverage-output-format xml`, or `--report-html --coverlet --coverlet-output-format json`.
On Linux and macOS, the Microsoft collector instruments assemblies in place by default. The test host controller
has the test app and Raun loaded from those files, so it can crash with `BadImageFormatException` after the run.
Set `EnableStaticManagedInstrumentation` to `false` and `EnableDynamicManagedInstrumentation` to `true` under
`codeCoverage:Configuration:CodeCoverage`, as in the [AppointmentTests testconfig.json](../../samples/AppointmentTests/testconfig.json).
This is the upstream collector bug [microsoft/codecoverage#203](https://github.com/microsoft/codecoverage/issues/203), still open.
Dynamic instrumentation is not supported on Linux or macOS Arm64, where this setting gives empty coverage; use Coverlet there.
Raun classifies branches as argument checks, error handling, null propagation, logging, or logic.
Add project rules with `builder.UseBranchClassificationMiddleware(() => new ProjectRule())` and `IBranchClassificationMiddleware` from `Raun.Coverage`.
See the [provider setup](../../samples/AspireAppointments/AspireAppointments.Tests/Program.cs).

## Tracing, simulated time, and Aspire

Subscribe to `RaunTelemetry.SourceName` with OpenTelemetry. Raun emits scenario and step spans, with logs and resource events.
Instrumented HTTP calls link application spans to the active step. Your exporter sends the spans; Raun does not export them.

For report samples, set `simulateTime: true` on `RunAsync` and call `Context.SimulateElapsed(TimeSpan.FromSeconds(1))` in steps.
This advances the simulated clock without a real delay. It has no effect in normal runs.

`Raun.Aspire` starts your AppHost in preflight, waits for named resources, and registers the running application:

```csharp
return await RaunAspire.RunAsync<Projects.MyAppHost>(args, aspire => aspire.WaitFor("api"));
```

See [AspireAppointments](../../samples/AspireAppointments) for clients, service registration, and trace export.
