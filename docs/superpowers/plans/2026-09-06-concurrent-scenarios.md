# Concurrent Scenarios and Contended Resources Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scenarios run concurrently (default degree = processor count, adjustable in code and with `--max-parallel-scenarios`), and a suite declares what its scenarios contend for with `[ExclusiveResource]`/`[SharedResource]`/`[PooledResource(N)]` token types and `[Uses<T>]`, so conflicting scenarios never overlap.

**Architecture:** `src/Raun` gets the declaration surface, a synchronous `ContentionGate`, richer run events, and new span attributes. `src/Raun.Generator` unions `[Uses<T>]` from step methods, scenario methods, classes, and the assembly onto `ScenarioDefinition.Uses`, and adds analyzer rule RAUN015. `src/Raun.Mtp` replaces the sequential `foreach` in `RaunRunLoop` with a launcher that admits scenarios through the gate up to the degree, serializes the event bus, and reports wait time in the HTML report. Lifecycle roles, the ledger, RAUN013 and lineage are untouched.

**Tech Stack:** .NET 10 / C# 14, Microsoft.Testing.Platform, Roslyn incremental generator + analyzer (netstandard2.0), xUnit v3, Verify snapshots, Jujutsu (`jj`) for version control.

**Spec:** `docs/superpowers/specs/2026-09-06-concurrent-scenarios-design.md`

## Global Constraints

- `dotnet build Raun.slnx` must end with **0 warnings**: `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true` repo-wide (`Directory.Build.props`). Fix code, do not suppress, unless the rule is genuinely wrong for the case (comment why, as `src/Raun/Phases.cs` does for CA1040).
- Version control is **Jujutsu**. Commit with `jj commit -m "<subject>" -m "<body>"`. Never run `git commit/add/checkout/reset/rebase/stash/merge/push`. There is no staging area; `jj` snapshots the working copy. Do not move the `main` bookmark and do not push; integration is Patrik's.
- Commit messages: conventional-commit subject (`feat(scheduler): …`, `fix(generator): …`, `docs: …`), a body that says why. **No `Co-Authored-By` or tooling trailers.**
- Never add a `<Version>` anywhere (MinVer is tag-driven).
- MTP rejects `--nologo` and `--filter`. Run whole test projects: `dotnet test Raun.slnx` or `dotnet test test/<Project>/<Project>.csproj`.
- Every new analyzer rule needs a row in `src/Raun.Generator/AnalyzerReleases.Unshipped.md` (RS2000).
- Verify snapshots: a changed snapshot leaves `*.received.txt` next to `*.verified.txt` under `test/Raun.Generator.Test/Snapshots` (or `test/Raun.Mtp.Test/`). Review the diff, then move received over verified with `mv` (in bash) — never edit a verified file by hand.
- Pipe-through-grep hides build failures. In bash use `set -o pipefail` or check `${PIPESTATUS[0]}`; simpler: run `dotnet build Raun.slnx 2>&1 | tail -5` and look for `0 Warning(s)` / `0 Error(s)`.
- Test projects are also under `latest-all`: a private nested class that is never instantiated trips CA1812, a public nested type trips CA1034. Token types used in tests are therefore **public top-level** classes.
- Test helper naming and style follow the existing files: xUnit `[Fact]`, `Assert.*`, snake_case test names that read as sentences.

---

## File Structure

**Created**

| Path | Responsibility |
|---|---|
| `src/Raun/Resources/ContendedResources.cs` | `IContendedResource`, the three kind attributes, `UsesAttribute<T>`, `ContendedResourceUse` |
| `src/Raun/Scheduling/ContentionGate.cs` | Synchronous all-or-nothing admission over a scenario's use set |
| `src/Raun.Mtp/RunOptionsProvider.cs` | MTP command-line option `--max-parallel-scenarios` |
| `src/Raun.Mtp/ScenarioParallelism.cs` | Parses/resolves the degree (CLI overrides code default) |
| `test/Raun.Test/ContendedResourceTokens.cs` | Public token types for core tests |
| `test/Raun.Test/ContentionGateTests.cs` | Gate behaviour |
| `test/Raun.Test/Resources/ContendedResourceAttributeTests.cs` | Attribute surface |
| `test/Raun.Mtp.Test/ContendedResourceTokens.cs` | Public token types for MTP tests |
| `test/Raun.Mtp.Test/ScenarioParallelismTests.cs` | CLI option and degree resolution |
| `test/Raun.Generator.Test/UsesLoweringTests.cs` | Generator lowering of `[Uses<T>]` |
| `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Uses_scenario.verified.txt` | New snapshot |

**Modified**

| Path | Change |
|---|---|
| `src/Raun/Model/ScenarioDefinition.cs` | `Uses` property |
| `src/Raun/Reporting/RunEvent.cs` | `RunStarted.Scenarios`, `ScenarioStarted.Waited`/`WaitedFor` |
| `src/Raun/Reporting/RunEventBus.cs` | Serialize concurrent publishers |
| `src/Raun/Tracing/RaunTelemetry.cs` | Three scenario attributes |
| `src/Raun.Mtp/RaunRunLoop.cs` | Launcher, degree, gate, wait accounting, span tags |
| `src/Raun.Mtp/RaunTestFramework.cs` | Degree ctor overload, CLI override |
| `src/Raun.Mtp/RaunTestApplication.cs` | `maxParallelScenarios` parameter, provider registration |
| `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs` | `Uses`, `WaitedMs`, `WaitedFor` on `ReportScenario` |
| `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs` | Order from `RunStarted`, wall span, new fields |
| `src/Raun.Mtp/HtmlReport/HtmlReportSink.cs` | Forward `RunStarted` and wait data |
| `src/Raun.Mtp/HtmlReport/report-template.html` | "waited … for …" line in the scenario header |
| `src/Raun.Aspire/AspireRunOptions.cs`, `RaunAspire.cs` | `MaxParallelScenarios` pass-through |
| `src/Raun.Generator/Lowering/Ir.cs`, `AttributeReader.cs`, `ScenarioParser.cs`, `Emit/ScenarioEmitter.cs` | Uses lowering |
| `src/Raun.Generator/Analysis/Descriptors.cs`, `ScenarioAnalyzer.cs`, `AnalyzerReleases.Unshipped.md` | RAUN015 |
| `samples/AspireAppointments/AspireAppointments.Api/Program.cs`, `AspireAppointments.Tests/Actors.cs`, `AppointmentsDsl.cs`, `Scenarios.cs` | Exclusive "clear the schedule" scenario |
| `README.md`, `AGENTS.md`, two specs | Docs |
| `test/Raun.Mtp.Test/RunLoopTests.cs`, `HtmlReportModelBuilderTests.cs`, `HtmlReportSinkTests.cs`, `test/Raun.Test/Reporting/RunEventBusTests.cs`, `test/Raun.Generator.Test/AnalyzerTests.cs`, `SampleSources.cs`, `GeneratorSnapshotTests.cs` | Tests |

---

### Task 1: Declaration surface and `ScenarioDefinition.Uses`

**Files:**
- Create: `src/Raun/Resources/ContendedResources.cs`
- Modify: `src/Raun/Model/ScenarioDefinition.cs` (after `TeardownPolicy`, before `Nodes`)
- Create: `test/Raun.Test/ContendedResourceTokens.cs`
- Create: `test/Raun.Test/Resources/ContendedResourceAttributeTests.cs`

**Interfaces:**
- Produces: `Raun.IContendedResource`; `Raun.ExclusiveResourceAttribute`; `Raun.SharedResourceAttribute`; `Raun.PooledResourceAttribute(int capacity)` with `int Capacity`; `Raun.UsesAttribute<T> where T : IContendedResource` with ctors `()` and `(LockMode mode)` and `LockMode Mode` (default `Shared`); `Raun.ContendedResourceUse(Type Resource, LockMode Mode)` readonly record struct; `ScenarioDefinition.Uses : IReadOnlyList<ContendedResourceUse>` defaulting to `[]`.

- [ ] **Step 1: Write the token types for tests**

`test/Raun.Test/ContendedResourceTokens.cs`:

```csharp
namespace Raun.Test;

// Public top-level on purpose: a private nested token would trip CA1812 (never instantiated) and a
// public nested one CA1034. They are names, never instances — see the concurrent-scenarios design.

[ExclusiveResource]
public sealed class ExclusiveDb : IContendedResource;

[SharedResource]
public sealed class SharedCatalog : IContendedResource;

[PooledResource(2)]
public sealed class PooledSmtp : IContendedResource;
```

- [ ] **Step 2: Write the failing attribute tests**

`test/Raun.Test/Resources/ContendedResourceAttributeTests.cs`:

```csharp
using System.Reflection;
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// The declaration surface for cross-scenario contention: token types carry exactly one kind
/// attribute, [Uses&lt;T&gt;] declares a need with a mode, and a definition carries its reduced uses.
/// </summary>
public class ContendedResourceAttributeTests
{
    [Fact]
    public void Kind_attributes_target_types_only_and_are_not_inherited()
    {
        foreach (var kind in new[] { typeof(ExclusiveResourceAttribute), typeof(SharedResourceAttribute), typeof(PooledResourceAttribute) })
        {
            var usage = kind.GetCustomAttribute<AttributeUsageAttribute>();
            Assert.NotNull(usage);
            Assert.Equal(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, usage.ValidOn);
            Assert.False(usage.Inherited);
        }
    }

    [Fact]
    public void Pooled_resource_exposes_its_capacity()
    {
        var attr = typeof(PooledSmtp).GetCustomAttribute<PooledResourceAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(2, attr.Capacity);
    }

    [Fact]
    public void Uses_targets_methods_classes_and_assemblies_and_allows_multiple()
    {
        var usage = typeof(UsesAttribute<>).GetCustomAttribute<AttributeUsageAttribute>();
        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, usage.ValidOn);
        Assert.True(usage.AllowMultiple);
    }

    [Fact]
    public void A_bare_use_is_shared_and_a_mode_can_be_given()
    {
        Assert.Equal(LockMode.Shared, new UsesAttribute<SharedCatalog>().Mode);
        Assert.Equal(LockMode.Exclusive, new UsesAttribute<SharedCatalog>(LockMode.Exclusive).Mode);
    }

    [Fact]
    public void A_definition_has_no_uses_unless_given_some()
    {
        var bare = new ScenarioDefinition { ScenarioId = "s", DisplayName = "s", MethodName = "N.s", Nodes = [] };
        Assert.Empty(bare.Uses);

        var declared = new ScenarioDefinition
        {
            ScenarioId = "s", DisplayName = "s", MethodName = "N.s", Nodes = [],
            Uses = [new ContendedResourceUse(typeof(ExclusiveDb), LockMode.Exclusive)],
        };
        var use = Assert.Single(declared.Uses);
        Assert.Equal(typeof(ExclusiveDb), use.Resource);
        Assert.Equal(LockMode.Exclusive, use.Mode);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -15`
Expected: build errors — `IContendedResource`, `ExclusiveResourceAttribute`, `UsesAttribute<>`, `ContendedResourceUse`, `Uses` do not exist.

- [ ] **Step 4: Write the surface**

`src/Raun/Resources/ContendedResources.cs`:

```csharp
using System;

namespace Raun;

/// <summary>
/// Marks a <b>token type</b> that scenarios contend for in the system under test — one database
/// a few scenarios must have to themselves, a pool of three SMTP servers, a serial port. The type
/// is a name, never an instance: Raun does not construct, inject, or scope it. Carry exactly one
/// of <see cref="ExclusiveResourceAttribute"/>, <see cref="SharedResourceAttribute"/>,
/// <see cref="PooledResourceAttribute"/>, and declare a need with <see cref="UsesAttribute{T}"/>.
/// Not to be confused with <see cref="IResource{TSelf}"/>, which is data a step traces.
/// </summary>
#pragma warning disable CA1040 // Avoid empty interfaces — a deliberate marker, like IPhase; it enables the compile-time constraint on Uses<T>.
public interface IContendedResource;
#pragma warning restore CA1040

/// <summary>One holder at a time. Every use, shared or exclusive, takes the single slot.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class ExclusiveResourceAttribute : Attribute;

/// <summary>Any number of shared holders; an exclusive use waits for zero holders and blocks new ones.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class SharedResourceAttribute : Attribute;

/// <summary>Up to <see cref="Capacity"/> shared holders; an exclusive use takes every slot.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
public sealed class PooledResourceAttribute : Attribute
{
    /// <param name="capacity">How many scenarios may hold the resource at once; at least 1.</param>
    public PooledResourceAttribute(int capacity) => Capacity = capacity;

    /// <summary>How many scenarios may hold the resource at once.</summary>
    public int Capacity { get; }
}

/// <summary>
/// Declares that the scenario(s) this is placed on need <typeparamref name="T"/>. On a DSL step
/// method it means every scenario that calls the step; on a <c>[Scenario]</c> method, that
/// scenario; on a class, every scenario declared in it; on the assembly, every scenario. All sites
/// are additive; per type, <see cref="LockMode.Exclusive"/> wins. The whole set is acquired
/// atomically before the scenario starts and released after its teardown.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class UsesAttribute<T> : Attribute
    where T : IContendedResource
{
    /// <summary>A shared use: one slot of the resource.</summary>
    public UsesAttribute()
    {
    }

    /// <param name="mode"><see cref="LockMode.Shared"/> takes one slot; <see cref="LockMode.Exclusive"/> takes every slot.</param>
    public UsesAttribute(LockMode mode) => Mode = mode;

    /// <summary>The access this use needs.</summary>
    public LockMode Mode { get; } = LockMode.Shared;
}

/// <summary>One reduced use of a contended resource by a scenario, as emitted by the generator.</summary>
/// <param name="Resource">The token type (implements <see cref="IContendedResource"/>).</param>
/// <param name="Mode">The strongest mode any site declared for this type.</param>
public readonly record struct ContendedResourceUse(Type Resource, LockMode Mode);
```

