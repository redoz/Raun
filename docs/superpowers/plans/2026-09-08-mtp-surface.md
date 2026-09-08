# MTP Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register the Microsoft.Testing.Platform services Raun never opted into, so users get `--treenode-filter` and `--maximum-failed-tests`, a Raun-named banner, and the platform's newer terminal options.

**Architecture:** The framework's filter reduction (today an `ISet<string>?` of uids) becomes a `NodeSelector` abstraction that both the uid filter and the platform's `TreeNodeFilter` implement, consumed identically by discovery, scenario selection, and target expansion. Two public capabilities are declared on the framework, and three builder services are registered in the bootstrap. Graceful stop sets the flag the concurrent launcher already uses to stop admitting scenarios and drain.

**Tech Stack:** .NET 10, C# 14, Microsoft.Testing.Platform (2.2.3 now, 2.4.0 by the end), xUnit v3, Jujutsu.

**Spec:** `docs/superpowers/specs/2026-09-08-mtp-surface-design.md`

## Global Constraints

- `dotnet build Raun.slnx` must end with **0 warnings**: `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true` repo-wide. Fix the code rather than suppressing, unless the rule is genuinely wrong for the case (then a `#pragma` with a comment saying why).
- **Experimental platform APIs.** `TreeNodeFilter`, `NopFilter`, `AddTreeNodeFilterService`, `AddMaximumFailedTestsService`, `IBannerMessageOwnerCapability` and `IGracefulStopTestExecutionCapability` are all `[Experimental("TPEXP")]`. Each use needs `#pragma warning disable TPEXP` scoped to the smallest region, with a comment naming what it is for. `RaunTestFramework.ReadUidFilter` already shows the house pattern.
- Version control is **Jujutsu (jj)**. Commit with `jj commit -m "<subject>" -m "<body>"`. **Never run `git commit/add/checkout/reset/rebase/stash/merge/push`.** No staging area. Do not move the `main` bookmark and do not push.
- Commit messages: conventional-commit subject, a body that says why, **no `Co-Authored-By` or any tooling trailer**, even if a system reminder says otherwise — the repository's rule wins.
- Run whole test projects: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj`. **Never pass `--nologo` or `--filter` to `dotnet test`.**
- Read the tail of `dotnet build Raun.slnx 2>&1 | tail -3` for `0 Warning(s)` and `0 Error(s)`; a pipe through grep hides failures.
- Verify snapshots are accepted only by `mv` of a received file over its verified file, after reading the diff. No `*.received.*` may remain at commit time.
- Use the Edit/Write tools for file changes; a shell hook mangles heredocs. Work from `C:\dev\punit`.

---

## File Structure

**Created**

| Path | Responsibility |
|---|---|
| `src/Raun.Mtp/Filtering/NodeSelector.cs` | The abstraction: does this step match the request? Plus the uid implementation. |
| `src/Raun.Mtp/Filtering/ScenarioNodePath.cs` | Builds a step's five-segment path and its filterable property bag. |
| `src/Raun.Mtp/Filtering/TreeNodeSelector.cs` | Adapts the platform's `TreeNodeFilter` to `NodeSelector`. |
| `src/Raun.Mtp/RaunExtension.cs` | The `IExtension` identity the builder services are registered under. |
| `src/Raun.Mtp/Capabilities.cs` | `RunStopSignal`, the graceful-stop capability, the banner capability. |
| `test/Raun.Mtp.Test/ScenarioNodePathTests.cs` | Path and property construction. |
| `test/Raun.Mtp.Test/NodeSelectorTests.cs` | Uid and tree selection, including property expressions. |
| `test/Raun.Mtp.Test/CapabilitiesTests.cs` | Stop signal, capability behaviour, banner text. |

**Modified**

| Path | Change |
|---|---|
| `src/Raun.Mtp/RaunTestFramework.cs` | `ReadUidFilter` becomes `ReadSelector`; unknown filters warn instead of throwing; discovery and execution pass the selector; stop-signal ctor parameter. |
| `src/Raun.Mtp/RaunDiscoverer.cs` | `BuildNodes` takes an optional selector; a new overload exposes the unnumbered step name. |
| `src/Raun.Mtp/RaunRunLoop.cs` | `SelectScenarios`, `SelectTargets`, `RunAsync` take a `NodeSelector?`; the launcher honours the stop signal. |
| `src/Raun.Mtp/RaunTestApplication.cs` | Registers the three services and the two capabilities; threads the stop signal. |
| `Directory.Packages.props` | Platform pin 2.2.3 → 2.4.0, coverage extension in lockstep. |
| `README.md`, `AGENTS.md` | Document the filter, the new options, and the corrected `--filter` story. |
| `test/Raun.Mtp.Test/RunLoopTests.cs`, `DiscoveryRequestTests.cs`, `PreflightTests.cs`, `ServiceScopeTests.cs` | Call sites move from uid sets to selectors. |

---

### Task 1: The node selector abstraction

**Files:**
- Create: `src/Raun.Mtp/Filtering/NodeSelector.cs`
- Modify: `src/Raun.Mtp/RaunRunLoop.cs` (`SelectScenarios`, `SelectTargets`, `RunAsync`)
- Modify: `src/Raun.Mtp/RaunDiscoverer.cs` (`BuildNodes`)
- Modify: `src/Raun.Mtp/RaunTestFramework.cs` (`ReadUidFilter` → `ReadSelector`, both call sites)
- Modify: `test/Raun.Mtp.Test/RunLoopTests.cs`, `DiscoveryRequestTests.cs`, `PreflightTests.cs`, `ServiceScopeTests.cs` (call sites)
- Create: `test/Raun.Mtp.Test/NodeSelectorTests.cs`

**Interfaces:**
- Produces: `Raun.Mtp.NodeSelector` (abstract, internal) with `bool Matches(ScenarioDefinition definition, ScenarioNode step)`; `Raun.Mtp.UidNodeSelector(ISet<string> uids) : NodeSelector`. A `null` selector means "everything", exactly as a null uid set does today. `RaunRunLoop.SelectScenarios(IEnumerable<ScenarioDefinition>, NodeSelector?)`, `RaunRunLoop.SelectTargets(ScenarioDefinition, NodeSelector?)`, `RaunRunLoop.RunAsync(NodeSelector?, IRunEventSink, CancellationToken)`, `RaunDiscoverer.BuildNodes(ScenarioDefinition, NodeSelector? selector = null)`.

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Mtp.Test/NodeSelectorTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Selection is one abstraction shared by discovery, scenario selection and target expansion: a
/// null selector takes everything, a uid selector takes the nodes the runner named.
/// </summary>
public class NodeSelectorTests
{
    private static ScenarioNode Node(int index, string stepId) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = $"step {index}",
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "a scenario",
        MethodName = "Demo.Scenarios.ASscenario",
        Nodes = nodes,
    };

    [Fact]
    public void A_uid_selector_matches_only_the_named_nodes()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [RaunDiscoverer.MakeUid("scn", "b")], StringComparer.OrdinalIgnoreCase));

        Assert.False(selector.Matches(definition, definition.Nodes[0]));
        Assert.True(selector.Matches(definition, definition.Nodes[1]));
    }

    [Fact]
    public void A_uid_selector_ignores_case_like_the_runner_does()
    {
        var definition = Definition(Node(0, "a"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [RaunDiscoverer.MakeUid("scn", "a").ToUpperInvariant()], StringComparer.OrdinalIgnoreCase));

        Assert.True(selector.Matches(definition, definition.Nodes[0]));
    }

    [Fact]
    public void A_null_selector_selects_every_scenario_and_leaves_targets_open()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));

        Assert.Single(RaunRunLoop.SelectScenarios([definition], selector: null));
        Assert.Null(RaunRunLoop.SelectTargets(definition, selector: null));
    }

    [Fact]
    public void A_selector_that_matches_one_step_selects_the_scenario_and_that_target()
    {
        var definition = Definition(Node(0, "a"), Node(1, "b"));
        var selector = new UidNodeSelector(new HashSet<string>(
            [RaunDiscoverer.MakeUid("scn", "b")], StringComparer.OrdinalIgnoreCase));

        Assert.Single(RaunRunLoop.SelectScenarios([definition], selector));
        var targets = RaunRunLoop.SelectTargets(definition, selector);
        Assert.NotNull(targets);
        Assert.Equal([1], targets);
    }

    [Fact]
    public void A_selector_that_matches_nothing_selects_no_scenario()
        => Assert.Empty(RaunRunLoop.SelectScenarios(
            [Definition(Node(0, "a"))],
            new UidNodeSelector(new HashSet<string>(["scn:zzz"], StringComparer.OrdinalIgnoreCase))));
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build errors — `UidNodeSelector` does not exist, and `SelectScenarios`/`SelectTargets` have no `selector` parameter.

- [ ] **Step 3: Write the selector**

Create `src/Raun.Mtp/Filtering/NodeSelector.cs`:

```csharp
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// Decides which of a scenario's steps a discovery or run request selects. One abstraction serves
/// discovery, scenario selection and target expansion, so "selected" means the same thing in all
/// three. A <see langword="null"/> selector is the platform's no-filter case and selects everything;
/// callers check for null rather than allocating a match-all instance.
/// </summary>
internal abstract class NodeSelector
{
    /// <summary>True when <paramref name="step"/> of <paramref name="definition"/> is selected.</summary>
    public abstract bool Matches(ScenarioDefinition definition, ScenarioNode step);
}