In `src/Raun/Model/ScenarioDefinition.cs`, after the `TeardownPolicy` property add:

```csharp
    /// <summary>
    /// The contended resources this scenario needs, reduced per type (exclusive wins), from every
    /// <c>[Uses&lt;T&gt;]</c> on its steps' DSL methods, the scenario method, its classes, and the
    /// assembly. The run loop acquires the whole set before the scenario starts. Empty when nothing
    /// is declared.
    /// </summary>
    public IReadOnlyList<ContendedResourceUse> Uses { get; init; } = [];
```

- [ ] **Step 5: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(resources): contended-resource declaration surface" -m "Token types carry one of [ExclusiveResource], [SharedResource], [PooledResource(N)]; [Uses<T>] declares a scenario's need with a LockMode; ScenarioDefinition carries the reduced set. Nothing consumes it yet. Design: docs/superpowers/specs/2026-09-06-concurrent-scenarios-design.md."
```

---

### Task 2: `ContentionGate`

**Files:**
- Create: `src/Raun/Scheduling/ContentionGate.cs`
- Create: `test/Raun.Test/ContentionGateTests.cs`

**Interfaces:**
- Consumes: `ContendedResourceUse`, the kind attributes (Task 1).
- Produces: `Raun.Scheduling.ContentionGate` with `bool TryAcquire(IReadOnlyList<ContendedResourceUse> uses, out Type? refusedBy)` and `void Release(IReadOnlyList<ContendedResourceUse> uses)`. Static helper `ContentionGate.Reduce(IReadOnlyList<ContendedResourceUse>) : List<ContendedResourceUse>` (internal).

- [ ] **Step 1: Write the failing tests**

`test/Raun.Test/ContentionGateTests.cs`:

```csharp
using Raun.Scheduling;
using Xunit;

namespace Raun.Test;

/// <summary>
/// Admission across scenarios: a scenario's whole use set is granted at once or not at all, an
/// exclusive resource has one slot, a shared resource has any number for shared uses, a pooled
/// resource has a capacity, and an exclusive use of any of them waits for zero holders.
/// </summary>
public class ContentionGateTests
{
    private static ContendedResourceUse Shared<T>() where T : IContendedResource => new(typeof(T), LockMode.Shared);
    private static ContendedResourceUse Exclusive<T>() where T : IContendedResource => new(typeof(T), LockMode.Exclusive);

    [Fact]
    public void An_empty_use_set_is_always_admitted()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([], out var refusedBy));
        Assert.Null(refusedBy);
    }

    [Fact]
    public void Exclusive_resource_has_one_slot_for_shared_and_exclusive_uses_alike()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<ExclusiveDb>()], out _));

        Assert.False(gate.TryAcquire([Shared<ExclusiveDb>()], out var refusedBy));
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.False(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));

        gate.Release([Shared<ExclusiveDb>()]);
        Assert.True(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));
    }

    [Fact]
    public void Shared_resource_admits_any_number_of_shared_uses()
    {
        var gate = new ContentionGate();
        for (var i = 0; i < 50; i++)
        {
            Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));
        }
    }

    [Fact]
    public void Exclusive_use_of_a_shared_resource_waits_for_zero_holders_and_blocks_new_ones()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));

        Assert.False(gate.TryAcquire([Exclusive<SharedCatalog>()], out var refusedBy));
        Assert.Equal(typeof(SharedCatalog), refusedBy);

        gate.Release([Shared<SharedCatalog>()]);
        Assert.True(gate.TryAcquire([Exclusive<SharedCatalog>()], out _));
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>()], out _));

        gate.Release([Exclusive<SharedCatalog>()]);
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>()], out _));
    }

    [Fact]
    public void Pooled_resource_caps_shared_holders_at_its_capacity()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));

        Assert.False(gate.TryAcquire([Shared<PooledSmtp>()], out var refusedBy));
        Assert.Equal(typeof(PooledSmtp), refusedBy);

        gate.Release([Shared<PooledSmtp>()]);
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
    }

    [Fact]
    public void Exclusive_use_of_a_pooled_resource_takes_every_slot()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Exclusive<PooledSmtp>()], out _));
        Assert.False(gate.TryAcquire([Shared<PooledSmtp>()], out _));

        gate.Release([Exclusive<PooledSmtp>()]);
        Assert.True(gate.TryAcquire([Shared<PooledSmtp>()], out _));
        Assert.False(gate.TryAcquire([Exclusive<PooledSmtp>()], out _));
    }

    [Fact]
    public void A_multi_use_set_is_granted_all_or_nothing()
    {
        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Exclusive<ExclusiveDb>()], out _));

        // Catalog is free, Db is not: nothing may be taken, so Catalog stays free for an exclusive use.
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>(), Shared<ExclusiveDb>()], out var refusedBy));
        Assert.Equal(typeof(ExclusiveDb), refusedBy);
        Assert.True(gate.TryAcquire([Exclusive<SharedCatalog>()], out _));
    }

    [Fact]
    public void Duplicate_types_in_one_set_reduce_with_exclusive_winning()
    {
        var reduced = ContentionGate.Reduce([Shared<SharedCatalog>(), Exclusive<SharedCatalog>(), Shared<SharedCatalog>()]);
        var use = Assert.Single(reduced);
        Assert.Equal(LockMode.Exclusive, use.Mode);

        var gate = new ContentionGate();
        Assert.True(gate.TryAcquire([Shared<SharedCatalog>(), Exclusive<SharedCatalog>()], out _));
        Assert.False(gate.TryAcquire([Shared<SharedCatalog>()], out _)); // held exclusively, not shared
    }

    [Fact]
    public void A_type_without_exactly_one_kind_attribute_is_rejected()
    {
        var gate = new ContentionGate();

        var none = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<NoKind>()], out _));
        Assert.Contains(nameof(NoKind), none.Message, StringComparison.Ordinal);

        var two = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<TwoKinds>()], out _));
        Assert.Contains(nameof(TwoKinds), two.Message, StringComparison.Ordinal);

        var zero = Assert.Throws<InvalidOperationException>(() => gate.TryAcquire([Shared<ZeroCapacity>()], out _));
        Assert.Contains("at least 1", zero.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Releasing_what_was_never_acquired_is_an_error()
    {
        var gate = new ContentionGate();
        Assert.Throws<InvalidOperationException>(() => gate.Release([Shared<SharedCatalog>()]));
    }
}
```

Append to `test/Raun.Test/ContendedResourceTokens.cs` (the analyzer rule RAUN015 does not exist yet; these compile today and stay as runtime-check fixtures):

```csharp
/// <summary>Invalid on purpose: no kind attribute.</summary>
public sealed class NoKind : IContendedResource;

/// <summary>Invalid on purpose: two kinds.</summary>
[ExclusiveResource]
[SharedResource]
public sealed class TwoKinds : IContendedResource;

/// <summary>Invalid on purpose: a pool of nothing.</summary>
[PooledResource(0)]
public sealed class ZeroCapacity : IContendedResource;
```

These three types are runtime fixtures only. Once RAUN015 ships (Task 9) they would be compile errors in a project the analyzer runs on; `test/Raun.Test` references only the `Raun` project (no generator, no analyzer), so they stay legal there.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -15`
Expected: build error — `ContentionGate` does not exist.

- [ ] **Step 3: Write the gate**