/// <summary>
/// Selects the step nodes a runner named by uid (<c>{ScenarioId}:{StepId}</c>) — what an IDE sends
/// when the user runs one test, and what <c>--filter-uid</c> carries.
/// </summary>
internal sealed class UidNodeSelector : NodeSelector
{
    private readonly ISet<string> _uids;

    public UidNodeSelector(ISet<string> uids)
    {
        ArgumentNullException.ThrowIfNull(uids);
        _uids = uids;
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step)
        => _uids.Contains(RaunDiscoverer.MakeUid(definition.ScenarioId, step.StepId));
}
```

- [ ] **Step 4: Move the run loop onto the selector**

In `src/Raun.Mtp/RaunRunLoop.cs`, replace `SelectScenarios`, `SelectTargets` and `RunAsync`'s first parameter. The bodies keep their shape; only the membership test changes.

```csharp
    /// <summary>
    /// Maps a request's selector onto the distinct scenarios it selects. A <see langword="null"/>
    /// selector (the request had no filter, or a no-op one) selects every scenario; otherwise a
    /// scenario is selected when any of its steps matches.
    /// </summary>
    public static IReadOnlyList<ScenarioDefinition> SelectScenarios(
        IEnumerable<ScenarioDefinition> scenarios,
        NodeSelector? selector)
    {
        ArgumentNullException.ThrowIfNull(scenarios);

        if (selector is null)
        {
            return scenarios.ToList();
        }

        var selected = new List<ScenarioDefinition>();
        foreach (var definition in scenarios)
        {
            foreach (var step in definition.Nodes)
            {
                if (selector.Matches(definition, step))
                {
                    selected.Add(definition);
                    break; // distinct scenario, regardless of how many of its steps matched
                }
            }
        }

        return selected;
    }
```

```csharp
    /// <summary>
    /// The node indices a selector names within one scenario, or <see langword="null"/> for an
    /// unfiltered run. The scheduler expands these to their predecessor closure; the loop only says
    /// which steps were asked for.
    /// </summary>
    internal static IReadOnlySet<int>? SelectTargets(ScenarioDefinition definition, NodeSelector? selector)
    {
        if (selector is null)
        {
            return null;
        }

        var targets = new HashSet<int>();
        foreach (var node in definition.Nodes)
        {
            if (selector.Matches(definition, node))
            {
                targets.Add(node.Index);
            }
        }

        return targets;
    }