`src/Raun/Scheduling/ContentionGate.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Raun.Scheduling;

/// <summary>
/// Admission control across scenarios (concurrent-scenarios design). A scenario's whole use set is
/// granted at once or not at all, so a scenario never holds one resource while waiting for another:
/// no hold-and-wait, no deadlock. The gate never waits — the run loop retries a refused scenario
/// whenever a running one finishes. Kinds are read from the token type's attributes once and cached.
/// </summary>
public sealed class ContentionGate
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, Slots> _slots = [];
    private readonly Dictionary<Type, int> _capacities = [];

    /// <summary>Holders of one resource: shared holders, or one exclusive holder.</summary>
    private sealed class Slots
    {
        public int Holders { get; set; }

        public bool Exclusive { get; set; }
    }

    /// <summary>
    /// Tries to take every use in <paramref name="uses"/>. Returns false, taking nothing, when any
    /// of them is refused; <paramref name="refusedBy"/> then names the first refusing resource.
    /// </summary>
    /// <exception cref="InvalidOperationException">A resource type does not carry exactly one kind
    /// attribute, or its pool capacity is below 1.</exception>
    public bool TryAcquire(IReadOnlyList<ContendedResourceUse> uses, out Type? refusedBy)
    {
        ArgumentNullException.ThrowIfNull(uses);
        var reduced = Reduce(uses);

        lock (_lock)
        {
            foreach (var use in reduced)
            {
                var capacity = CapacityOf(use.Resource);
                var slots = SlotsOf(use.Resource);
                if (!Admits(slots, capacity, use.Mode))
                {
                    refusedBy = use.Resource;
                    return false;
                }
            }

            foreach (var use in reduced)
            {
                var slots = _slots[use.Resource];
                if (use.Mode == LockMode.Exclusive)
                {
                    slots.Exclusive = true;
                }
                else
                {
                    slots.Holders++;
                }
            }

            refusedBy = null;
            return true;
        }
    }

    /// <summary>Gives back every use in <paramref name="uses"/>, which must have been acquired.</summary>
    public void Release(IReadOnlyList<ContendedResourceUse> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);

        lock (_lock)
        {
            foreach (var use in Reduce(uses))
            {
                if (!_slots.TryGetValue(use.Resource, out var slots)
                    || (use.Mode == LockMode.Exclusive ? !slots.Exclusive : slots.Holders == 0))
                {
                    throw new InvalidOperationException(
                        $"'{use.Resource.Name}' was released with mode {use.Mode} but was not held that way.");
                }

                if (use.Mode == LockMode.Exclusive)
                {
                    slots.Exclusive = false;
                }
                else
                {
                    slots.Holders--;
                }
            }
        }
    }

    /// <summary>One use per type; <see cref="LockMode.Exclusive"/> wins over <see cref="LockMode.Shared"/>.</summary>
    internal static List<ContendedResourceUse> Reduce(IReadOnlyList<ContendedResourceUse> uses)
    {
        var byType = new Dictionary<Type, LockMode>();
        foreach (var use in uses)
        {
            byType[use.Resource] = byType.TryGetValue(use.Resource, out var existing) && existing == LockMode.Exclusive
                ? LockMode.Exclusive
                : use.Mode;
        }

        var reduced = new List<ContendedResourceUse>(byType.Count);
        foreach (var pair in byType)
        {
            reduced.Add(new ContendedResourceUse(pair.Key, pair.Value));
        }

        return reduced;
    }

    // capacity 0 = unbounded ([SharedResource]); 1 = [ExclusiveResource]; N = [PooledResource(N)].
    private static bool Admits(Slots slots, int capacity, LockMode mode)
    {
        if (slots.Exclusive)
        {
            return false;
        }

        if (mode == LockMode.Exclusive)
        {
            return slots.Holders == 0;
        }

        return capacity == 0 || slots.Holders < capacity;
    }

    private Slots SlotsOf(Type resource)
    {
        if (!_slots.TryGetValue(resource, out var slots))
        {
            slots = new Slots();
            _slots[resource] = slots;
        }

        return slots;
    }

    private int CapacityOf(Type resource)
    {
        if (_capacities.TryGetValue(resource, out var known))
        {
            return known;
        }

        var exclusive = resource.IsDefined(typeof(ExclusiveResourceAttribute), inherit: false);
        var shared = resource.IsDefined(typeof(SharedResourceAttribute), inherit: false);
        var pooled = resource.GetCustomAttribute<PooledResourceAttribute>(inherit: false);
        var kinds = (exclusive ? 1 : 0) + (shared ? 1 : 0) + (pooled is null ? 0 : 1);
        if (kinds != 1)
        {
            throw new InvalidOperationException(
                $"'{resource.Name}' must carry exactly one of [ExclusiveResource], [SharedResource], [PooledResource]; it carries {kinds}.");
        }

        if (pooled is { Capacity: < 1 })
        {
            throw new InvalidOperationException(
                $"'{resource.Name}' declares [PooledResource({pooled.Capacity})]; the capacity must be at least 1.");
        }

        var capacity = exclusive ? 1 : shared ? 0 : pooled!.Capacity;
        _capacities[resource] = capacity;
        return capacity;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.
If CA1859 complains about `IReadOnlyList` parameters, keep the public signatures (they are the contract) and adjust only private locals.

- [ ] **Step 5: Commit**

```bash
jj commit -m "feat(scheduler): ContentionGate admits a scenario's use set all-or-nothing" -m "Synchronous, never waits: an exclusive resource has one slot, a shared resource any number for shared uses, a pooled resource its capacity, and an exclusive use takes every slot. Kinds are read from the token type's attributes once and cached; a type without exactly one kind attribute is rejected. Not wired into the run loop yet."
```

---

### Task 3: Run events carry order and wait data; the bus serializes publishers

**Files:**
- Modify: `src/Raun/Reporting/RunEvent.cs`
- Modify: `src/Raun/Reporting/RunEventBus.cs`
- Modify: `test/Raun.Test/Reporting/RunEventBusTests.cs`

**Interfaces:**
- Produces: `RunStarted(int ScenarioCount, IReadOnlyList<ScenarioDefinition>? Scenarios = null)`; `ScenarioStarted(ScenarioDefinition Definition, TimeSpan Waited = default, Type? WaitedFor = null)`. `RunEventBus.PublishAsync` delivers one event at a time, in call order, even when called concurrently.

- [ ] **Step 1: Write the failing bus test**

Add to `test/Raun.Test/Reporting/RunEventBusTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Concurrent_publishers_are_delivered_one_at_a_time_and_a_throwing_sink_stays_isolated()
    {
        var sink = new NonReentrantSink();
        var bad = new ThrowingSink();
        var bus = new RunEventBus([sink, bad]);

        var publishers = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    await bus.PublishAsync(new RunStarted(1));
                }
            }))
            .ToArray();
        await Task.WhenAll(publishers);

        Assert.Equal(32 * 20, sink.Delivered);
        Assert.Equal(0, sink.Overlaps);
        Assert.Single(bus.Failures);
    }

    [Fact]
    public void ScenarioStarted_defaults_to_no_wait_and_RunStarted_to_no_ordering()
    {
        var started = new ScenarioStarted(new Raun.Model.ScenarioDefinition
        {
            ScenarioId = "s", DisplayName = "s", MethodName = "N.s", Nodes = [],
        });
        Assert.Equal(TimeSpan.Zero, started.Waited);
        Assert.Null(started.WaitedFor);

        var run = new RunStarted(3);
        Assert.Equal(3, run.ScenarioCount);
        Assert.Null(run.Scenarios);
    }

    /// <summary>Counts deliveries and any delivery that begins before the previous one ended.</summary>
    private sealed class NonReentrantSink : IRunEventSink
    {
        private int _inside;

        public int Delivered { get; private set; }

        public int Overlaps { get; private set; }

        public async ValueTask PublishAsync(RunEvent evt)
        {
            if (Interlocked.Increment(ref _inside) != 1)
            {
                Overlaps++;
            }

            await Task.Yield(); // give a concurrent publisher every chance to overlap
            Delivered++;
            Interlocked.Decrement(ref _inside);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -15`
Expected: build error on `started.Waited` / `run.Scenarios` (members do not exist). After adding the records in Step 3 but before Step 4, the concurrency test fails with `Overlaps` > 0 or a wrong `Failures` count.

- [ ] **Step 3: Extend the records**

In `src/Raun/Reporting/RunEvent.cs` replace the `RunStarted` and `ScenarioStarted` records:

```csharp
/// <summary>Raised once at the start of a run, before any scenario. <paramref name="Scenarios"/> is
/// the run's canonical order (preflight first when present, then the selected scenarios in
/// registration order) so a sink can lay scenarios out deterministically whatever order admission
/// launches them in; null when the publisher has no ordering to offer.</summary>
public sealed record RunStarted(int ScenarioCount, IReadOnlyList<ScenarioDefinition>? Scenarios = null) : RunEvent;

/// <summary>Raised when a scenario begins; carries the definition so a session-scoped sink can
/// attribute every following step to its scenario. <paramref name="Waited"/> is how long admission
/// held the scenario back behind a contended resource (<paramref name="WaitedFor"/> names the first
/// refusing one); zero and null when it was admitted the moment a slot was free.</summary>
public sealed record ScenarioStarted(ScenarioDefinition Definition, TimeSpan Waited = default, Type? WaitedFor = null) : RunEvent;
```

- [ ] **Step 4: Serialize the bus**

Replace `src/Raun/Reporting/RunEventBus.cs` with:

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
    private readonly object _turns = new();
    private Task _tail = Task.CompletedTask;

    public RunEventBus(IReadOnlyList<IRunEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;
        _firstError = new Exception?[sinks.Count];
    }

    /// <summary>The first error each failed sink raised, in sink order; empty when all sinks held.</summary>
    public IReadOnlyList<Exception> Failures => _failures;

    // THREADING: scenarios run concurrently, so PublishAsync is called from several async flows at
    // once. Publications take turns in call order: each waits for the previous one to finish before
    // delivering, so every sink still sees one event at a time and the accumulators behind them
    // (HtmlReportModelBuilder, _failures) need no locking. Within one scenario the scheduler raises
    // callbacks serially, so a scenario's own events stay in order; different scenarios interleave.
    public async ValueTask PublishAsync(RunEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_turns)
        {
            previous = _tail;
            _tail = turn.Task;
        }

        await previous.ConfigureAwait(false); // never faults: every turn completes in the finally below
        try
        {
            await DeliverAsync(evt).ConfigureAwait(false);
        }
        finally
        {
            turn.SetResult();
        }
    }

    private async Task DeliverAsync(RunEvent evt)
    {
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

- [ ] **Step 5: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`. (`RunStarted(int)` call sites in `RaunRunLoop` and the MTP tests keep compiling: the list is optional.)

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(reporting): run events carry order and wait data; the bus takes turns" -m "RunStarted may carry the run's canonical scenario order and ScenarioStarted how long admission held a scenario back. RunEventBus serializes concurrent publishers with a turn queue instead of relying on sequential scenarios, so sinks keep seeing one event at a time once scenarios overlap."
```

---

### Task 4: The launcher — scenarios run up to a degree

**Files:**
- Modify: `src/Raun.Mtp/RaunRunLoop.cs` (ctor, `RunAsync`, new `LaunchAsync`, `RunOneAsync` signature)
- Modify: `test/Raun.Mtp.Test/RunLoopTests.cs`
- Create: `test/Raun.Mtp.Test/ContendedResourceTokens.cs` (used from Task 5 on; created here so the file exists)

**Interfaces:**
- Consumes: `RunStarted.Scenarios` (Task 3).
- Produces: `RaunRunLoop(Func<IEnumerable<ScenarioDefinition>> scenarioSource, RunScenario? runScenario = null, bool simulateTime = false, IServiceProvider? services = null, Func<ScenarioContext, Task>? preflight = null, int maxParallelScenarios = 0)`. `0` means `Environment.ProcessorCount`; negative throws `ArgumentOutOfRangeException`. `RunOneAsync(definition, bus, uids, runContext, runId, TimeSpan waited, Type? waitedFor, CancellationToken)` (private).

- [ ] **Step 1: Create the MTP-side token types**

`test/Raun.Mtp.Test/ContendedResourceTokens.cs`:

```csharp
namespace Raun.Mtp.Test;

// Public top-level on purpose: a private nested token would trip CA1812, a public nested one CA1034.

[ExclusiveResource]
public sealed class ExclusiveDb : IContendedResource;

[SharedResource]
public sealed class SharedCatalog : IContendedResource;

[PooledResource(2)]
public sealed class PooledSmtp : IContendedResource;
```

- [ ] **Step 2: Write the failing tests**

In `test/Raun.Mtp.Test/RunLoopTests.cs`:

(a) Replace the test `Distinct_scenarios_run_sequentially_for_v1` with:

```csharp
    [Fact]
    public async Task Degree_one_runs_scenarios_sequentially()
    {
        // Records the max observed concurrency across scenario runs; degree 1 => never exceeds 1.
        var current = 0;
        var max = 0;
        var sync = new object();

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
            }

            await Task.Delay(20);

            lock (sync)
            {
                current--;
            }

            return null;
        }

        var a = Definition("a", "A", Node(0, "x", "x", invoke: Body));
        var b = Definition("b", "B", Node(0, "y", "y", invoke: Body));
        var c = Definition("c", "C", Node(0, "z", "z", invoke: Body));

        var loop = new RaunRunLoop(() => [a, b, c], maxParallelScenarios: 1);
        await loop.RunAsync(uids: null, new RecordingSink(), CancellationToken.None);

        Assert.Equal(1, max);
    }

    [Fact]
    public async Task Scenarios_run_concurrently_up_to_the_degree()
    {
        // Six scenarios, degree 3. Every body blocks until three are in flight, so "== 3" is proven
        // by rendezvous rather than sampled; the launcher itself guarantees "<= 3".
        var current = 0;
        var max = 0;
        var sync = new object();
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
                if (current == 3)
                {
                    third.TrySetResult();
                }
            }

            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (sync)
            {
                current--;
            }

            return null;
        }

        var definitions = Enumerable.Range(0, 6)
            .Select(i => Definition($"s{i}", $"S{i}", Node(0, "x", "x", invoke: Body)))
            .ToArray();

        var sink = new RecordingSink();
        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Equal(3, max);
        Assert.Equal(6, sink.PassedUids.Count());
    }

    [Fact]
    public void A_negative_degree_is_rejected_and_zero_means_the_processor_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaunRunLoop(() => [], maxParallelScenarios: -1));
        Assert.Equal(Environment.ProcessorCount, new RaunRunLoop(() => []).MaxParallelScenarios);
        Assert.Equal(2, new RaunRunLoop(() => [], maxParallelScenarios: 2).MaxParallelScenarios);
    }

    [Fact]
    public async Task RunStarted_carries_the_registration_order_with_preflight_first()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));
        var sink = new RecordingSink();

        await new RaunRunLoop(() => [a, b], preflight: _ => Task.CompletedTask, maxParallelScenarios: 2)
            .RunAsync(uids: null, sink, CancellationToken.None);

        var started = Assert.Single(sink.Events.OfType<RunStarted>());
        Assert.Equal(2, started.ScenarioCount);
        Assert.NotNull(started.Scenarios);
        Assert.Equal([Preflight.ScenarioId, "a", "b"], started.Scenarios.Select(d => d.ScenarioId)); // "raun", then registration order
    }

    [Fact]
    public async Task A_failed_preflight_skips_every_scenario_at_any_degree()
    {
        var a = Definition("a", "A", Node(0, "x", "x"));
        var b = Definition("b", "B", Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));
        var sink = new RecordingSink();

        await new RaunRunLoop(
                () => [a, b, c],
                preflight: _ => throw new InvalidOperationException("no container runtime"),
                maxParallelScenarios: 3)
            .RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Empty(sink.PassedUids);
        foreach (var uid in new[] { Uid("a", "x"), Uid("b", "y"), Uid("c", "z") })
        {
            Assert.Contains(uid, sink.SkippedUids);
        }

        Assert.Single(sink.Events.OfType<RunFinished>());
    }
```

(b) The existing `Cancellation_mid_run_stops_launching_further_scenarios` relies on sequential launching. Change its loop construction to `new RaunRunLoop(() => [first, second], maxParallelScenarios: 1)` and add this degree-3 sibling:

```csharp
    [Fact]
    public async Task Cancellation_mid_run_stops_launching_scenarios_beyond_the_first_pass()
    {
        // Degree 3, five scenarios: the first pass launches up to three; the first body cancels the
        // platform token, so no later slot may be refilled — scenarios four and five never start.
        using var cts = new CancellationTokenSource();
        var launched = new List<string>();
        var sync = new object();

        ScenarioDefinition Def(string id, bool cancels) => Definition(id, id,
            Node(0, "a", "a", invoke: (_, _) =>
            {
                lock (sync) { launched.Add(id); }
                if (cancels) { cts.Cancel(); }
                return Task.FromResult<object?>(null);
            }));

        var definitions = new[] { Def("one", cancels: true), Def("two", false), Def("three", false), Def("four", false), Def("five", false) };
        var sink = new RecordingSink();

        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(uids: null, sink, cts.Token);

        var startedIds = sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId).ToList();
        Assert.Contains("one", startedIds);
        Assert.DoesNotContain("four", startedIds);
        Assert.DoesNotContain("five", startedIds);
        Assert.Single(sink.Events.OfType<RunFinished>());
    }
```

(c) Add a degree-3 trace-isolation test next to `Each_scenario_is_its_own_trace_linked_to_the_run_span_with_steps_nested_under_it`:

```csharp
    [Fact]
    public async Task Concurrent_scenarios_keep_their_own_traces_and_step_parents()
    {
        var ids = Enumerable.Range(0, 3).Select(i => $"ctrace-{i}-" + Guid.NewGuid().ToString("N")[..6]).ToArray();
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            if (Interlocked.Increment(ref arrived) == 3)
            {
                rendezvous.TrySetResult();
            }

            await rendezvous.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }

        var definitions = ids.Select(id => Definition(id, "traced " + id, Node(0, "x", "x", invoke: Body), Node(1, "y", "y", dependsOn: [0]))).ToArray();
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => definitions, maxParallelScenarios: 3)
            .RunAsync(uids: null, new RecordingSink(), CancellationToken.None);

        foreach (var id in ids)
        {
            var spans = capture.ForScenario(id);
            var scenario = Assert.Single(spans, s => s.DisplayName == "traced " + id);
            Assert.Null(scenario.Parent);
            var steps = spans.Where(s => s != scenario).ToList();
            Assert.Equal(2, steps.Count);
            Assert.All(steps, step =>
            {
                Assert.Equal(scenario.TraceId, step.TraceId);
                Assert.Equal(scenario.SpanId, step.ParentSpanId);
            });
        }

        // Three different traces, not one.
        Assert.Equal(3, ids.Select(id => capture.ForScenario(id).First().TraceId).Distinct().Count());
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build errors — `maxParallelScenarios` is not a parameter, `MaxParallelScenarios` does not exist.

- [ ] **Step 4: Write the launcher**

In `src/Raun.Mtp/RaunRunLoop.cs`:

(a) Add a field and a property after `private readonly Func<ScenarioContext, Task>? preflight;`:

```csharp
    private readonly int maxParallelScenarios;

    /// <summary>How many scenarios may run at once. Steps inside a scenario stay unbounded.</summary>
    public int MaxParallelScenarios => maxParallelScenarios;
```

(b) Extend the ctor: add the parameter `int maxParallelScenarios = 0` after `preflight`, document it, and set the field:

```csharp
    /// <param name="maxParallelScenarios">
    /// How many scenarios may run at once. <c>0</c> (the default) means <see cref="Environment.ProcessorCount"/>;
    /// <c>1</c> runs scenarios one after another. Steps inside a scenario stay unbounded, so the
    /// number of concurrently running steps is at most this times the widest scenario.
    /// </param>
```

```csharp
        ArgumentOutOfRangeException.ThrowIfNegative(maxParallelScenarios);
        this.maxParallelScenarios = maxParallelScenarios == 0 ? Environment.ProcessorCount : maxParallelScenarios;
```