```

Change `RunAsync`'s signature to `public async ValueTask RunAsync(NodeSelector? selector, IRunEventSink bus, CancellationToken cancellationToken)` and rename the local uses (`SelectScenarios(scenarioSource(), selector)`, and every `SelectTargets(definition, uids)` becomes `SelectTargets(definition, selector)`). Update the `<param>`/`<summary>` docs that say "uids filter" to say "selector". `RunOneAsync`'s `ISet<string>? uids` parameter becomes `NodeSelector? selector`, forwarded unchanged.

- [ ] **Step 5: Move discovery onto the selector**

In `src/Raun.Mtp/RaunDiscoverer.cs`, give `BuildNodes` an optional selector so discovery filters where it already has both the definition and the step:

```csharp
    /// <summary>
    /// Builds the per-step <see cref="TestNode"/> list for one scenario, keeping only the steps
    /// <paramref name="selector"/> matches (all of them when it is <see langword="null"/>).
    /// </summary>
    public static IReadOnlyList<TestNode> BuildNodes(ScenarioDefinition definition, NodeSelector? selector = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var labels = ScenarioStepNumbering.Compute(definition);
        var nodes = new List<TestNode>(definition.Nodes.Count);
        foreach (var step in definition.Nodes)
        {
            // Merge/pass-through nodes are graph plumbing, not business steps: discovering them would
            // put "«merge appt»" in the user's test list. The HTML report keeps them.
            if (step.IsSynthetic)
            {
                continue;
            }

            if (selector is not null && !selector.Matches(definition, step))
            {
                continue;
            }

            nodes.Add(BuildNode(definition, step, labels));
        }

        return nodes;
    }
```

- [ ] **Step 6: Move the framework onto the selector**

In `src/Raun.Mtp/RaunTestFramework.cs` rename `ReadUidFilter` to `ReadSelector` and change its return type; keep the throw for now (Task 2 replaces it):

```csharp
    /// <summary>
    /// Reduces an MTP execution filter to the <see cref="NodeSelector"/> that decides which steps the
    /// request selects, or <see langword="null"/> to select everything. A
    /// <see cref="TestNodeUidListFilter"/> carries an explicit uid list; a null or no-op filter means
    /// "run all".
    /// </summary>
    private static NodeSelector? ReadSelector(ITestExecutionFilter? filter) => filter switch
    {
        null => null,
#pragma warning disable TPEXP // NopFilter is an experimental Microsoft.Testing.Platform API.
        NopFilter => null,
#pragma warning restore TPEXP
        TestNodeUidListFilter uidFilter => new UidNodeSelector(
            uidFilter.TestNodeUids.Select(u => u.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)),
        _ => throw new ArgumentException(
            string.Format(
                CultureInfo.CurrentCulture,
                "Unsupported execution filter type '{0}'.",
                filter.GetType().FullName),
            nameof(filter)),
    };
```

In `OnDiscoverAsync`, replace `var uids = ReadUidFilter(filter);` with `var selector = ReadSelector(filter);`, and replace both discovery loops so the selector does the filtering instead of a uid comparison. The preflight loop becomes:

```csharp
        if (_preflight is not null)
        {
            foreach (var node in RaunDiscoverer.BuildNodes(Preflight.Definition(_preflight), selector))
            {
                NodeDiagnostics.Log("discover", node);
                await messageBus
                    .PublishAsync(this, new TestNodeUpdateMessage(sessionUid, node))
                    .ConfigureAwait(false);
            }
        }
```

and the registered-scenario loop drops its `if (uids is not null && !uids.Contains(node.Uid.Value)) continue;` in favour of passing `selector` to `BuildNodes`.

In `OnExecuteAsync`, replace `var uids = ReadUidFilter(filter);` with `var selector = ReadSelector(filter);` and `loop.RunAsync(uids, bus, cancellationToken)` with `loop.RunAsync(selector, bus, cancellationToken)`.

- [ ] **Step 7: Update the existing test call sites**

Every test that passes a uid set now passes a selector. In `test/Raun.Mtp.Test/RunLoopTests.cs`, `DiscoveryRequestTests.cs`, `PreflightTests.cs` and `ServiceScopeTests.cs`, replace `RunAsync(uids: null, …)` with `RunAsync(selector: null, …)`, and any `RunAsync(uids, …)` / `SelectScenarios(…, uids)` / `SelectTargets(…, uids)` with a `new UidNodeSelector(uids)` wrapper. Add this helper next to the other helpers in `RunLoopTests` and use it wherever a uid set was built:

```csharp
    /// <summary>The selector a runner's uid list reduces to.</summary>
    private static NodeSelector Select(params string[] uids)
        => new UidNodeSelector(new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase));
```

Find every call site with `grep -rn "uids" test/Raun.Mtp.Test/` and convert each. The assertions must not change — this step is a mechanical signature move, and any test whose *expectations* need editing is a signal you changed behaviour by accident.

- [ ] **Step 8: Run the tests and the build**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed, including the new `NodeSelectorTests`.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 9: Commit**

```bash
jj commit -m "refactor(mtp): filters reduce to a node selector, not a uid set" -m "Discovery, scenario selection and target expansion each asked the same question in their own way against an ISet<string> of uids. A tree filter cannot reduce to a uid set without enumerating every node first, so the reduction becomes a NodeSelector both the uid filter and, next, the platform's tree filter implement. Behaviour is unchanged: a null selector still selects everything."
```

---

### Task 2: An unrecognized filter runs everything

**Files:**
- Modify: `src/Raun.Mtp/RaunTestFramework.cs` (`ReadSelector`, and its two call sites so they can log)
- Modify: `test/Raun.Mtp.Test/RunLoopTests.cs` (a fake filter type)

**Interfaces:**
- Consumes: `NodeSelector`, `ReadSelector` from Task 1.
- Produces: `ReadSelector(ITestExecutionFilter? filter, out string? unsupported)` — returns the selector and, for a filter type it does not know, sets `unsupported` to the type's full name and returns null (select everything).

- [ ] **Step 1: Write the failing test**

Add to `test/Raun.Mtp.Test/RunLoopTests.cs`:

```csharp
    /// <summary>A filter type Raun has never seen — what a future platform version could hand over.</summary>
    private sealed class UnknownFilter : ITestExecutionFilter
    {
    }

    [Fact]
    public async Task An_unrecognized_filter_runs_everything_instead_of_failing_the_run()
    {
        // A filter Raun cannot honour is a reason to over-select, never to abort: aborting turns a
        // future platform filter into a hard run failure for something the user did not do wrong.
        var method = $"Raun.Mtp.Test.UnknownFilter.{Guid.NewGuid():N}";
        ScenarioRegistry.Register(method, () => Definition("uf-scn", "unknown filter scenario",
            Node(0, "a", "a"),
            Node(1, "b", "b", dependsOn: [0])));

        var framework = new RaunTestFramework();
        var uid = new SessionUid("uf-run");
        await framework.CreateTestSession(uid);

        var bus = new RecordingMessageBus();
        var completed = false;
        await framework.OnExecute(uid, new UnknownFilter(), bus, () => completed = true, CancellationToken.None);

        Assert.True(completed);
        Assert.Contains(bus.Nodes, n => n.Uid.Value == Uid("uf-scn", "a"));
        Assert.Contains(bus.Nodes, n => n.Uid.Value == Uid("uf-scn", "b"));
    }
```

Check `ITestExecutionFilter`'s member list first with `grep -rn "interface ITestExecutionFilter" -A5` against the decompiled platform or the package XML docs; if it declares members, implement them on `UnknownFilter` as no-ops.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: FAIL with `ArgumentException: Unsupported execution filter type 'Raun.Mtp.Test.RunLoopTests+UnknownFilter'`.

- [ ] **Step 3: Make it degrade instead of throw**

Replace `ReadSelector` in `src/Raun.Mtp/RaunTestFramework.cs`:

```csharp
    /// <summary>
    /// Reduces an MTP execution filter to the <see cref="NodeSelector"/> that decides which steps the
    /// request selects, or <see langword="null"/> to select everything. A
    /// <see cref="TestNodeUidListFilter"/> carries an explicit uid list, a
    /// <see cref="TreeNodeFilter"/> a path expression, and a null or no-op filter means "run all".
    /// A filter type Raun does not recognize selects everything and names itself in
    /// <paramref name="unsupported"/>: over-selecting is recoverable and visible, whereas aborting
    /// would turn a future platform filter into a failed run the user cannot act on.
    /// </summary>
    private static NodeSelector? ReadSelector(ITestExecutionFilter? filter, out string? unsupported)
    {
        unsupported = null;
        switch (filter)
        {
            case null:
                return null;
#pragma warning disable TPEXP // NopFilter is an experimental Microsoft.Testing.Platform API.
            case NopFilter:
#pragma warning restore TPEXP
                return null;
            case TestNodeUidListFilter uidFilter:
                return new UidNodeSelector(
                    uidFilter.TestNodeUids.Select(u => u.Value).ToHashSet(StringComparer.OrdinalIgnoreCase));
            default:
                unsupported = filter.GetType().FullName;
                return null;
        }
    }
```

In both `OnDiscoverAsync` and `OnExecuteAsync`, take the new out parameter and log it. Add this private helper and call it right after `ReadSelector` in each:

```csharp
    /// <summary>Warns that a filter type was ignored, through the platform logger when there is one.</summary>
    private async Task WarnUnsupportedFilterAsync(string? unsupported)
    {
        if (unsupported is null)
        {
            return;
        }

        var message = $"ignoring unsupported execution filter type '{unsupported}'; selecting every test";
        ILogger? logger = _services?.GetLoggerFactory().CreateLogger(typeof(RaunTestFramework).FullName!);
        if (logger is not null)
        {
            await logger.LogWarningAsync(message).ConfigureAwait(false);
        }

        NodeDiagnostics.Log("unsupported-filter", message);
    }
```

Call sites become `var selector = ReadSelector(filter, out var unsupported); await WarnUnsupportedFilterAsync(unsupported).ConfigureAwait(false);`.

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.

- [ ] **Step 5: Commit**

```bash
jj commit -m "fix(mtp): an unrecognized execution filter selects everything and warns" -m "The filter switch threw ArgumentException for any type it did not know, so a filter a future platform version hands over would fail the run outright for something the user did nothing wrong to trigger. Over-selecting is recoverable and the warning names the type; aborting is not."
```

---

### Task 3: Node paths and filterable properties

**Files:**
- Create: `src/Raun.Mtp/Filtering/ScenarioNodePath.cs`
- Create: `test/Raun.Mtp.Test/ScenarioNodePathTests.cs`

**Interfaces:**
- Produces: `internal static class ScenarioNodePath` with `string For(ScenarioDefinition definition, ScenarioNode step)` and `PropertyBag Properties(ScenarioDefinition definition, ScenarioNode step)`.

**Context the implementer needs:** `ScenarioTestIdentity` (same folder's parent) already derives namespace and type from `ScenarioDefinition.MethodName`, which the generator emits as `{namespace}.{typeSimpleName}.{methodSimpleName}` with an empty namespace for a top-level type. Read its `Split` helper and reuse it — make it `internal static` if it is private, rather than writing a second splitter. The class segment prefers `ClassDisplayName` when set, exactly as the identity property does.

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Mtp.Test/ScenarioNodePathTests.cs`:

```csharp
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// A step's tree-filter path is /assembly/namespace/class/scenario/step, each segment URL-encoded
/// because the platform's contract is an encoded path and a display name may contain a slash.
/// </summary>
public class ScenarioNodePathTests
{
    private static ScenarioNode Node(string template, string phase = "Given") => new()
    {
        Index = 0,
        StepId = "s0",
        Phase = phase,
        OperationName = "Op",
        DisplayNameTemplate = template,
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(
        string methodName, string displayName, string? classDisplayName, ScenarioNode step) => new()
    {
        ScenarioId = "scn",
        DisplayName = displayName,
        MethodName = methodName,
        ClassDisplayName = classDisplayName,
        Nodes = [step],
    };

    [Fact]
    public void The_path_has_five_segments_and_starts_with_a_slash()
    {
        var step = Node("patient Jane exists");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var path = ScenarioNodePath.For(definition, step);

        Assert.StartsWith("/", path, StringComparison.Ordinal);
        var segments = path.Split('/');
        Assert.Equal(6, segments.Length);          // leading empty + five segments
        Assert.Equal(string.Empty, segments[0]);
        Assert.Equal("Demo", segments[2]);
        Assert.Equal("Booking", segments[3]);
        Assert.Equal("customer%20books", segments[4]);
        Assert.Equal("patient%20Jane%20exists", segments[5]);
    }

    [Fact]
    public void A_class_display_name_overrides_the_derived_type()
    {
        var step = Node("a step");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", "Appointment booking", step);

        Assert.Equal("Appointment%20booking", ScenarioNodePath.For(definition, step).Split('/')[3]);
    }

    [Fact]
    public void A_scenario_in_the_global_namespace_still_has_five_segments()
    {
        var step = Node("a step");
        var definition = Definition("Booking.CustomerBooks", "customer books", null, step);

        var segments = ScenarioNodePath.For(definition, step).Split('/');
        Assert.Equal(6, segments.Length);
        Assert.Equal(string.Empty, segments[2]);   // empty namespace segment, never a missing one
        Assert.Equal("Booking", segments[3]);
    }

    [Fact]
    public void A_slash_in_a_display_name_is_encoded_rather_than_splitting_the_path()
    {
        var step = Node("reads a/b");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var path = ScenarioNodePath.For(definition, step);

        Assert.Equal(6, path.Split('/').Length);
        Assert.Contains("a%2Fb", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Properties_carry_the_phase_and_the_scenario_name()
    {
        var step = Node("a step", phase: "When");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var bag = ScenarioNodePath.Properties(definition, step);
        var metadata = bag.OfType<TestMetadataProperty>().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        Assert.Equal("When", metadata["Phase"]);
        Assert.Equal("customer books", metadata["Scenario"]);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build error — `ScenarioNodePath` does not exist.

- [ ] **Step 3: Write the path builder**

Create `src/Raun.Mtp/Filtering/ScenarioNodePath.cs`:

```csharp
using System.Reflection;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// The address a step presents to <c>--treenode-filter</c>:
/// <c>/{assembly}/{namespace}/{class}/{scenario}/{step}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Five segments rather than the conventional four because in Raun a <em>step</em> is a test node:
/// <c>/*/*/*/booking/*</c> selects a scenario and <c>/*/*/*/booking/*reminder*</c> one step inside it.
/// The segments come from the same derivation <see cref="ScenarioTestIdentity"/> uses, so filtering
/// and IDE grouping agree.
/// </para>
/// <para>
/// Every segment is URL-encoded: the platform documents the path as segment-encoded, and a display
/// name containing <c>/</c> would otherwise split into extra segments — the platform rejects a slash
/// inside a filter segment outright. The step segment is the step's own name without the numbering
/// prefix discovery adds, because the number is positional: a filter keyed on it would silently
/// select a different step once one is inserted above it.
/// </para>
/// </remarks>
internal static class ScenarioNodePath
{
    /// <summary>Property key for a step's phase marker (Given/When/Then, or a custom marker).</summary>
    private const string PhaseKey = "Phase";

    /// <summary>Property key for the owning scenario's display name.</summary>
    private const string ScenarioKey = "Scenario";

    private static readonly string AssemblyName =
        Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;

    /// <summary>Builds the encoded five-segment path for one step.</summary>
    public static string For(ScenarioDefinition definition, ScenarioNode step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        ScenarioTestIdentity.Split(definition.MethodName, out var @namespace, out var typeName, out _);
        var type = string.IsNullOrEmpty(definition.ClassDisplayName) ? typeName : definition.ClassDisplayName!;

        return string.Concat(
            "/", Uri.EscapeDataString(AssemblyName),
            "/", Uri.EscapeDataString(@namespace),
            "/", Uri.EscapeDataString(type),
            "/", Uri.EscapeDataString(definition.DisplayName),
            "/", Uri.EscapeDataString(step.DisplayNameTemplate));
    }

    /// <summary>
    /// The properties a filter's <c>[key=value]</c> expression can match. The platform inspects only
    /// <see cref="TestMetadataProperty"/>, and never reads the bag at all when no segment carries a
    /// property expression.
    /// </summary>
    public static PropertyBag Properties(ScenarioDefinition definition, ScenarioNode step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        return new PropertyBag(
            new TestMetadataProperty(PhaseKey, step.Phase),
            new TestMetadataProperty(ScenarioKey, definition.DisplayName));
    }
}
```

If `ScenarioTestIdentity.Split` is `private`, change it to `internal static` and leave its doc comment alone. If `PropertyBag`'s constructor or `TestMetadataProperty`'s shape differs from the above, follow what the compiler reports — check `MtpReportSink.cs` for how the repo already builds properties.

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.

Note on the assembly segment: under `dotnet test` the entry assembly is the test app, so the segment is the sample's own name. The tests above never assert on it for that reason.

- [ ] **Step 5: Commit**

```bash
jj commit -m "feat(mtp): steps expose a tree-filter path and filterable properties" -m "Five segments (/assembly/namespace/class/scenario/step) because a step is a test node in Raun, so a filter can address one step or a whole scenario. Segments are URL-encoded per the platform's contract, which also stops a display name containing a slash from splitting the path. Phase and Scenario ride along as metadata so [key=value] expressions work without inventing traits."
```

---

### Task 4: The tree-node selector, and registering the service

**Files:**
- Create: `src/Raun.Mtp/Filtering/TreeNodeSelector.cs`
- Create: `src/Raun.Mtp/RaunExtension.cs`
- Modify: `src/Raun.Mtp/RaunTestFramework.cs` (`ReadSelector` gains the `TreeNodeFilter` case)
- Modify: `src/Raun.Mtp/RaunTestApplication.cs` (register the service)
- Modify: `test/Raun.Mtp.Test/NodeSelectorTests.cs`

**Interfaces:**
- Consumes: `NodeSelector` (Task 1), `ScenarioNodePath` (Task 3).
- Produces: `internal sealed class TreeNodeSelector(TreeNodeFilter filter) : NodeSelector`; `internal sealed class RaunExtension : IExtension` with `Uid = "raun.mtp"`.

**Why the test cannot construct a `TreeNodeFilter`:** its constructor is `internal` to the platform, so the only way to obtain one is from the platform itself. The selector is therefore tested through the real command line in Task 5's end-to-end test, and unit-tested here only for the parts that do not need a filter instance. Do not add a reflection hack to construct one.

- [ ] **Step 1: Write the selector**

Create `src/Raun.Mtp/Filtering/TreeNodeSelector.cs`:

```csharp
using Microsoft.Testing.Platform.Requests;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// Adapts the platform's <c>--treenode-filter</c> to Raun's selection: each step is offered as its
/// encoded path plus the properties a <c>[key=value]</c> expression can match.
/// </summary>
#pragma warning disable TPEXP // TreeNodeFilter is an experimental Microsoft.Testing.Platform API.
internal sealed class TreeNodeSelector : NodeSelector
{
    private readonly TreeNodeFilter _filter;