Update the class `<remarks>`: replace the sentence "Cross-scenario execution is sequential for v1 (the scheduler already provides bounded parallelism <em>within</em> a scenario)." with "Scenarios run concurrently up to <see cref=\"MaxParallelScenarios\"/>; the scheduler additionally parallelizes steps <em>within</em> a scenario."

(c) In `RunAsync`, replace everything from `// Run-level setup, before any scenario.` down to (but not including) `await bus.PublishAsync(new RunFinished())` with:

```csharp
        // Run-level setup, before any scenario. It runs even when the filter selected nothing: a
        // filtered run of one step still needs whatever preflight brings up.
        var preflightFailed = false;
        if (preflightDefinition is not null)
        {
            var results = await RunOneAsync(preflightDefinition, bus, uids: null, runContext, runId, waited: TimeSpan.Zero, waitedFor: null, cancellationToken)
                .ConfigureAwait(false);
            preflightFailed = results.Any(r => r.Status is StepStatus.Failed or StepStatus.Skipped);
        }

        if (preflightFailed)
        {
            // Attribute the failure to a row rather than to an exit code: every step reports skipped
            // naming preflight, and the run still completes so the report is whole.
            foreach (var definition in selected)
            {
                await SkipScenarioAsync(definition, bus, runContext, runId).ConfigureAwait(false);
            }
        }
        else
        {
            await LaunchAsync(selected, bus, uids, runContext, runId, cancellationToken).ConfigureAwait(false);
        }
```

and change the `RunStarted` publication near the top of `RunAsync` to build the canonical order:

```csharp
        var selected = SelectScenarios(scenarioSource(), uids);
        var preflightDefinition = preflight is null ? null : Preflight.Definition(preflight);
        IReadOnlyList<ScenarioDefinition> order = preflightDefinition is null ? selected : [preflightDefinition, .. selected];
        await bus.PublishAsync(new RunStarted(selected.Count, order)).ConfigureAwait(false);
```

(d) Add the launcher after `RunAsync`:

```csharp
    /// <summary>
    /// Runs the selected scenarios with at most <see cref="MaxParallelScenarios"/> in flight.
    /// Scenarios launch in registration order as slots free up. Cancellation keeps the sequential
    /// loop's contract: the first scenario always launches (an up-front cancellation still reports
    /// its steps as skipped through the scheduler); after any launch, an observed cancellation
    /// stops further launches; whatever is running drains through its own linked token.
    /// </summary>
    private async ValueTask LaunchAsync(
        IReadOnlyList<ScenarioDefinition> selected,
        IRunEventSink bus,
        ISet<string>? uids,
        ActivityContext? runContext,
        string runId,
        CancellationToken cancellationToken)
    {
        var pending = new List<ScenarioDefinition>(selected);
        var running = new Dictionary<Task<IReadOnlyList<StepResult>>, ScenarioDefinition>();
        var started = false;

        while (pending.Count > 0 || running.Count > 0)
        {
            var i = 0;
            while (i < pending.Count && running.Count < maxParallelScenarios)
            {
                if (started && cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var definition = pending[i];
                pending.RemoveAt(i);
                var run = RunOneAsync(definition, bus, uids, runContext, runId, waited: TimeSpan.Zero, waitedFor: null, cancellationToken).AsTask();
                running[run] = definition;
                started = true;
            }

            if (running.Count == 0)
            {
                break; // cancelled with nothing left in flight
            }

            var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            running.Remove(finished);
            await finished.ConfigureAwait(false); // surfaces a loop bug; step failures never throw here
        }
    }
```

(e) Give `RunOneAsync` two new parameters before the token — `TimeSpan waited, Type? waitedFor` — and pass them on to the event (`new ScenarioStarted(definition, waited, waitedFor)`) and to `StartScenarioActivity(definition, runContext, runId, waited, waitedFor)`. Extend `StartScenarioActivity` with the same two parameters; leave its body unchanged for now (Task 5 adds the tags).

- [ ] **Step 5: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed, including the untouched cancellation and preflight tests.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`. If CA1849/CA2007 or IDE rules fire on the launcher, fix as they say; do not suppress.
Run: `dotnet test Raun.slnx 2>&1 | tail -5` — expected: everything green (samples included).

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(mtp): scenarios run concurrently up to a degree" -m "RaunRunLoop replaces its sequential foreach with a launcher: scenarios start in registration order as slots free up, bounded by maxParallelScenarios (0 = processor count, 1 = the old behaviour). Cancellation invariants are unchanged: the first scenario always launches, an observed cancellation stops further launches, running scenarios drain, RunFinished always publishes. RunStarted now carries the canonical order, preflight first."
```

---

### Task 5: Admission through the gate, wait accounting, span attributes

**Files:**
- Modify: `src/Raun.Mtp/RaunRunLoop.cs` (`LaunchAsync`, `StartScenarioActivity`)
- Modify: `src/Raun/Tracing/RaunTelemetry.cs`
- Modify: `test/Raun.Mtp.Test/RunLoopTests.cs`

**Interfaces:**
- Consumes: `ContentionGate` (Task 2), `ScenarioStarted.Waited/WaitedFor` (Task 3).
- Produces: `RaunTelemetry.Attributes.ScenarioUses = "raun.scenario.uses"`, `ScenarioWaitedMs = "raun.scenario.waited_ms"`, `ScenarioWaitedFor = "raun.scenario.waited_for"`.

- [ ] **Step 1: Write the failing tests**

Add to `RunLoopTests.cs` a second `Definition` helper next to the existing one:

```csharp
    private static ScenarioDefinition Definition(string id, string display, ContendedResourceUse[] uses, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id,
        DisplayName = display,
        MethodName = $"Ns.{id}",
        Nodes = nodes,
        Uses = uses,
    };

    private static ContendedResourceUse Shared<T>() where T : IContendedResource => new(typeof(T), LockMode.Shared);
    private static ContendedResourceUse Exclusive<T>() where T : IContendedResource => new(typeof(T), LockMode.Exclusive);
```

and these tests:

```csharp
    [Fact]
    public async Task Exclusive_users_of_one_resource_never_overlap_while_an_unrelated_scenario_does()
    {
        // A holds the db until C has started (so A and C overlap); B also wants the db and must wait
        // for A. Degree 3 leaves room for all three, so only the gate can hold B back.
        var cStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dbCurrent = 0;
        var dbMax = 0;
        var sync = new object();

        async Task<object?> DbBody(IStepInputs _, ScenarioContext __)
        {
            lock (sync) { dbCurrent++; dbMax = Math.Max(dbMax, dbCurrent); }
            await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (sync) { dbCurrent--; }
            return null;
        }

        var a = Definition("a", "A", [Exclusive<ExclusiveDb>()], Node(0, "x", "x", invoke: DbBody));
        var b = Definition("b", "B", [Exclusive<ExclusiveDb>()], Node(0, "y", "y", invoke: DbBody));
        var c = Definition("c", "C", Node(0, "z", "z", invoke: (_, _) => { cStarted.TrySetResult(); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [a, b, c], maxParallelScenarios: 3).RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Equal(1, dbMax);
        Assert.Equal(3, sink.PassedUids.Count());
        Assert.Equal(["a", "c", "b"], sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
    }

    [Fact]
    public async Task Shared_users_overlap_and_an_exclusive_user_waits_for_all_of_them()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedInFlight = 0;
        var sharedFinished = 0;
        var seenBySweeper = -1;

        async Task<object?> SharedBody(IStepInputs _, ScenarioContext __)
        {
            if (Interlocked.Increment(ref sharedInFlight) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(TimeSpan.FromSeconds(10)); // proves the two shared users overlap
            Interlocked.Increment(ref sharedFinished);
            return null;
        }

        var s1 = Definition("s1", "S1", [Shared<SharedCatalog>()], Node(0, "x", "x", invoke: SharedBody));
        var s2 = Definition("s2", "S2", [Shared<SharedCatalog>()], Node(0, "y", "y", invoke: SharedBody));
        var sweeper = Definition("sweep", "Sweep", [Exclusive<SharedCatalog>()],
            Node(0, "z", "z", invoke: (_, _) => { seenBySweeper = Volatile.Read(ref sharedFinished); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [s1, s2, sweeper], maxParallelScenarios: 3).RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Equal(2, seenBySweeper);
        var sweepStarted = Assert.Single(sink.Events.OfType<ScenarioStarted>(), e => e.Definition.ScenarioId == "sweep");
        Assert.True(sweepStarted.Waited > TimeSpan.Zero);
        Assert.Equal(typeof(SharedCatalog), sweepStarted.WaitedFor);
    }

    [Fact]
    public async Task Pooled_capacity_caps_concurrent_holders_below_the_degree()
    {
        var current = 0;
        var max = 0;
        var sync = new object();
        var pair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<object?> Body(IStepInputs _, ScenarioContext __)
        {
            lock (sync)
            {
                current++;
                max = Math.Max(max, current);
                if (current == 2) { pair.TrySetResult(); }
            }

            await pair.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (sync) { current--; }
            return null;
        }

        var definitions = Enumerable.Range(0, 4)
            .Select(i => Definition($"m{i}", $"M{i}", [Shared<PooledSmtp>()], Node(0, "x", "x", invoke: Body)))
            .ToArray();

        var sink = new RecordingSink();
        await new RaunRunLoop(() => definitions, maxParallelScenarios: 4).RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Equal(2, max);
        Assert.Equal(4, sink.PassedUids.Count());
    }

    [Fact]
    public async Task Admission_prefers_registration_order_among_admissible_scenarios()
    {
        // Degree 2. A holds the db until D starts; B wants the db; C and D are free. Expected launch
        // order: A, C (pass one), D (when C finishes; B is still refused), B (when A releases).
        var dStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var a = Definition("a", "A", [Exclusive<ExclusiveDb>()], Node(0, "x", "x", invoke: async (_, _) =>
        {
            await dStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return null;
        }));
        var b = Definition("b", "B", [Exclusive<ExclusiveDb>()], Node(0, "y", "y"));
        var c = Definition("c", "C", Node(0, "z", "z"));
        var d = Definition("d", "D", Node(0, "w", "w", invoke: (_, _) => { dStarted.TrySetResult(); return Task.FromResult<object?>(null); }));

        var sink = new RecordingSink();
        await new RaunRunLoop(() => [a, b, c, d], maxParallelScenarios: 2).RunAsync(uids: null, sink, CancellationToken.None);

        Assert.Equal(["a", "c", "d", "b"], sink.Events.OfType<ScenarioStarted>().Select(e => e.Definition.ScenarioId));
    }

    [Fact]
    public async Task Waiting_scenarios_report_their_wait_on_the_span_and_uses_are_tagged()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderId = "hold-" + Guid.NewGuid().ToString("N")[..8];
        var waiterId = "wait-" + Guid.NewGuid().ToString("N")[..8];

        var holder = Definition(holderId, "holder", [Exclusive<ExclusiveDb>(), Shared<SharedCatalog>()],
            Node(0, "x", "x", invoke: async (_, _) => { await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); return null; }));
        var waiter = Definition(waiterId, "waiter", [Shared<ExclusiveDb>()], Node(0, "y", "y"));
        var opener = Definition("open", "opener", Node(0, "z", "z", invoke: (_, _) => { release.TrySetResult(); return Task.FromResult<object?>(null); }));
        using var capture = new SpanCapture();

        await new RaunRunLoop(() => [holder, waiter, opener], maxParallelScenarios: 3)
            .RunAsync(uids: null, new RecordingSink(), CancellationToken.None);

        var holderSpan = Assert.Single(capture.ForScenario(holderId), s => s.DisplayName == "holder");
        Assert.Equal("ExclusiveDb:Exclusive,SharedCatalog:Shared", holderSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioUses));
        Assert.Null(holderSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedMs));

        var waiterSpan = Assert.Single(capture.ForScenario(waiterId), s => s.DisplayName == "waiter");
        Assert.Equal("ExclusiveDb:Shared", waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioUses));
        Assert.NotNull(waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedMs));
        Assert.Equal("ExclusiveDb", waiterSpan.GetTagItem(RaunTelemetry.Attributes.ScenarioWaitedFor));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -20`
Expected: build errors for the three `RaunTelemetry.Attributes` names; after adding them (Step 3), the overlap tests fail (`dbMax` is 2, `max` is 4, order differs, `Waited` is zero).

- [ ] **Step 3: Add the attributes**

In `src/Raun/Tracing/RaunTelemetry.cs`, inside `Attributes` after `StepOperation`:

```csharp
        /// <summary>The scenario's contended-resource uses, <c>Type:Mode</c> comma-separated; absent when none.</summary>
        public const string ScenarioUses = "raun.scenario.uses";

        /// <summary>Milliseconds admission held the scenario back behind a contended resource; absent when zero.</summary>
        public const string ScenarioWaitedMs = "raun.scenario.waited_ms";

        /// <summary>Name of the resource that first refused the scenario; set with <see cref="ScenarioWaitedMs"/>.</summary>
        public const string ScenarioWaitedFor = "raun.scenario.waited_for";
```

- [ ] **Step 4: Wire the gate and the tags**

In `src/Raun.Mtp/RaunRunLoop.cs`:

(a) Replace the whole `LaunchAsync` method with:

```csharp
    /// <summary>
    /// Runs the selected scenarios with at most <see cref="MaxParallelScenarios"/> in flight, each
    /// admitted only when its contended-resource uses are compatible with everything running.
    /// Scenarios launch in registration order as slots free up; a refused scenario is retried each
    /// time a running one finishes, and is admitted at the latest when everything else has drained.
    /// Cancellation keeps the sequential loop's contract: the first scenario always launches (an
    /// up-front cancellation still reports its steps as skipped through the scheduler); after any
    /// launch, an observed cancellation stops further launches; whatever is running drains through
    /// its own linked token.
    /// </summary>
    private async ValueTask LaunchAsync(
        IReadOnlyList<ScenarioDefinition> selected,
        IRunEventSink bus,
        ISet<string>? uids,
        ActivityContext? runContext,
        string runId,
        CancellationToken cancellationToken)
    {
        var pending = new List<ScenarioDefinition>(selected);
        var running = new Dictionary<Task<IReadOnlyList<StepResult>>, ScenarioDefinition>();
        var waits = new Dictionary<ScenarioDefinition, Wait>();
        var gate = new ContentionGate();
        var started = false;

        while (pending.Count > 0 || running.Count > 0)
        {
            var i = 0;
            while (i < pending.Count && running.Count < maxParallelScenarios)
            {
                if (started && cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var definition = pending[i];
                if (!gate.TryAcquire(definition.Uses, out var refusedBy))
                {
                    // A slot was free and the gate said no: that is contention, worth reporting.
                    if (!waits.ContainsKey(definition))
                    {
                        waits[definition] = new Wait(Stopwatch.GetTimestamp(), refusedBy!);
                    }

                    i++;
                    continue;
                }

                pending.RemoveAt(i);
                var waited = TimeSpan.Zero;
                Type? waitedFor = null;
                if (waits.Remove(definition, out var wait))
                {
                    waited = Stopwatch.GetElapsedTime(wait.Since);
                    waitedFor = wait.By;
                }

                var run = RunOneAsync(definition, bus, uids, runContext, runId, waited, waitedFor, cancellationToken).AsTask();
                running[run] = definition;
                started = true;
            }

            if (running.Count == 0)
            {
                break; // cancelled with nothing left in flight; the gate is empty, so nothing else can be blocked
            }

            var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var done = running[finished];
            running.Remove(finished);
            gate.Release(done.Uses);
            await finished.ConfigureAwait(false); // surfaces a loop bug; step failures never throw here
        }
    }

    /// <summary>When a scenario was first refused by the gate, and by which resource.</summary>
    private readonly record struct Wait(long Since, Type By);
```

Add `using Raun.Scheduling;` (already present) and confirm `using System.Diagnostics;` is at the top (it is, for `Activity`).

(b) In `StartScenarioActivity(ScenarioDefinition definition, ActivityContext? runContext, string runId, TimeSpan waited, Type? waitedFor)`, after the `code.line.number` block and before `return activity;`:

```csharp
        if (definition.Uses.Count > 0)
        {
            activity.SetTag(
                RaunTelemetry.Attributes.ScenarioUses,
                string.Join(",", definition.Uses.Select(u => $"{u.Resource.Name}:{u.Mode}")));
        }

        if (waited > TimeSpan.Zero)
        {
            // A refused scenario waited, even when the clock rounds it to 0 ms: report at least 1 so the tag exists.
            activity.SetTag(RaunTelemetry.Attributes.ScenarioWaitedMs, Math.Max(1L, (long)waited.TotalMilliseconds));
            activity.SetTag(RaunTelemetry.Attributes.ScenarioWaitedFor, waitedFor?.Name);
        }
```

- [ ] **Step 5: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.
Run the whole suite three times to shake out ordering flakes: `for i in 1 2 3; do dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -1; done` — expected: three passes.

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(mtp): admit scenarios through the ContentionGate and report waits" -m "The launcher asks the gate before launching; a refused scenario is retried when a running one finishes and admission prefers registration order among admissible scenarios. Time spent refused while a slot was free is reported on ScenarioStarted and as raun.scenario.waited_ms / waited_for on the scenario span, alongside raun.scenario.uses."
```

---

### Task 6: Configuration — code default, CLI override, Aspire pass-through

**Files:**
- Create: `src/Raun.Mtp/RunOptionsProvider.cs`
- Create: `src/Raun.Mtp/ScenarioParallelism.cs`
- Modify: `src/Raun.Mtp/RaunTestFramework.cs` (fields, ctor overload, `OnExecuteAsync`)
- Modify: `src/Raun.Mtp/RaunTestApplication.cs`
- Modify: `src/Raun.Aspire/AspireRunOptions.cs`, `src/Raun.Aspire/RaunAspire.cs`
- Create: `test/Raun.Mtp.Test/ScenarioParallelismTests.cs`

**Interfaces:**
- Produces: `RunOptionsProvider.MaxParallelScenariosOption = "max-parallel-scenarios"`; `ScenarioParallelism.TryParse(string[]? arguments, out int degree)`; `ScenarioParallelism.Resolve(IServiceProvider? services, int codeDefault)`; `RaunTestFramework(IServiceProvider services, bool simulateTime, IServiceProvider? userServices, Func<ScenarioContext, Task>? preflight, int maxParallelScenarios)`; `RaunTestApplication.RunAsync(string[] args, Action<ITestApplicationBuilder>? configure = null, bool simulateTime = false, IServiceProvider? services = null, Func<ScenarioContext, Task>? preflight = null, int maxParallelScenarios = 0)`; `AspireRunOptions.MaxParallelScenarios { get; set; }`.

- [ ] **Step 1: Write the failing tests**

`test/Raun.Mtp.Test/ScenarioParallelismTests.cs`:

```csharp
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>The degree of scenario parallelism: code sets the suite default, --max-parallel-scenarios overrides per run.</summary>
public class ScenarioParallelismTests
{
    [Fact]
    public void The_option_is_registered_and_takes_exactly_one_argument()
    {
        var provider = new RunOptionsProvider();
        var option = Assert.Single(provider.GetCommandLineOptions(), o => o.Name == "max-parallel-scenarios");
        Assert.Equal(ArgumentArity.ExactlyOne, option.Arity);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("1", true, 1)]
    [InlineData("8", true, 8)]
    [InlineData("-1", false, 0)]
    [InlineData("many", false, 0)]
    [InlineData("", false, 0)]
    public void Parses_a_non_negative_integer_only(string argument, bool valid, int expected)
    {
        Assert.Equal(valid, ScenarioParallelism.TryParse([argument], out var degree));
        if (valid)
        {
            Assert.Equal(expected, degree);
        }
    }

    [Fact]
    public async Task The_provider_rejects_what_the_parser_rejects()
    {
        var provider = new RunOptionsProvider();
        var option = provider.GetCommandLineOptions().Single(o => o.Name == "max-parallel-scenarios");

        Assert.True((await provider.ValidateOptionArgumentsAsync(option, ["4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["-4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["x"])).IsValid);
    }

    [Fact]
    public void Resolve_returns_the_code_default_without_services_or_without_the_option()
    {
        Assert.Equal(3, ScenarioParallelism.Resolve(services: null, codeDefault: 3));
        Assert.Equal(3, ScenarioParallelism.Resolve(new OptionsProvider(null), codeDefault: 3));
    }

    [Fact]
    public void Resolve_lets_the_command_line_override_the_code_default()
    {
        Assert.Equal(1, ScenarioParallelism.Resolve(new OptionsProvider("1"), codeDefault: 3));
        Assert.Equal(0, ScenarioParallelism.Resolve(new OptionsProvider("0"), codeDefault: 3));
    }

    /// <summary>Stands in for MTP's provider: answers ICommandLineOptions with one optional value for the degree.</summary>
    private sealed class OptionsProvider(string? degree) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandLineOptions) ? new Options(degree) : null;

        private sealed class Options(string? degree) : ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => optionName == "max-parallel-scenarios" && degree is not null;

            public bool TryGetOptionArgumentList(
                string optionName,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments)
            {
                arguments = IsOptionSet(optionName) ? [degree!] : null;
                return arguments is not null;
            }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build errors — `RunOptionsProvider`, `ScenarioParallelism` do not exist.

- [ ] **Step 3: Write the provider and resolver**

`src/Raun.Mtp/RunOptionsProvider.cs`:

```csharp
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Raun.Mtp;

/// <summary>
/// Registers Raun's run-shaping command-line options with Microsoft.Testing.Platform:
/// <c>--max-parallel-scenarios &lt;n&gt;</c> overrides the degree the suite set in code
/// (<c>0</c> = processor count, <c>1</c> = sequential).
/// </summary>
internal sealed class RunOptionsProvider : ICommandLineOptionsProvider
{
    internal const string MaxParallelScenariosOption = "max-parallel-scenarios";

    public string Uid => "raun.mtp.run";
    public string Version => "1.0.0";
    public string DisplayName => "Raun run options";
    public string Description => "Controls how Raun runs scenarios.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new CommandLineOption(MaxParallelScenariosOption,
            "Maximum scenarios running at once. 0 means the processor count; 1 runs scenarios one after another. Overrides the suite's code default.",
            ArgumentArity.ExactlyOne, isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
        => commandOption.Name == MaxParallelScenariosOption && !ScenarioParallelism.TryParse(arguments, out _)
            ? ValidationResult.InvalidTask($"'--{MaxParallelScenariosOption}' requires an integer of 0 or more.")
            : ValidationResult.ValidTask;

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
        => ValidationResult.ValidTask;
}
```

`src/Raun.Mtp/ScenarioParallelism.cs`:

```csharp
using System.Globalization;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Services;

namespace Raun.Mtp;

/// <summary>Resolves how many scenarios may run at once: the command line wins over the code default.</summary>
internal static class ScenarioParallelism
{
    /// <summary>Accepts exactly one argument that is a non-negative integer (no sign, no decimals).</summary>
    public static bool TryParse(string[]? arguments, out int degree)
    {
        degree = 0;
        return arguments is { Length: 1 }
            && int.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out degree);
    }

    /// <summary>
    /// The degree for this run: <c>--max-parallel-scenarios</c> when given and valid, otherwise
    /// <paramref name="codeDefault"/> (what <c>RaunTestApplication.RunAsync</c> was called with).
    /// A null <paramref name="services"/> is the unit-test path with no MTP host.
    /// </summary>
    public static int Resolve(IServiceProvider? services, int codeDefault)
    {
        if (services is null)
        {
            return codeDefault;
        }

        ICommandLineOptions options = services.GetCommandLineOptions();
        return options.TryGetOptionArgumentList(RunOptionsProvider.MaxParallelScenariosOption, out var arguments)
            && TryParse(arguments, out var degree)
            ? degree
            : codeDefault;
    }
}
```

- [ ] **Step 4: Thread the degree through the framework, the application, and Aspire**

`src/Raun.Mtp/RaunTestFramework.cs`:
- Add a field after `_simulateTime`: `private readonly int _maxParallelScenarios;`
- Add a ctor after the preflight one:

```csharp
    /// <summary>Production ctor including the suite's default degree of scenario parallelism
    /// (<c>0</c> = processor count, <c>1</c> = sequential); <c>--max-parallel-scenarios</c> overrides it per run.</summary>
    public RaunTestFramework(
        IServiceProvider services,
        bool simulateTime,
        IServiceProvider? userServices,
        Func<ScenarioContext, Task>? preflight,
        int maxParallelScenarios)
        : this(services, simulateTime, userServices, preflight) => _maxParallelScenarios = maxParallelScenarios;
```

- In `OnExecuteAsync`, construct the loop with the resolved degree:

```csharp
        var loop = new RaunRunLoop(
            EnumerateRegisteredScenarios,
            simulateTime: _simulateTime,
            services: _userServices,
            preflight: _preflight,
            maxParallelScenarios: ScenarioParallelism.Resolve(_services, _maxParallelScenarios));
```

`src/Raun.Mtp/RaunTestApplication.cs`: add the parameter `int maxParallelScenarios = 0` after `preflight`, with this doc:

```csharp
    /// <param name="maxParallelScenarios">
    /// The suite's default degree of scenario parallelism: how many scenarios may run at once.
    /// <c>0</c> (the default) means the processor count; <c>1</c> runs scenarios one after another.
    /// <c>--max-parallel-scenarios &lt;n&gt;</c> overrides it per run. Steps inside a scenario stay
    /// unbounded. Scenarios that contend for the same thing declare it with <c>[Uses&lt;T&gt;]</c>.
    /// </param>
```

register the provider next to the HTML one, and pass the degree to the framework:

```csharp
        builder.CommandLine.AddProvider(() => new HtmlReport.HtmlReportOptionsProvider());
        builder.CommandLine.AddProvider(() => new RunOptionsProvider());

        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(),
            (_, serviceProvider) => new RaunTestFramework(serviceProvider, simulateTime, services, preflight, maxParallelScenarios));
```

Update the `<see cref="RunAsync(...)"/>` in the class remarks to the six-parameter signature (`…, Func{ScenarioContext,Task}?, int`).

`src/Raun.Aspire/AspireRunOptions.cs`, after `StartupTimeout`:

```csharp
    /// <summary>
    /// How many scenarios may run at once against the application. <c>0</c> (the default) means the
    /// processor count; <c>1</c> runs them one after another. <c>--max-parallel-scenarios</c> overrides it.
    /// </summary>
    public int MaxParallelScenarios { get; set; }
```

`src/Raun.Aspire/RaunAspire.cs`, in the `RaunTestApplication.RunAsync` call add `maxParallelScenarios: options.MaxParallelScenarios` after `preflight:`.

- [ ] **Step 5: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --max-parallel-scenarios 1 2>&1 | tail -3` — expected: the usual `52 total / 51 passed / 1 skipped` summary (option accepted).
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --max-parallel-scenarios nope 2>&1 | tail -3` — expected: MTP reports the validation message and exits non-zero.

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(mtp): --max-parallel-scenarios and a code default for the degree" -m "RaunTestApplication.RunAsync and AspireRunOptions take the suite's default degree; a RunOptionsProvider registers --max-parallel-scenarios <n> (0 = processor count, 1 = sequential), validated as a non-negative integer, and the framework lets the command line override the code default per run."
```

---

### Task 7: HTML report — registration order, wall span, wait line

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`
- Modify: `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`
- Modify: `src/Raun.Mtp/HtmlReport/HtmlReportSink.cs`
- Modify: `src/Raun.Mtp/HtmlReport/report-template.html` (function `buildScenarioCard`, around line 1919)
- Modify: `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`, `test/Raun.Mtp.Test/HtmlReportSinkTests.cs`
- Snapshot: `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt`

**Interfaces:**
- Consumes: `RunStarted.Scenarios`, `ScenarioStarted.Waited/WaitedFor` (Task 3).
- Produces: `HtmlReportModelBuilder.OnRunStarted(IReadOnlyList<ScenarioDefinition>? scenarios)`; `OnScenarioStarted(ScenarioDefinition definition, TimeSpan waited = default, Type? waitedFor = null)`; `ReportScenario.Uses : IReadOnlyList<string>`, `WaitedMs : double`, `WaitedFor : string?`.

- [ ] **Step 1: Write the failing builder and sink tests**

Add to `HtmlReportModelBuilderTests`:

```csharp
    private static ScenarioDefinition Def(string id, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id, DisplayName = id, MethodName = "Ns." + id, Nodes = nodes,
    };

    [Fact]
    public void Scenarios_follow_the_RunStarted_order_whatever_order_they_started_and_finished_in()
    {
        var first = Def("first", Node(0, "a", "Given", "a"));
        var second = Def("second", Node(0, "b", "Given", "b"));

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnRunStarted([first, second]);
        builder.OnScenarioStarted(second);                         // admitted first
        builder.OnStepFinished(second, Result(second.Nodes[0], T0, 10));
        builder.OnScenarioStarted(first);
        builder.OnStepFinished(first, Result(first.Nodes[0], T0.AddMilliseconds(5), 10));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(["first", "second"], model.Scenarios.Select(s => s.ScenarioId));
    }

    [Fact]
    public void Interleaved_step_events_land_on_their_own_scenarios()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"), Node(1, "a1", "Then", "a1", dependsOn: [0]));
        var b = Def("b", Node(0, "b0", "Given", "b0"));

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnRunStarted([a, b]);
        builder.OnScenarioStarted(a);
        builder.OnScenarioStarted(b);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 10));
        builder.OnStepFinished(b, Result(b.Nodes[0], T0, 10));
        builder.OnStepFinished(a, Result(a.Nodes[1], T0.AddMilliseconds(10), 10));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(2, model.Scenarios[0].Steps.Count);
        Assert.Single(model.Scenarios[1].Steps);
    }

    [Fact]
    public void Total_is_the_wall_span_across_overlapping_scenarios_not_their_sum()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"));
        var b = Def("b", Node(0, "b0", "Given", "b0"));

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnRunStarted([a, b]);
        builder.OnScenarioStarted(a);
        builder.OnScenarioStarted(b);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 100));                      // 0..100
        builder.OnStepFinished(b, Result(b.Nodes[0], T0.AddMilliseconds(50), 100));  // 50..150

        Assert.Equal(150, builder.Build("2026-09-06T00:00:00Z").Summary.TotalMs);
    }

    [Fact]
    public void A_scenario_that_never_started_contributes_nothing_to_the_wall_span()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"));
        var never = Def("never", Node(0, "n0", "Given", "n0"));

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnRunStarted([a, never]);
        builder.OnScenarioStarted(a);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 40));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(40, model.Summary.TotalMs);
        Assert.Equal(2, model.Scenarios.Count);
    }

    [Fact]
    public void Uses_and_wait_are_carried_per_scenario()
    {
        var def = new ScenarioDefinition
        {
            ScenarioId = "w", DisplayName = "w", MethodName = "Ns.w", Nodes = [Node(0, "x", "Given", "x")],
            Uses = [new ContendedResourceUse(typeof(ExclusiveDb), LockMode.Exclusive), new ContendedResourceUse(typeof(SharedCatalog), LockMode.Shared)],
        };

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnRunStarted([def]);
        builder.OnScenarioStarted(def, waited: TimeSpan.FromMilliseconds(1234), waitedFor: typeof(ExclusiveDb));
        builder.OnStepFinished(def, Result(def.Nodes[0], T0, 10));

        var scenario = Assert.Single(builder.Build("2026-09-06T00:00:00Z").Scenarios);
        Assert.Equal(["ExclusiveDb:Exclusive", "SharedCatalog:Shared"], scenario.Uses);
        Assert.Equal(1234, scenario.WaitedMs);
        Assert.Equal("ExclusiveDb", scenario.WaitedFor);
    }
```

Add to `HtmlReportSinkTests` (mirroring `Writes_a_self_contained_html_file_on_run_finished`; reuse its `Def()` and `Passed()` helpers and `_dir`):

```csharp
    [Fact]
    public async Task The_wait_reaches_the_json_and_the_template_renders_it()
    {
        var path = Path.Combine(_dir, "raun-report-wait.html");
        var sink = new HtmlReport.HtmlReportSink(path, new TestTimeProviderUtc(T0));
        var def = Def();

        await sink.PublishAsync(new RunStarted(1, [def]));
        await sink.PublishAsync(new ScenarioStarted(def, TimeSpan.FromMilliseconds(250), typeof(ExclusiveDb)));
        await sink.PublishAsync(new StepFinished(def, Passed(def.Nodes[0])));
        await sink.PublishAsync(new ScenarioFinished(def, [Passed(def.Nodes[0])]));
        await sink.PublishAsync(new RunFinished());

        var html = await File.ReadAllTextAsync(path);
        Assert.Contains("\"waitedMs\": 250", html, StringComparison.Ordinal);
        Assert.Contains("\"waitedFor\": \"ExclusiveDb\"", html, StringComparison.Ordinal);
        Assert.Contains("waited ", html, StringComparison.Ordinal); // the header line's template text
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build errors — `OnRunStarted`, the `waited:` argument, `Uses`/`WaitedMs`/`WaitedFor` do not exist.

- [ ] **Step 3: Extend the model**

In `HtmlReportModel.cs`, `ReportScenario`, after `References`:

```csharp
    /// <summary>The scenario's contended-resource uses as <c>Type:Mode</c>; empty when none declared.</summary>
    public IReadOnlyList<string> Uses { get; init; } = [];