    public TreeNodeSelector(TreeNodeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter;
    }

    public override bool Matches(ScenarioDefinition definition, ScenarioNode step)
        => _filter.MatchesFilter(
            ScenarioNodePath.For(definition, step),
            ScenarioNodePath.Properties(definition, step));
}
#pragma warning restore TPEXP
```

- [ ] **Step 2: Teach the framework about the filter**

In `src/Raun.Mtp/RaunTestFramework.cs`, add a case to `ReadSelector` above the `default`:

```csharp
#pragma warning disable TPEXP // TreeNodeFilter is an experimental Microsoft.Testing.Platform API.
            case TreeNodeFilter treeFilter:
                return new TreeNodeSelector(treeFilter);
#pragma warning restore TPEXP
```

Add `using Microsoft.Testing.Platform.Requests;` if it is not already imported.

- [ ] **Step 3: Give Raun an extension identity**

Create `src/Raun.Mtp/RaunExtension.cs`:

```csharp
using System.Reflection;
using Microsoft.Testing.Platform.Extensions;

namespace Raun.Mtp;

/// <summary>
/// Raun's identity as the owner of the platform services it registers (the tree-node filter and the
/// maximum-failed-tests option). The platform shows it in <c>--info</c> and in error messages.
/// </summary>
internal sealed class RaunExtension : IExtension
{
    /// <summary>Stable uid; a literal on purpose, so renaming the class cannot silently change it.</summary>
    public string Uid => "raun.mtp";

    public string Version =>
        typeof(RaunExtension).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion is { Length: > 0 } informational
            ? informational.Split('+')[0]
            : "1.0.0";

    public string DisplayName => "Raun";

    public string Description => "Raun scenario test framework for Microsoft.Testing.Platform";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);
}
```

- [ ] **Step 4: Register the service**

In `src/Raun.Mtp/RaunTestApplication.cs`, before `RegisterTestFramework`:

```csharp
        // Opt into the platform's own filter rather than inventing a Raun dialect: this registers
        // --treenode-filter, and the framework receives the parsed TreeNodeFilter in the request.
        var extension = new RaunExtension();
#pragma warning disable TPEXP // AddTreeNodeFilterService is an experimental Microsoft.Testing.Platform API.
        builder.AddTreeNodeFilterService(extension);
#pragma warning restore TPEXP
```

Add `using Microsoft.Testing.Platform.Helpers;`.

- [ ] **Step 5: Verify the option appears and works end to end**

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --help 2>&1 | grep -E "^    --treenode-filter"`
Expected: the option is listed.

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --treenode-filter '/*/*/*/*/*' 2>&1 | tail -6`
Expected: the same totals as an unfiltered run (`52 total / 51 passed / 1 skipped`).

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --list-tests 2>&1 | head -20` to read a real scenario display name, then filter to it:
`dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --treenode-filter '/*/*/*/<that scenario>/*' 2>&1 | tail -6`
Expected: fewer tests than the full run, and all of them from that scenario. Record both numbers in your report.

- [ ] **Step 6: Run the tests and the build**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.

- [ ] **Step 7: Commit**

```bash
jj commit -m "feat(mtp): register the platform's tree-node filter service" -m "--treenode-filter is a public opt-in on the builder that Raun never called, which is the whole reason users had no expression filter; the platform has no --filter of its own (that belongs to the VSTest bridge). Registering the service and adapting TreeNodeFilter to the node selector gives Raun the same filter syntax every other native MTP framework has."
```

---

### Task 5: Graceful stop and `--maximum-failed-tests`

**Files:**
- Create: `src/Raun.Mtp/Capabilities.cs`
- Create: `test/Raun.Mtp.Test/CapabilitiesTests.cs`
- Modify: `src/Raun.Mtp/RaunRunLoop.cs` (ctor parameter, admission guard)
- Modify: `src/Raun.Mtp/RaunTestFramework.cs` (ctor parameter, pass to the loop)
- Modify: `src/Raun.Mtp/RaunTestApplication.cs` (capabilities, service registration)

**Interfaces:**
- Produces: `internal sealed class RunStopSignal` with `void Request()` and `bool IsStopRequested`; `internal sealed class RaunGracefulStopCapability(RunStopSignal signal) : IGracefulStopTestExecutionCapability`; `internal sealed class RaunBannerCapability : IBannerMessageOwnerCapability`. `RaunRunLoop`'s constructor gains `RunStopSignal? stopSignal = null` as its last parameter; `RaunTestFramework` gains a ctor overload taking one.

- [ ] **Step 1: Write the failing tests**

Create `test/Raun.Mtp.Test/CapabilitiesTests.cs`:

```csharp
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// The capabilities Raun declares to the platform: a graceful stop (which is what unlocks
/// --maximum-failed-tests) and the banner.
/// </summary>
public class CapabilitiesTests
{
    [Fact]
    public void A_fresh_stop_signal_is_not_requested()
        => Assert.False(new RunStopSignal().IsStopRequested);

    [Fact]
    public async Task The_graceful_stop_capability_requests_the_stop()
    {
        var signal = new RunStopSignal();
        var capability = new RaunGracefulStopCapability(signal);

        await capability.StopTestExecutionAsync(CancellationToken.None);

        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public void Requesting_a_stop_twice_is_harmless()
    {
        var signal = new RunStopSignal();
        signal.Request();
        signal.Request();

        Assert.True(signal.IsStopRequested);
    }

    [Fact]
    public async Task The_banner_names_Raun()
    {
        var banner = await new RaunBannerCapability().GetBannerMessageAsync();

        Assert.NotNull(banner);
        Assert.Contains("Raun", banner, StringComparison.Ordinal);
    }
}
```

Add to `test/Raun.Mtp.Test/RunLoopTests.cs`:

```csharp
    [Fact]
    public async Task A_requested_stop_stops_admitting_scenarios_and_drains_what_is_running()
    {
        // Graceful means graceful: nothing in flight is killed, nothing new starts, and the run
        // still reports. This is the same halt path a faulted scenario uses.
        var signal = new RunStopSignal();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();
        var sync = new object();

        ScenarioDefinition Def(string id, bool stops) => Definition(id, id,
            Node(0, "a", "a", invoke: async (_, _) =>
            {
                lock (sync) { started.Add(id); }
                if (stops)
                {
                    signal.Request();
                    release.TrySetResult();
                }

                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return null;
            }));

        var definitions = new[] { Def("one", stops: true), Def("two", false), Def("three", false), Def("four", false) };
        var sink = new RecordingSink();

        await new RaunRunLoop(() => definitions, maxParallelScenarios: 1, stopSignal: signal)
            .RunAsync(selector: null, sink, CancellationToken.None);

        lock (sync)
        {
            Assert.Equal(["one"], started);
        }

        Assert.Single(sink.Events.OfType<ScenarioFinished>());
        Assert.Single(sink.Events.OfType<RunFinished>());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -15`
Expected: build errors — `RunStopSignal`, `RaunGracefulStopCapability`, `RaunBannerCapability` do not exist, and `RaunRunLoop` has no `stopSignal` parameter.

- [ ] **Step 3: Write the capabilities**

Create `src/Raun.Mtp/Capabilities.cs`:

```csharp
using System.Reflection;
using Microsoft.Testing.Platform.Capabilities.TestFramework;

namespace Raun.Mtp;

/// <summary>
/// A run-scoped "stop admitting work" flag. The platform asks for a graceful stop (today when
/// <c>--maximum-failed-tests</c> is crossed) and the run loop honours it at its admission scan, so
/// nothing in flight is killed. Created in the bootstrap because the capability and the framework are
/// built by different factories and need to meet.
/// </summary>
internal sealed class RunStopSignal
{
    private int _requested;

    /// <summary>True once a stop has been asked for; never returns to false within a run.</summary>
    public bool IsStopRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Asks the run to stop admitting scenarios. Idempotent.</summary>
    public void Request() => Interlocked.Exchange(ref _requested, 1);
}

/// <summary>
/// Declares that Raun can stop gracefully, which is what makes the platform offer
/// <c>--maximum-failed-tests</c>: its option provider is enabled only for a framework with this
/// capability.
/// </summary>
#pragma warning disable TPEXP // IGracefulStopTestExecutionCapability is an experimental Microsoft.Testing.Platform API.
internal sealed class RaunGracefulStopCapability : IGracefulStopTestExecutionCapability
{
    private readonly RunStopSignal _signal;

    public RaunGracefulStopCapability(RunStopSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        _signal = signal;
    }

    /// <summary>Stops admitting new scenarios; in-flight ones run to completion and still report.</summary>
    public Task StopTestExecutionAsync(CancellationToken cancellationToken)
    {
        _signal.Request();
        return Task.CompletedTask;
    }
}

/// <summary>Names Raun and its version in the platform's banner instead of the platform's default.</summary>
internal sealed class RaunBannerCapability : IBannerMessageOwnerCapability
{
    public Task<string?> GetBannerMessageAsync()
    {
        var version = typeof(RaunBannerCapability).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var trimmed = string.IsNullOrEmpty(version) ? "0.0.0" : version!.Split('+')[0];
        return Task.FromResult<string?>($"Raun v{trimmed}");
    }
}
#pragma warning restore TPEXP
```

- [ ] **Step 4: Honour the signal in the launcher**

In `src/Raun.Mtp/RaunRunLoop.cs`, add a `RunStopSignal? stopSignal = null` parameter to the constructor (last, after `maxParallelScenarios`), document it, and store it in a field:

```csharp
    /// <param name="stopSignal">
    /// Set by the platform's graceful-stop capability. When a stop is requested the launcher stops
    /// admitting scenarios; whatever is running drains and reports, exactly as it does when a
    /// scenario faults. <see langword="null"/> (the default) means no stop can be requested.
    /// </param>
```

In `LaunchAsync`, extend the admission guard so a requested stop halts admission the same way cancellation and a fault do:

```csharp
                if ((started && cancellationToken.IsCancellationRequested) || halted || stopSignal?.IsStopRequested == true)
                {
                    break;
                }
```

- [ ] **Step 5: Thread it through the framework and the bootstrap**

In `src/Raun.Mtp/RaunTestFramework.cs`, add a `RunStopSignal? _stopSignal` field and a ctor overload taking it after `maxParallelScenarios`, chaining to the existing one, with a doc comment. Pass it when constructing the loop in `OnExecuteAsync`: `stopSignal: _stopSignal`.

In `src/Raun.Mtp/RaunTestApplication.cs`, create the signal, register the service, and declare both capabilities:

```csharp
        var stopSignal = new RunStopSignal();

        // --maximum-failed-tests is offered only to a framework that declares a graceful stop.
#pragma warning disable TPEXP // AddMaximumFailedTestsService is an experimental Microsoft.Testing.Platform API.
        builder.AddMaximumFailedTestsService(extension);
#pragma warning restore TPEXP

        builder.RegisterTestFramework(
            _ => new TestFrameworkCapabilities(
                new RaunBannerCapability(),
                new RaunGracefulStopCapability(stopSignal)),
            (_, serviceProvider) => new RaunTestFramework(
                serviceProvider, simulateTime, services, preflight, maxParallelScenarios, stopSignal));
```

- [ ] **Step 6: Verify the option appears and the tests pass**

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --help 2>&1 | grep -E "^    --maximum-failed-tests"` — expected: listed.
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj 2>&1 | head -3` — expected: the banner names Raun.
Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj 2>&1 | tail -5` — expected: all passed.
Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`.

- [ ] **Step 7: Commit**

```bash
jj commit -m "feat(mtp): graceful stop, --maximum-failed-tests, and a Raun banner" -m "The platform offers --maximum-failed-tests only to a framework declaring IGracefulStopTestExecutionCapability, so Raun never had it. Stopping sets the flag the concurrent launcher already uses for a faulted scenario: nothing in flight is killed, in-flight scenarios drain, and the run still publishes RunFinished. The banner capability names Raun rather than only the platform."
```

---

### Task 6: Move the platform pin to 2.4.0

**Files:**
- Modify: `Directory.Packages.props`