    /// <summary>Milliseconds admission held the scenario back behind a contended resource; 0 when none.</summary>
    public double WaitedMs { get; init; }

    /// <summary>Name of the resource that first refused the scenario; null when it never waited.</summary>
    public string? WaitedFor { get; init; }
```

- [ ] **Step 4: Rewrite the builder's top half**

In `HtmlReportModelBuilder.cs` replace the class header down to (and including) `Build` with:

```csharp
/// <summary>
/// Builds the deterministic <see cref="HtmlReportModel"/> from the run-event stream. All layout
/// (lane packing, resource rollup, ms-offset reduction) happens here — not in the renderer — so the
/// JSON is snapshot-testable (design §4). Drive it with <see cref="OnRunStarted"/> (the canonical
/// scenario order), <see cref="OnScenarioStarted"/>, one <see cref="OnStepFinished"/> per terminal
/// step, then <see cref="Build"/>. Scenarios run concurrently, so step events of different
/// scenarios interleave; each is routed by its definition, and the report keeps the run's order.
/// </summary>
internal sealed class HtmlReportModelBuilder
{
    private readonly List<ScenarioAccumulator> _scenarios = [];

    /// <summary>Lays the scenarios out in the run's canonical order before any of them starts.</summary>
    public void OnRunStarted(IReadOnlyList<ScenarioDefinition>? scenarios)
    {
        if (scenarios is null)
        {
            return;
        }

        foreach (var definition in scenarios)
        {
            if (Find(definition.ScenarioId) is null)
            {
                _scenarios.Add(new ScenarioAccumulator(definition));
            }
        }
    }

    public void OnScenarioStarted(ScenarioDefinition definition, TimeSpan waited = default, Type? waitedFor = null)
    {
        var acc = Find(definition.ScenarioId);
        if (acc is null)
        {
            // Driven without a RunStarted order (a sink under unit test): append as they come.
            acc = new ScenarioAccumulator(definition);
            _scenarios.Add(acc);
        }

        acc.Waited = waited;
        acc.WaitedFor = waitedFor;
    }

    public void OnStepFinished(ScenarioDefinition definition, StepResult result)
    {
        var acc = Find(definition.ScenarioId)
                  ?? throw new InvalidOperationException(
                      $"StepFinished for '{definition.ScenarioId}' before its ScenarioStarted.");
        acc.Add(result);
    }

    public HtmlReportModel Build(string generatedAtUtc)
    {
        var scenarios = _scenarios.Select(s => s.Build()).ToList();

        // Wall clock: earliest start to latest end over the scenarios that ran, not the sum — with
        // concurrent scenarios the sum would count overlapping time twice.
        var ran = _scenarios.Where(s => s.HasSteps).ToList();
        var totalMs = 0d;
        if (ran.Count > 0)
        {
            var origin = ran.Min(s => s.Start);
            totalMs = ran.Max(s => (s.Start - origin).TotalMilliseconds + s.DurationMs);
        }

        var summary = new ReportSummary
        {
            Passed = scenarios.Count(s => s.Status == "passed"),
            Failed = scenarios.Count(s => s.Status == "failed"),
            Skipped = scenarios.Count(s => s.Status == "skipped"),
            TotalMs = totalMs,
        };

        return new HtmlReportModel
        {
            GeneratedAtUtc = generatedAtUtc,
            Summary = summary,
            Scenarios = scenarios,
        };
    }

    private ScenarioAccumulator? Find(string scenarioId)
        => _scenarios.Find(s => s.Definition.ScenarioId == scenarioId);
```

In `ScenarioAccumulator`:
- add properties after `Definition`:

```csharp
        public TimeSpan Waited { get; set; }

        public Type? WaitedFor { get; set; }

        public bool HasSteps => _results.Count > 0;

        public DateTimeOffset Start => _results.Count == 0 ? DateTimeOffset.UnixEpoch : _results.Min(r => r.StartedAt);