**Context:** the newer platform adds `--progress`, `--ansi`, `--output minimal`, `--show-slowest-tests`, `--show-flaky-tests` and `--show-test-results`, none of which need code. `Microsoft.Testing.Extensions.CodeCoverage` must move with it: AGENTS.md records that a mismatch shows up as a `TypeLoadException` *after* tests pass, and the Aspire sample is the only project referencing coverage, so it is the one that proves the pairing.

- [ ] **Step 1: Find the coverage version that matches**

Run: `dotnet package search Microsoft.Testing.Extensions.CodeCoverage --exact-match --format json 2>/dev/null | tail -20`
Pick the newest stable version. Record which one you chose and why in your report.

- [ ] **Step 2: Bump both pins**

In `Directory.Packages.props`, change the platform pin to `2.4.0` and the coverage pin to the version from Step 1. Update the comment above the platform pin so it no longer describes 2.2.3 as current.

- [ ] **Step 3: Verify the whole solution and both samples**

Run: `dotnet build Raun.slnx 2>&1 | tail -3` — expected: `0 Warning(s)`, `0 Error(s)`.
Run: `dotnet test Raun.slnx 2>&1 | tail -6` — expected: all green, one pre-existing skip.
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj 2>&1 | tail -6` — expected: `52 total / 51 passed / 1 skipped`.
Run: `dotnet run --project samples/AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj 2>&1 | tail -6` — expected: `13 total / 13 passed`, **and no `TypeLoadException` anywhere in the output**. Grep the full output for `TypeLoadException` explicitly and say so in your report; this sample starts a real Aspire AppHost, so allow up to ten minutes.
Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --help 2>&1 | grep -E "^    --(progress|ansi|show-slowest-tests|show-flaky-tests|show-test-results)"` — expected: all five listed.

If the Aspire sample throws `TypeLoadException`, the coverage pin is wrong for this platform version: try the next version and repeat. If no coverage version works with 2.4.0, stop and report BLOCKED with both versions tried and the exact exception — do not disable coverage in the sample to get green.

- [ ] **Step 4: Commit**

```bash
jj commit -m "chore(deps): move the testing platform pin to 2.4.0" -m "Brings the terminal options the platform added since the old pin (--progress, --ansi, --output minimal, --show-slowest-tests, --show-flaky-tests, --show-test-results) with no code change. The code coverage extension moves in lockstep because a mismatch surfaces as a TypeLoadException after tests pass; the Aspire sample is the only project referencing it and proves the pairing."
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Correct and extend the README**

In `README.md`'s "Run it" section, after the existing `dotnet test` / `--report-html` block, add:

~~~markdown
### Selecting what to run

Raun registers the platform's own tree filter, so the syntax is the one every Microsoft.Testing.Platform
framework uses. A step is a test node in Raun, so paths have five segments:

```
/{assembly}/{namespace}/{class}/{scenario}/{step}
```

```bash
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/customer books/*'    # one scenario
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/*/*reminder*'        # steps by name
dotnet run --project MyScenarios -- --treenode-filter '/*/*/*/*/*[Phase=When]'     # by phase
```

Selecting a step runs everything it needs — its dependencies, merge sources, guard conditions, and
teardown — and nothing after it, exactly as selecting one step in an IDE does. `--filter-uid` takes
node uids and is what an IDE sends when you run a single test.

There is no `--filter`: that option belongs to the VSTest bridge that xUnit and MSTest use for
VSTest compatibility, not to the platform itself.
~~~

Also mention `--maximum-failed-tests` in the same section, one line: it stops Raun admitting new
scenarios once the threshold is crossed, while scenarios already running finish and report.

- [ ] **Step 2: Add the AGENTS.md notes**

Under "Things that look like bugs but are not", add:

```markdown
- There is no `--filter`. The platform never had one — it belongs to `Microsoft.Testing.Extensions.VSTestBridge`,
  which xUnit and MSTest use for VSTest compatibility. Raun registers the platform's own
  `--treenode-filter` instead, plus `--filter-uid`, which is what an IDE sends for a single test.
- A `--treenode-filter` path has five segments, not the conventional four, because a step is a test
  node: `/{assembly}/{namespace}/{class}/{scenario}/{step}`. Segments are URL-encoded, so a display
  name containing `/` still occupies exactly one segment.
- `--maximum-failed-tests` stops Raun launching new scenarios; scenarios already running finish and
  report. Nothing is killed mid-flight, so the number of failures can exceed the threshold by
  whatever was already in the air.
```

- [ ] **Step 3: Verify the docs match the code**

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --help 2>&1 | grep -E "^    --(treenode-filter|filter-uid|maximum-failed-tests)"` — expected: all three listed, matching what the README claims.
Run the three README filter examples against the sample, substituting a real scenario name from `--list-tests`, and confirm each selects a subset rather than erroring. Record the totals in your report.

- [ ] **Step 4: Commit**

```bash
jj commit -m "docs: how to select what to run" -m "Documents --treenode-filter's five-segment paths and property expressions, --filter-uid, and --maximum-failed-tests, and corrects the record that Raun was missing a --filter the platform never had."
```

---

## Self-Review

**Spec coverage.** Every section of the spec maps to a task: the platform filter and node paths to Tasks 3 and 4, the two filterable properties to Task 3, the selector abstraction to Task 1, the degrade-instead-of-throw to Task 2, graceful stop and the banner to Task 5, the pin bump to Task 6, the docs to Task 7. Nothing in the spec is unimplemented.

**Type consistency.** `NodeSelector.Matches(ScenarioDefinition, ScenarioNode)` is defined in Task 1 and used with that exact shape in Tasks 3 and 4. `ScenarioNodePath.For` / `.Properties` are defined in Task 3 and consumed in Task 4. `RunStopSignal.Request()` / `.IsStopRequested` are defined and consumed within Task 5. `RaunRunLoop`'s new `stopSignal` parameter is introduced in Task 5 and used in that task's test.

**Known soft spots the implementer must resolve rather than guess.** `ScenarioTestIdentity.Split` may be private and need widening to `internal` (Task 3 says so). `PropertyBag`'s constructor shape should be confirmed against `MtpReportSink.cs` rather than assumed (Task 3 says so). `ITestExecutionFilter` may declare members the fake filter must implement (Task 2 says so). The coverage version for platform 2.4.0 is a lookup with a stated fallback and a stated blocking condition (Task 6).