        /// <summary>Latest step end relative to <see cref="Start"/>, in ms; 0 with no steps.</summary>
        public double DurationMs => _results.Count == 0
            ? 0
            : _results.Max(r => Ms(r.StartedAt - Start) + Ms(r.Duration));
```

- in `Build()`, replace `var start = _results.Count == 0 ? DateTimeOffset.UnixEpoch : _results.Min(r => r.StartedAt);` with `var start = Start;`, and in the returned `ReportScenario` add:

```csharp
                Uses = Definition.Uses.Select(u => $"{u.Resource.Name}:{u.Mode}").ToList(),
                WaitedMs = Ms(Waited),
                WaitedFor = WaitedFor?.Name,
```

(`durationMs` there stays as computed from `steps`; it equals `DurationMs`.)

- [ ] **Step 5: Forward in the sink and render the line**

`HtmlReportSink.cs`: add

```csharp
    protected override ValueTask OnRunStartedAsync(RunStarted e)
    {
        _builder.OnRunStarted(e.Scenarios);
        return default;
    }
```

and change `OnScenarioStartedAsync` to `_builder.OnScenarioStarted(e.Definition, e.Waited, e.WaitedFor);`.

`report-template.html`, in `buildScenarioCard`, replace the `head.innerHTML = …` statement with:

```js
      const waited = sc.waitedMs > 0
        ? '<div class="cls">waited ' + fmtDur(sc.waitedMs) + " for " + esc(sc.waitedFor || "") + "</div>"
        : "";
      head.innerHTML =
        '<div class="card-title">' +
          '<div class="name">' + esc(sc.displayName) + "</div>" +
          '<div class="cls">' + esc(sc.classDisplayName || "") +
            ' <span class="mth mono">· ' + esc(sc.methodName) + "</span></div>" +
          waited +
        "</div>" +
        '<div class="card-meta">' +
          '<span class="dur">' + fmtDur(sc.durationMs) + "</span>" +
          '<span class="pill ' + sc.status + '"><span class="pd"></span>' + sc.status + "</span>" +
        "</div>";
```

- [ ] **Step 6: Run the tests, accept the snapshot, verify the build**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -8`
Expected: everything passes except `Builds_the_expected_json_model`, which leaves `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.received.txt`. Inspect it: the only differences must be the three new fields (`"Uses": []`, `"WaitedMs": 0`, `"WaitedFor": null`) and `TotalMs` must still be `90`. Then:

```bash
mv test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.received.txt test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt
```

Run again: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -3` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --report-html --results-directory out/raun-report 2>&1 | tail -2` then open `out/raun-report/raun-report.html` in a browser (the in-app Browser pane can open a `file://` URL) — expected: the report renders, scenario order unchanged, the header's "wall clock" figure is no longer the sum. Delete `out/` afterwards so it does not end up in the commit.

- [ ] **Step 7: Commit**

```bash
jj commit -m "feat(report): registration order, wall-clock total, and the wait line" -m "The builder lays scenarios out from RunStarted's canonical order so admission order never reshuffles the report, computes the wall span instead of summing scenario durations (the header already said wall clock), and carries each scenario's uses and wait. The scenario header shows 'waited N for Resource' when admission held it back."
```

---

### Task 8: Generator lowers `[Uses<T>]` onto the definition

**Files:**
- Modify: `src/Raun.Generator/Lowering/Ir.cs`
- Modify: `src/Raun.Generator/Lowering/AttributeReader.cs`
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs`
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs`
- Modify: `test/Raun.Generator.Test/SampleSources.cs`, `GeneratorSnapshotTests.cs`
- Create: `test/Raun.Generator.Test/UsesLoweringTests.cs`
- Snapshot: `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Uses_scenario.verified.txt`

**Interfaces:**
- Produces: `ParsedUse(string ResourceFqn, string Mode)`; `ParsedScenario.Uses : IReadOnlyList<ParsedUse>` (sorted by `ResourceFqn`, ordinal); `AttributeReader.Uses(ImmutableArray<AttributeData>) : IEnumerable<(INamedTypeSymbol Resource, string Mode)>` where `Mode` is `"Shared"` or `"Exclusive"`; generated definitions get `Uses = new global::Raun.ContendedResourceUse[] { new global::Raun.ContendedResourceUse(typeof(global::X), global::Raun.LockMode.Shared), … }` only when non-empty.

- [ ] **Step 1: Add the sample sources**

Append to `test/Raun.Generator.Test/SampleSources.cs` inside the class:

```csharp
    // Contended resources: an assembly-level use, a class-level use, a scenario-level use, and uses
    // on DSL step methods. The expected reduction is Audit:Shared (assembly), Database:Exclusive
    // (the step's Exclusive beats the class's Shared), Smtp:Shared (method and step agree).
    public const string UsesDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        [assembly: Uses<UsesDemo.Audit>]

        namespace UsesDemo;

        [SharedResource]
        public sealed class Database : IContendedResource;

        [ExclusiveResource]
        public sealed class Smtp : IContendedResource;

        [SharedResource]
        public sealed class Audit : IContendedResource;

        public static class UsesDsl
        {
            extension(Given)
            {
                [StepName("the schedule is empty")]
                [Uses<Database>(LockMode.Exclusive)]
                public static Task ScheduleIsEmpty() => Task.CompletedTask;

                [StepName("a patient exists")]
                public static Task PatientExists() => Task.CompletedTask;
            }

            extension(When)
            {
                [StepName("a reminder is sent")]
                [Uses<Smtp>]
                public static Task ReminderIsSent() => Task.CompletedTask;
            }
        }
        """;

    // Scenario appended to UsesDsl, continuing its file-scoped `namespace UsesDemo;`.
    public const string UsesScenario =
        """

        [Uses<Database>]
        public static class UsesScenarios
        {
            [Scenario("clears and reminds")]
            [Uses<Smtp>]
            public static async Task ClearAndRemind()
            {
                await Given.ScheduleIsEmpty();
                await When.ReminderIsSent();
            }
        }
        """;

    // A scenario in the same DSL that touches no [Uses] site of its own: only the assembly and the
    // step methods it calls count.
    public const string UsesFreeScenario =
        """

        public static class PlainScenarios
        {
            [Scenario("just a patient")]
            public static async Task JustAPatient()
            {
                await Given.PatientExists();
            }
        }
        """;
```

- [ ] **Step 2: Write the failing lowering tests**

`test/Raun.Generator.Test/UsesLoweringTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// The generator unions every [Uses&lt;T&gt;] a scenario is subject to — its steps' DSL methods, the
/// scenario method, its classes, the assembly — onto ScenarioDefinition.Uses, one entry per type
/// with Exclusive winning. A scenario subject to none gets no Uses initializer at all.
/// </summary>
public class UsesLoweringTests
{
    private static ScenarioDefinition Lower(string scenario)
    {
        var result = GeneratorHarness.Run(SampleSources.UsesDsl + scenario);
        result.AssertCompiles();
        return result.Definitions().Single();
    }

    [Fact]
    public void Uses_from_every_site_are_unioned_and_reduced_per_type()
    {
        var definition = Lower(SampleSources.UsesScenario);

        var uses = definition.Uses.OrderBy(u => u.Resource.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(["Audit", "Database", "Smtp"], uses.Select(u => u.Resource.Name));
        Assert.Equal(LockMode.Shared, uses[0].Mode);     // assembly
        Assert.Equal(LockMode.Exclusive, uses[1].Mode);  // step's Exclusive beats class's Shared
        Assert.Equal(LockMode.Shared, uses[2].Mode);     // scenario method + step, both Shared
        Assert.All(uses, u => Assert.True(typeof(IContendedResource).IsAssignableFrom(u.Resource)));
    }

    [Fact]
    public void Only_the_assembly_and_the_called_steps_count_for_a_scenario_without_its_own_uses()
    {
        var definition = Lower(SampleSources.UsesFreeScenario);

        var use = Assert.Single(definition.Uses);
        Assert.Equal("Audit", use.Resource.Name);
        Assert.Equal(LockMode.Shared, use.Mode);
    }

    [Fact]
    public void A_scenario_subject_to_no_uses_emits_no_Uses_initializer()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        Assert.DoesNotContain("Uses =", result.GeneratedSource, StringComparison.Ordinal);
        Assert.Empty(result.Definitions().Single().Uses);
    }
}
```

Add to `GeneratorSnapshotTests`:

```csharp
    [Fact]
    public Task Uses_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.UsesDsl + SampleSources.UsesScenario))
            .UseDirectory("Snapshots");
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj 2>&1 | tail -15`
Expected: the three `UsesLoweringTests` fail (`Uses` is empty), `Uses_scenario` produces a received file (do not accept it yet).

- [ ] **Step 4: Lower the attribute**

`Ir.cs`: after `ResourceRoleClaim` add

```csharp
/// <summary>One reduced contended-resource use of a scenario: the token type's fully-qualified
/// name (with <c>global::</c>) and the mode name (<c>Shared</c> or <c>Exclusive</c>) as spelled on
/// <c>Raun.LockMode</c>.</summary>
internal readonly record struct ParsedUse(string ResourceFqn, string Mode);
```

and to `ParsedScenario`:

```csharp
    /// <summary>Every [Uses&lt;T&gt;] the scenario is subject to, one per type (Exclusive wins),
    /// sorted by <see cref="ParsedUse.ResourceFqn"/> so emission is deterministic. Empty ⇒ no initializer.</summary>
    public IReadOnlyList<ParsedUse> Uses { get; init; } = [];
```

`AttributeReader.cs`: add

```csharp
    /// <summary>Numeric value of <c>Raun.LockMode.Exclusive</c>. MUST stay identical to the runtime enum (separate assembly).</summary>
    private const int LockModeExclusive = 1;

    /// <summary>
    /// Every <c>[Uses&lt;T&gt;]</c> among <paramref name="attributes"/>: the token type and the mode
    /// name, <c>Shared</c> unless the attribute was given <c>LockMode.Exclusive</c>.
    /// </summary>
    public static IEnumerable<(INamedTypeSymbol Resource, string Mode)> Uses(ImmutableArray<AttributeData> attributes)
    {
        foreach (var attr in attributes)
        {
            if (attr.AttributeClass is { Name: "UsesAttribute", IsGenericType: true, TypeArguments.Length: 1 } cls
                && cls.TypeArguments[0] is INamedTypeSymbol resource)
            {
                var exclusive = attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is int mode
                    && mode == LockModeExclusive;
                yield return (resource, exclusive ? "Exclusive" : "Shared");
            }
        }
    }
```

`ScenarioParser.cs`:
- add a field next to `_dslNamespaces`: `private readonly Dictionary<string, string> _uses = new(StringComparer.Ordinal);` (fqn → mode).
- add the helper:

```csharp
    /// <summary>Merges the [Uses] on one site into the scenario's set; Exclusive wins per type.</summary>
    private void AddUses(ImmutableArray<AttributeData> attributes)
    {
        foreach (var (resource, mode) in AttributeReader.Uses(attributes))
        {
            var fqn = resource.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!_uses.TryGetValue(fqn, out var existing) || existing != "Exclusive")
            {
                _uses[fqn] = mode;
            }
        }
    }
```

(add `using System.Collections.Immutable;` if not already imported.)
- in the step-building method, right after `var resourceClaims = BuildResourceClaims(invocation, method, resultType is not null, replacements);` add `AddUses(method.GetAttributes());`
- in `Parse()`, right before `var usings = CollectUsings().ToList();` add:

```csharp
        // Uses declared on the scenario itself, its containing classes (nested outward), and the
        // assembly; the steps' DSL methods were merged as each step was lowered.
        AddUses(_method.GetAttributes());
        for (var type = _method.ContainingType; type is not null; type = type.ContainingType)
        {
            AddUses(type.GetAttributes());
        }

        AddUses(_model.Compilation.Assembly.GetAttributes());
```

and in the returned `ParsedScenario` add `Uses = _uses.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new ParsedUse(p.Key, p.Value)).ToList(),`.

`ScenarioEmitter.cs`: replace the `definition` object creation (the `SeparatedList<ExpressionSyntax>([ Set("ScenarioId", …), …, Set("Nodes", IdentifierName("nodes")) ])` block) with:

```csharp
        var members = new List<ExpressionSyntax>
        {
            Set("ScenarioId", Lit(scenario.ScenarioId)),
            Set("DisplayName", Lit(scenario.DisplayName)),
            Set("MethodName", Lit(scenario.MethodFullName)),
            Set("ClassDisplayName", Lit(scenario.ClassDisplayName)),
            Set("SourceFile", Lit(scenario.SourceFile)),
            Set("SourceLine", Num(scenario.SourceLine)),
            Set("Timeout", Timeout(scenario.TimeoutMs)),
            Set("TeardownPolicy", ParseExpression($"(global::Raun.Run){scenario.TeardownPolicy}")),
            Set("Nodes", IdentifierName("nodes")),
        };

        // Only when declared, so use-free scenarios emit exactly what they did before.
        if (scenario.Uses.Count > 0)
        {
            var entries = scenario.Uses.Select(u =>
                $"new global::Raun.ContendedResourceUse(typeof({u.ResourceFqn}), global::Raun.LockMode.{u.Mode})");
            members.Add(Set("Uses", ParseExpression(
                "new global::Raun.ContendedResourceUse[] { " + string.Join(", ", entries) + " }")));
        }

        var definition = ObjectCreationExpression(ParseTypeName("global::Raun.Model.ScenarioDefinition"))
            .WithInitializer(InitializerExpression(SyntaxKind.ObjectInitializerExpression, SeparatedList(members)));
```

- [ ] **Step 5: Run the tests, accept the new snapshot, verify all snapshots and the build**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj 2>&1 | tail -8`
Expected: `UsesLoweringTests` pass; `Uses_scenario` still fails with a received file; **no other snapshot test fails** (if `Linear_scenario` or any other leaves a received file, the emitter changed use-free output — fix that, do not accept). Inspect the received file: the definition initializer ends with `Uses = new global::Raun.ContendedResourceUse[] { new global::Raun.ContendedResourceUse(typeof(global::UsesDemo.Audit), global::Raun.LockMode.Shared), new global::Raun.ContendedResourceUse(typeof(global::UsesDemo.Database), global::Raun.LockMode.Exclusive), new global::Raun.ContendedResourceUse(typeof(global::UsesDemo.Smtp), global::Raun.LockMode.Shared) }`. Then:

```bash
mv test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Uses_scenario.received.txt test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Uses_scenario.verified.txt
```

Run again: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj 2>&1 | tail -3` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.

- [ ] **Step 6: Commit**

```bash
jj commit -m "feat(generator): lower [Uses<T>] onto ScenarioDefinition.Uses" -m "The parser unions [Uses<T>] from each step's DSL method, the scenario method, its containing classes, and the assembly, reduces per type with Exclusive winning, and the emitter adds a Uses initializer only when the set is non-empty, so use-free scenarios emit exactly what they did before (existing snapshots unchanged; one new snapshot)."
```

---

### Task 9: Analyzer rule RAUN015

**Files:**
- Modify: `src/Raun.Generator/Analysis/Descriptors.cs`
- Modify: `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`
- Modify: `src/Raun.Generator/AnalyzerReleases.Unshipped.md`
- Modify: `test/Raun.Generator.Test/AnalyzerTests.cs`

**Interfaces:**
- Produces: `Descriptors.ContendedResourceKind` (RAUN015, Error, category `Raun.Usage`).

- [ ] **Step 1: Write the failing analyzer tests**

Add to `AnalyzerTests`:

```csharp
    [Fact]
    public async Task Contended_resource_samples_are_clean()
    {
        Assert.Empty(await Analyze(SampleSources.UsesDsl + SampleSources.UsesScenario));
    }

    [Fact]
    public async Task RAUN015_contended_resource_without_a_kind()
    {
        var diagnostics = await GeneratorHarness.AnalyzeAsync(
            """
            using Raun;
            public sealed class Db : IContendedResource;
            """);

        var d = Assert.Single(diagnostics, d => d.Id == "RAUN015");
        Assert.Contains("Db", d.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RAUN015_contended_resource_with_two_kinds()
    {
        var diagnostics = await GeneratorHarness.AnalyzeAsync(
            """
            using Raun;
            [ExclusiveResource, SharedResource]
            public sealed class Db : IContendedResource;
            """);

        AssertHas(diagnostics, "RAUN015");
    }

    [Fact]
    public async Task RAUN015_pool_of_nothing()
    {
        var diagnostics = await GeneratorHarness.AnalyzeAsync(
            """
            using Raun;
            [PooledResource(0)]
            public sealed class Smtp : IContendedResource;
            """);

        AssertHas(diagnostics, "RAUN015");
    }

    [Fact]
    public async Task A_type_that_is_not_a_contended_resource_is_left_alone()
    {
        Assert.Empty(await GeneratorHarness.AnalyzeAsync(
            """
            public sealed class Plain;
            """));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj 2>&1 | tail -10`
Expected: the three RAUN015 tests fail (no diagnostic reported).

- [ ] **Step 3: Add the descriptor, the rule, and the release row**

`Descriptors.cs`, after `StepContextInCleanup`:

```csharp
    public static readonly DiagnosticDescriptor ContendedResourceKind = new(
        "RAUN015",
        "Contended resource must declare exactly one kind",
        "'{0}' implements IContendedResource and must carry exactly one of [ExclusiveResource], [SharedResource], [PooledResource] with a capacity of at least 1",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

`ScenarioAnalyzer.cs`:
- add `Descriptors.ContendedResourceKind,` to `SupportedDiagnostics`;
- in `Initialize` add `context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);`
- add:

```csharp
    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        try
        {
            AnalyzeContendedResource(context, (INamedTypeSymbol)context.Symbol);
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, context.Symbol.Locations.FirstOrDefault(), GeneratorSafety.Describe(ex)));
        }
    }

    /// <summary>RAUN015: a type implementing Raun.IContendedResource carries exactly one kind attribute,
    /// and a pool has a capacity of at least 1. The runtime gate throws the same; catching it here
    /// keeps it out of the first parallel run.</summary>
    private static void AnalyzeContendedResource(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        if (!type.AllInterfaces.Any(i => i.Name == "IContendedResource" && i.ContainingNamespace.ToDisplayString() == "Raun"))
        {
            return;
        }

        var kinds = 0;
        var poolTooSmall = false;
        foreach (var attr in type.GetAttributes())
        {
            switch (attr.AttributeClass?.Name)
            {
                case "ExclusiveResourceAttribute":
                case "SharedResourceAttribute":
                    kinds++;
                    break;
                case "PooledResourceAttribute":
                    kinds++;
                    poolTooSmall = attr.ConstructorArguments.Length != 1
                        || attr.ConstructorArguments[0].Value is not int capacity
                        || capacity < 1;
                    break;
            }
        }

        if (kinds != 1 || poolTooSmall)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.ContendedResourceKind, type.Locations.FirstOrDefault(), type.Name));
        }
    }
```

`AnalyzerReleases.Unshipped.md`: append the row

```
RAUN015 | Raun.Usage | Error | Contended resource must declare exactly one kind
```

- [ ] **Step 4: Run the tests to verify they pass, and the build is warning-free**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj 2>&1 | tail -3` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)` (RS2000 satisfied by the row). `test/Raun.Test` still builds with its deliberately invalid tokens (`NoKind`, `TwoKinds`, `ZeroCapacity`): it references only the `Raun` project, so the analyzer never runs there.

- [ ] **Step 5: Commit**

```bash
jj commit -m "feat(analyzer): RAUN015 requires exactly one kind on a contended resource" -m "A type implementing IContendedResource without exactly one of [ExclusiveResource], [SharedResource], [PooledResource], or with a pool capacity below 1, is an error at compile time — the ContentionGate would throw the same on the first parallel run."
```

---

### Task 10: Aspire sample — an exclusive "clear the schedule" scenario

**Files:**
- Modify: `samples/AspireAppointments/AspireAppointments.Api/Program.cs`
- Modify: `samples/AspireAppointments/AspireAppointments.Tests/Actors.cs` (`AppointmentsClient`)
- Modify: `samples/AspireAppointments/AspireAppointments.Tests/AppointmentsDsl.cs`
- Modify: `samples/AspireAppointments/AspireAppointments.Tests/Scenarios.cs`

- [ ] **Step 1: Add the endpoint, the client call, and the steps**

`Program.cs` (API), after the `MapGet("/appointments", …)` block:

```csharp
// Admin-only. The suite's "clear the schedule" scenario uses this exclusively: run alongside a
// booking scenario it would empty the schedule between "admin books" and "patient reads".
app.MapDelete("/appointments", (HttpContext http) =>
{
    if (RoleOf(http) is not "admin")
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    appointments.Clear();
    return Results.NoContent();
});
```

`Actors.cs`, in `AppointmentsClient` after `ListAsync`:

```csharp
    /// <summary>Removes every appointment. The API allows this for <see cref="Actor.Admin"/> only.</summary>
    public async Task<HttpResponseMessage> ClearAsync() =>
        await http.DeleteAsync(new Uri("/appointments", UriKind.Relative));
```

`AppointmentsDsl.cs`: inside `extension(When)` add

```csharp
        [StepName("an admin clears the schedule")]
        public static async Task AdminClearsSchedule(ScenarioContext? ctx = null)
        {
            var response = await Api(ctx).As(Actor.Admin).ClearAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"clear failed: {response.StatusCode}");
            }
        }
```

and inside `extension(Then)` add

```csharp
        [StepName("the schedule is empty")]
        public static async Task ScheduleIsEmpty(ScenarioContext? ctx = null)
        {
            var remaining = await Api(ctx).As(Actor.Patient).ListAsync();
            if (remaining.Length != 0)
            {
                throw new InvalidOperationException($"expected an empty schedule, found {remaining.Length} appointment(s)");
            }
        }
```

- [ ] **Step 2: Declare the resource and the scenario**

Replace `Scenarios.cs` with:

```csharp
using System.ComponentModel;
using Raun;

namespace AspireAppointments.Tests;

/// <summary>
/// The one thing these scenarios contend for: the API's schedule. Booking scenarios share it —
/// each books its own patient — while clearing it needs it exclusively. Scenarios run concurrently
/// by default; this declaration is what keeps "clear" from running between another scenario's
/// "admin books" and "patient reads". It is a name, not a service: Raun never constructs it.
/// </summary>
[SharedResource]
public sealed class Schedule : IContendedResource;

/// <summary>
/// Scenarios driving a real Aspire application. The AppHost is started once for the run, as the
/// preflight node — so its startup is timed and reported, and a failure to come up is a failing test
/// rather than a process that exits before anything reports.
/// </summary>
[DisplayName("Appointments API")]
[Uses<Schedule>]
public static class Scenarios
{
    // Two actors in one scenario: booked as an admin, read back as a patient. The actor belongs to
    // the call, which is why nothing here mutates headers on a shared client.
    [Scenario("an admin books an appointment a patient can read")]
    public static async Task AdminBooksPatientReads()
    {
        await Given.ApiIsReachable();

        var appointment = await When.AdminBooks("Alice", "2026-09-05T09:00");

        await Then.PatientCanRead(appointment);
    }

    [Scenario("a patient may not book appointments")]
    public static async Task PatientCannotBook()
    {
        await Given.ApiIsReachable();

        var status = await When.PatientTriesToBook("Bob", "2026-09-05T10:00");

        await Then.AttemptWasRejected(status);
    }

    // Exclusive: waits until no booking scenario holds the schedule, and keeps new ones out until
    // it is done. Without this, "the patient can read" above could race against the clear.
    [Scenario("an admin clears the schedule")]
    [Uses<Schedule>(LockMode.Exclusive)]
    public static async Task AdminClearsSchedule()
    {
        await Given.ApiIsReachable();

        await When.AdminClearsSchedule();

        await Then.ScheduleIsEmpty();
    }
}
```

- [ ] **Step 3: Build and run the sample**

Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.
Run: `dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj 2>&1 | tail -4`
Expected: `13 total / 13 passed` (preflight, three scenarios of three steps plus teardown each). If the summary shows fewer, `--list-tests` lists the nodes; if "the schedule is empty" fails, the clear ran before a booking scenario finished — that means `Uses` did not reach the definition (check `Scenarios.cs` compiles against the new generator: the sample references the projects, so a stale build is the usual cause; `dotnet build` again).

Run it once more with tracing pointed at nothing to confirm the span tags do not require a listener: `dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj -- --max-parallel-scenarios 1 2>&1 | tail -2` — expected: `13 total / 13 passed`.

- [ ] **Step 4: Commit**

```bash
jj commit -m "feat(samples): Aspire suite clears the schedule under an exclusive use" -m "The mock API gains an admin-only DELETE /appointments; the suite declares the schedule as a shared contended resource, books under a shared use, and clears under an exclusive one — the hazard concurrent scenarios create and the declaration that removes it."
```

---

### Task 11: Docs, spec amendments, final verification

**Files:**
- Modify: `README.md`, `AGENTS.md`
- Modify: `docs/superpowers/specs/2026-09-05-resource-conflict-detection-design.md` (Tier 3 section)
- Modify: `docs/superpowers/specs/2026-09-06-tracing-design.md` (amendment)
- Modify: `docs/superpowers/specs/2026-09-06-concurrent-scenarios-design.md` (status, `WaitedFor` field)

- [ ] **Step 1: README**

In `README.md`:
- Both occurrences of `RAUN000`–`RAUN014` (the "How it works" list and the project-layout table) become `RAUN000`–`RAUN015`.
- After the `## Run it` section's closing paragraph (the one ending "and nothing after it."), add (the inner code fences are part of the README text):

~~~markdown
### Scenarios run in parallel

Scenarios are independent by contract: each gets its own DI scope, context, and trace, and you own
the isolation of the data it touches — unique patients per scenario, not a shared `"Jane"`. Raun runs
them concurrently, up to the processor count by default. Steps inside a scenario still follow the
graph; the degree counts scenarios.

```bash
dotnet run --project MyScenarios -- --max-parallel-scenarios 1   # one scenario at a time
dotnet run --project MyScenarios -- --max-parallel-scenarios 4
```

The code default is the `maxParallelScenarios` argument of `RaunTestApplication.RunAsync` (or
`MaxParallelScenarios` on the Aspire options); the command line overrides it per run.

When scenarios genuinely contend for something — one database a few scenarios need to themselves,
three SMTP servers, a serial port — declare it with a token type and `[Uses<T>]`:

```csharp
[SharedResource]     public sealed class Database : IContendedResource;   // any number; Exclusive serializes
[ExclusiveResource]  public sealed class SerialPort : IContendedResource; // one at a time
[PooledResource(3)]  public sealed class Smtp : IContendedResource;       // up to three

[Uses<Database>]                        // shared use; put it on a step, a scenario, a class, or the assembly
[Uses<Database>(LockMode.Exclusive)]    // waits for every holder to finish, then keeps them out
[Scenario("the schema migrates cleanly")]
public static async Task SchemaMigrates() { … }
```

| | Shared use (default) | Exclusive use |
|---|---|---|
| `[ExclusiveResource]` | the one slot | the one slot |
| `[SharedResource]` | free | waits for zero holders, blocks new ones |
| `[PooledResource(N)]` | one of N | all N |

A scenario's whole set is acquired before it starts and released after its teardown, so nothing ever
waits while holding: no deadlocks. Time spent waiting shows on the scenario's span
(`raun.scenario.waited_ms`) and in the HTML report's scenario header. The token is a name, not a
service — register it in DI yourself if it is also one.
~~~

- [ ] **Step 2: AGENTS.md**

Under "Things that look like bugs but are not" add:

```markdown
- Scenarios run concurrently by default (`--max-parallel-scenarios 1` makes a run sequential), so
  console and step-completion order across scenarios varies run to run. The HTML report lists
  scenarios in registration order regardless.
- A scenario header reading "waited 1.2 s for Database" is admission control, not slowness: an
  `[Uses<T>]` declaration held it back behind a running scenario.
```

In the "Build, test, verify" list, change the bullet "MTP rejects `--nologo` and `--filter` on the command line. Run whole projects." to "MTP rejects `--nologo` and `--filter` on the command line. Run whole projects; `--max-parallel-scenarios 1` makes a run sequential when you need deterministic output order."

- [ ] **Step 3: Spec amendments**

`2026-09-05-resource-conflict-detection-design.md`: at the top of the "Tier 3 — deferred" section insert the line
`> **Superseded 2026-09-06** by `2026-09-06-concurrent-scenarios-design.md`: cross-scenario admission is declared with `[ContendedResource]`/`[Uses<T>]`, not derived from roles.`

`2026-09-06-tracing-design.md`: append

```markdown
## Amendment 2026-09-06 — concurrent scenarios

Scenario spans gain `raun.scenario.uses` (`Type:Mode` list, absent when none), and, when admission
held the scenario back behind a contended resource, `raun.scenario.waited_ms` and
`raun.scenario.waited_for`. Scenario spans stay roots linked to the run span; concurrent scenarios
are separate traces (`RunLoopTests.Concurrent_scenarios_keep_their_own_traces_and_step_parents`).
See `2026-09-06-concurrent-scenarios-design.md`.
```

`2026-09-06-concurrent-scenarios-design.md`: change `- **Status:** Approved in brainstorm (Patrik, 2026-09-06). Not yet built.` to `- **Status:** Built 2026-09-06 (plan: docs/superpowers/plans/2026-09-06-concurrent-scenarios.md).`, and in the Report section change `ReportScenario gains Uses (a list of "Type:Mode" strings) and WaitedMs.` to `ReportScenario gains Uses (a list of "Type:Mode" strings), WaitedMs, and WaitedFor (the refusing type's name, for the header line).`

- [ ] **Step 4: Full verification**

Run each and read the tail:

```bash
dotnet build Raun.slnx 2>&1 | tail -3
```
Expected: `0 Warning(s)`, `0 Error(s)`.

```bash
dotnet test Raun.slnx 2>&1 | tail -6
```
Expected: every project green; total above the previous 497 by the new tests; no `received` files left: `find test -name "*.received.*"` prints nothing.

```bash
dotnet run --project samples/AppointmentTests/AppointmentTests.csproj 2>&1 | tail -2
```
Expected: `52 total / 51 passed / 1 skipped` (unchanged).

```bash
dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj 2>&1 | tail -2
```
Expected: `13 total / 13 passed`.

```bash
jj st
```
Expected: only the doc changes from this task, nothing stray.

- [ ] **Step 5: Commit**

```bash
jj commit -m "docs: concurrent scenarios and contended resources" -m "README explains the default degree, --max-parallel-scenarios, the three resource kinds and the use-by-kind table; AGENTS.md notes the nondeterministic cross-scenario output order and the 'waited for' header line; the conflict-detection spec's Tier 3 is marked superseded and the tracing spec records the new scenario attributes."
```

Then report to Patrik: the change set is a linear stack of eleven commits on top of `main`, integration (`jj bookmark set main -r @-` and `jj git push --bookmark main`) is his to run.
