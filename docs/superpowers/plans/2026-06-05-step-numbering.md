# Step Numbering + Scenario-Name Grouping (Raun.Mtp) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Number each scenario's step leaves so VS Test Explorer (which sorts siblings lexically) shows them in execution order, and surface the human scenario name as the method-level grouping node.

**Architecture:** A new pure helper `ScenarioStepNumbering` computes an `Index → label` map per scenario (standalone steps take the next integer; a parallel/array group takes one integer with sub-numbered members; numbers are zero-padded to a per-scenario width so lexical sort = numeric order). Discovery (`RaunDiscoverer`) and execution (`RaunStepReporter`) both build their leaf text from that single source, dropping the old `"{scenario} ▸ {step}"` prefix. `ScenarioTestIdentity.Create` gains a second parameter so the method identity carries the scenario display name while namespace/type still derive from the FQN.

**Tech Stack:** C# / .NET, Microsoft.Testing.Platform (MTP) 1.9.1, xUnit.v3 for the unit tests, `jj` for version control. Test command: `dotnet test Raun.slnx` (never `--nologo` — MTP rejects it and reports "Zero tests ran").

**Spec:** `docs/superpowers/specs/2026-06-05-step-numbering-design.md` (approved). **Handoff:** `docs/superpowers/handoffs/2026-06-05-step-numbering-handoff.md`.

---

## File Structure

**New files:**
- `src/Raun.Mtp/ScenarioStepNumbering.cs` — pure `Compute(ScenarioDefinition) → IReadOnlyDictionary<int,string>` plus a `Format(labels, node, stepText)` leaf-text composer shared by both call sites (keeps R7 — discovery/execution agreement — DRY).
- `test/Raun.Mtp.Test/ScenarioStepNumberingTests.cs` — unit tests for the numbering + formatting.

**Modified files:**
- `src/Raun.Mtp/ScenarioTestIdentity.cs` — `Create` takes `(methodFullName, scenarioDisplayName)`; method identity = scenario name.
- `src/Raun.Mtp/RaunDiscoverer.cs` — compose numbered leaf text; drop the `DisplayNameSeparator` prefix; pass display name into `Create`.
- `src/Raun.Mtp/RaunStepReporter.cs` — compute the label map once; compose numbered leaf text; pass display name into `Create`.
- `test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs` — `Create` now maps the display name onto `MethodName`.
- `test/Raun.Mtp.Test/RaunDiscovererTests.cs` — numbered leaf text, identity method = scenario name.
- `test/Raun.Mtp.Test/RaunStepReporterTests.cs` — numbered leaf text, no prefix.
- `test/Raun.Mtp.Test/DiscoveryRequestTests.cs` — one display-name assertion updated to the numbered form.
- `samples/AppointmentTests/AppointmentDsl.cs` — revert the debug `await Task.Delay(5000)` (Task 1, before the baseline commit).

**Decision — keep `NodeDiagnostics`:** the env-gated (`RAUN_NODE_DEBUG`) tracer is zero-cost when off, already proved its worth diagnosing the grouping collapse, and this change puts spaces in the method identity (the bridge managed-name path the handoff flags as fragile). Its `Log()` calls are interleaved into the same three files the feature touches, so splitting it out is not cleanly separable. It rides in the baseline commit (Task 1).

---

## Task 1: Baseline cleanup & commit

The `jj` working copy `@` (no description) bundles all pre-feature work — the scenario-method identity fix, `NodeDiagnostics`, the `StepContext`/async-observer refactor, the two handoff docs, the `Directory.Packages.props` bump, and one debug artifact. Revert the artifact, confirm green, then land it as the baseline so the feature work starts from a clean `@`.

**Files:**
- Modify: `samples/AppointmentTests/AppointmentDsl.cs:61`

- [ ] **Step 1: Revert the debug delay**

In `samples/AppointmentTests/AppointmentDsl.cs`, the `ImportUsers` When-step has a debug delay. Remove the `await Task.Delay(5000);` line so the body is:

```csharp
        [StepName("importing the users")]
        public static async Task<ImportResult> ImportUsers(User[] users)
        {
            await Task.Yield();
            return new ImportResult(users.Length);
        }
```

- [ ] **Step 2: Verify the suite is green**

Run: `dotnet test Raun.slnx`
Expected: PASS — 143 passed, 0 failed (the sample now runs fast, no 5s stall).

- [ ] **Step 3: Commit the baseline**

```bash
jj describe -m "Raun.Mtp: scenario-method grouping identity + async StepContext observer + node diagnostics"
jj new
```

`jj describe` names the current working copy (which holds all the baseline edits); `jj new` opens a fresh empty `@` on top for the feature work.

---

## Task 2: `ScenarioStepNumbering` — Compute + Format

A pure, unit-testable helper mirroring the `ScenarioTestIdentity` precedent. No MTP types; lives in `Raun.Mtp` so the discoverer/reporter can share it.

**Files:**
- Create: `src/Raun.Mtp/ScenarioStepNumbering.cs`
- Test: `test/Raun.Mtp.Test/ScenarioStepNumberingTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `test/Raun.Mtp.Test/ScenarioStepNumberingTests.cs`:

```csharp
using Raun.Mtp;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// Unit tests for <see cref="ScenarioStepNumbering"/>: standalone steps take the next top-level
/// number; a parallel/array group (nodes sharing a GroupId) takes one top-level number with
/// sub-numbered members; numbers are zero-padded to a per-scenario width so a runner that sorts
/// sibling leaves lexically renders them in execution order.
/// </summary>
public class ScenarioStepNumberingTests
{
    static ScenarioNode Node(int index, string? group = null) => new()
    {
        Index = index,
        StepId = "s" + index,
        Phase = "Given",
        OperationName = "Op" + index,
        DisplayNameTemplate = "step " + index,
        DependsOn = [],
        GroupId = group,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn",
        DisplayName = "scenario",
        MethodName = "Ns.Scn",
        Nodes = nodes,
    };

    [Fact]
    public void Linear_scenario_numbers_each_step_sequentially()
    {
        var labels = ScenarioStepNumbering.Compute(Def(Node(0), Node(1), Node(2), Node(3)));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2", labels[1]);
        Assert.Equal("3", labels[2]);
        Assert.Equal("4", labels[3]);
    }

    [Fact]
    public void Single_step_is_numbered_one()
    {
        var labels = ScenarioStepNumbering.Compute(Def(Node(0)));

        Assert.Equal("1", Assert.Single(labels.Values));
    }

    [Fact]
    public void Tuple_group_consumes_one_top_level_number_with_sub_indices()
    {
        // standalone, group(g1) x2, standalone, standalone
        var labels = ScenarioStepNumbering.Compute(
            Def(Node(0), Node(1, "g1"), Node(2, "g1"), Node(3), Node(4)));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2.1", labels[1]);
        Assert.Equal("2.2", labels[2]);
        Assert.Equal("3", labels[3]);
        Assert.Equal("4", labels[4]);
    }

    [Fact]
    public void Group_at_start_takes_top_level_one()
    {
        // group(g0) x2 first, then two standalones
        var labels = ScenarioStepNumbering.Compute(
            Def(Node(0, "g0"), Node(1, "g0"), Node(2), Node(3)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("2", labels[2]);
        Assert.Equal("3", labels[3]);
    }

    [Fact]
    public void Array_group_of_three_sub_numbers_all_members()
    {
        var labels = ScenarioStepNumbering.Compute(
            Def(Node(0, "g0"), Node(1, "g0"), Node(2, "g0"), Node(3), Node(4)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("1.3", labels[2]);
        Assert.Equal("2", labels[3]);
        Assert.Equal("3", labels[4]);
    }

    [Fact]
    public void Two_groups_in_one_scenario_each_take_their_own_top_level_number()
    {
        var labels = ScenarioStepNumbering.Compute(
            Def(Node(0, "ga"), Node(1, "ga"), Node(2), Node(3, "gb"), Node(4, "gb"), Node(5)));

        Assert.Equal("1.1", labels[0]);
        Assert.Equal("1.2", labels[1]);
        Assert.Equal("2", labels[2]);
        Assert.Equal("3.1", labels[3]);
        Assert.Equal("3.2", labels[4]);
        Assert.Equal("4", labels[5]);
    }

    [Fact]
    public void Ten_or_more_top_level_steps_zero_pad_the_number()
    {
        var nodes = new ScenarioNode[12];
        for (var i = 0; i < 12; i++)
        {
            nodes[i] = Node(i);
        }

        var labels = ScenarioStepNumbering.Compute(Def(nodes));

        Assert.Equal("01", labels[0]);
        Assert.Equal("09", labels[8]);
        Assert.Equal("10", labels[9]);
        Assert.Equal("12", labels[11]);
    }

    [Fact]
    public void Group_with_ten_or_more_members_zero_pads_the_sub_index()
    {
        // one standalone, then a group of 10 — top-level stays width 1, sub-index pads to width 2.
        var nodes = new ScenarioNode[11];
        nodes[0] = Node(0);
        for (var i = 1; i < 11; i++)
        {
            nodes[i] = Node(i, "g");
        }

        var labels = ScenarioStepNumbering.Compute(Def(nodes));

        Assert.Equal("1", labels[0]);
        Assert.Equal("2.01", labels[1]);
        Assert.Equal("2.09", labels[9]);
        Assert.Equal("2.10", labels[10]);
    }

    [Fact]
    public void Labels_sort_lexically_into_execution_order()
    {
        // 12 top-level numbers where #2 is a group of 11 members (the spec's worked example):
        // index 0 standalone; indices 1..11 group "g"; indices 12..21 standalone.
        var nodes = new ScenarioNode[22];
        nodes[0] = Node(0);
        for (var i = 1; i <= 11; i++)
        {
            nodes[i] = Node(i, "g");
        }

        for (var i = 12; i < 22; i++)
        {
            nodes[i] = Node(i);
        }

        var labels = ScenarioStepNumbering.Compute(Def(nodes));

        var inIndexOrder = labels.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        var inLexicalOrder = labels.Values.OrderBy(v => v, StringComparer.Ordinal).ToList();

        Assert.Equal(inIndexOrder, inLexicalOrder);
    }

    [Fact]
    public void Format_standalone_step_uses_trailing_dot()
    {
        var def = Def(Node(0));
        var labels = ScenarioStepNumbering.Compute(def);

        Assert.Equal("1. the database is clean",
            ScenarioStepNumbering.Format(labels, def.Nodes[0], "the database is clean"));
    }

    [Fact]
    public void Format_group_member_omits_the_trailing_dot()
    {
        var def = Def(Node(0), Node(1, "g1"), Node(2, "g1"));
        var labels = ScenarioStepNumbering.Compute(def);

        Assert.Equal("2.1 patient Jane exists",
            ScenarioStepNumbering.Format(labels, def.Nodes[1], "patient Jane exists"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Raun.slnx`
Expected: FAIL — compile error, `ScenarioStepNumbering` does not exist.

- [ ] **Step 3: Implement `ScenarioStepNumbering`**

Create `src/Raun.Mtp/ScenarioStepNumbering.cs`:

```csharp
using System.Globalization;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// Computes per-step display labels for a scenario so a runner that sorts sibling leaves
/// lexically (VS Test Explorer) renders them in execution order. A standalone step takes the next
/// top-level number; a parallel/array group (consecutive nodes sharing a non-null
/// <see cref="ScenarioNode.GroupId"/>) takes one top-level number with sub-numbered members. Both
/// the number and the sub-index are zero-padded to a per-scenario width so lexical order equals
/// numeric order (≤9 steps render <c>1</c>–<c>9</c>; ≥10 render <c>01</c>…). Pure and runner-free,
/// so it is unit-testable and shared by discovery and execution.
/// </summary>
internal static class ScenarioStepNumbering
{
    /// <summary>
    /// Maps each <see cref="ScenarioNode.Index"/> to its label (e.g. <c>"1"</c>, <c>"2.1"</c>, or
    /// zero-padded <c>"02.01"</c> for large scenarios). Group membership is resolved by first
    /// encounter so the result is robust to a non-contiguous group.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Compute(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var topLevelByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var subCounterByGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var assignments = new List<(int Index, int Top, int Sub)>(definition.Nodes.Count);
        var nextTop = 0;
        var maxGroupSize = 1;

        foreach (var node in definition.Nodes.OrderBy(n => n.Index))
        {
            if (node.GroupId is null)
            {
                // Standalone step: consume the next top-level number (sub 0 = no sub-index).
                assignments.Add((node.Index, ++nextTop, 0));
            }
            else if (!topLevelByGroup.TryGetValue(node.GroupId, out var top))
            {
                // First member of a group: consume one top-level number, start its sub-counter at 1.
                topLevelByGroup[node.GroupId] = ++nextTop;
                subCounterByGroup[node.GroupId] = 1;
                assignments.Add((node.Index, nextTop, 1));
            }
            else
            {
                // Later member: reuse the group's top-level number, advance its sub-counter.
                var sub = ++subCounterByGroup[node.GroupId];
                assignments.Add((node.Index, top, sub));
                if (sub > maxGroupSize)
                {
                    maxGroupSize = sub;
                }
            }
        }

        var topWidth = Digits(nextTop);
        var subWidth = Digits(maxGroupSize);

        var labels = new Dictionary<int, string>(assignments.Count);
        foreach (var (index, top, sub) in assignments)
        {
            var topText = top.ToString(CultureInfo.InvariantCulture).PadLeft(topWidth, '0');
            labels[index] = sub == 0
                ? topText
                : topText + "." + sub.ToString(CultureInfo.InvariantCulture).PadLeft(subWidth, '0');
        }

        return labels;
    }

    /// <summary>
    /// Composes a leaf display name from a step's computed label and its (runtime-formatted) text.
    /// A standalone step reads <c>"{label}. {step}"</c>; a group member reads <c>"{label} {step}"</c>
    /// (no trailing dot). Both call sites use this so the discovered tree and the running tree agree.
    /// </summary>
    public static string Format(IReadOnlyDictionary<int, string> labels, ScenarioNode node, string stepText)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(node);

        var label = labels[node.Index];
        return node.GroupId is null ? label + ". " + stepText : label + " " + stepText;
    }

    /// <summary>Decimal digit-width of a count (at least 1).</summary>
    static int Digits(int value) => value < 10 ? 1 : value.ToString(CultureInfo.InvariantCulture).Length;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Raun.slnx`
Expected: PASS — all `ScenarioStepNumberingTests` green, no regressions.

- [ ] **Step 5: Commit**

```bash
jj commit -m "feat(mtp): add ScenarioStepNumbering for ordered, padded step labels"
```

---

## Task 3: Wire scenario name + numbered leaves into discovery & execution

`ScenarioTestIdentity.Create` gains the scenario display name; both `RaunDiscoverer` and `RaunStepReporter` compose their leaf text via `ScenarioStepNumbering.Format`, dropping the `" ▸ "` prefix. Tests are updated first (red), then the source. Changing `Create`'s signature is a compile-breaking change, so the three source edits and their test updates land together in one commit.

**Files:**
- Modify: `src/Raun.Mtp/ScenarioTestIdentity.cs:37-52` (the `Create` method + its doc)
- Modify: `src/Raun.Mtp/RaunDiscoverer.cs:28-66` (drop `DisplayNameSeparator`, rework `BuildNodes`/`BuildNode`)
- Modify: `src/Raun.Mtp/RaunStepReporter.cs:46-65,153-171` (label-map field, rework `BuildNode`)
- Test: `test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs`
- Test: `test/Raun.Mtp.Test/RaunDiscovererTests.cs`
- Test: `test/Raun.Mtp.Test/RaunStepReporterTests.cs`
- Test: `test/Raun.Mtp.Test/DiscoveryRequestTests.cs:82`

- [ ] **Step 1: Update `ScenarioTestIdentityTests`**

Replace the `Create_maps_the_parts_onto_the_identity_property` test (the `Split` theory stays unchanged) with:

```csharp
    [Fact]
    public void Create_uses_scenario_display_name_as_method_and_derives_namespace_type_from_fqn()
    {
        var id = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "customer books an appointment");

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Bookings", id.TypeName);
        Assert.Equal("customer books an appointment", id.MethodName);
        Assert.Equal("System.Void", id.ReturnTypeFullName);
        Assert.Empty(id.ParameterTypeFullNames);
    }
```

- [ ] **Step 2: Update `RaunDiscovererTests`**

(a) Add a `group` parameter to the `Node` helper (so group-member cases can be built):

```csharp
    static ScenarioNode Node(int index, string stepId, string template, string? file = null, int line = 0, string? group = null) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        SourceFile = file,
        SourceLine = line,
        DependsOn = [],
        GroupId = group,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };
```

(b) Replace `Display_name_joins_scenario_and_step_template_with_separator` with two tests:

```csharp
    [Fact]
    public void Standalone_step_display_name_is_numbered_without_scenario_prefix()
    {
        var definition = Definition(
            display: "patient booking",
            nodes: [Node(0, "a", "patient Jane exists")]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }

    [Fact]
    public void Group_member_display_name_uses_sub_number_without_trailing_dot()
    {
        var definition = Definition(
            nodes:
            [
                Node(0, "clean", "the database is clean"),
                Node(1, "p", "patient Jane exists", group: "g1"),
                Node(2, "s", "an available slot exists", group: "g1"),
                Node(3, "c", "creating an appointment"),
            ]);

        var nodes = RaunDiscoverer.BuildNodes(definition);

        Assert.Equal("1. the database is clean", nodes[0].DisplayName);
        Assert.Equal("2.1 patient Jane exists", nodes[1].DisplayName);
        Assert.Equal("2.2 an available slot exists", nodes[2].DisplayName);
        Assert.Equal("3. creating an appointment", nodes[3].DisplayName);
    }
```

(c) Replace the body of `Node_carries_method_identity_for_namespace_class_method_grouping` so the method identity is the scenario name:

```csharp
    [Fact]
    public void Node_carries_method_identity_for_namespace_class_method_grouping()
    {
        // Runners build their namespace -> class -> method tree from a TestMethodIdentifierProperty.
        // Namespace/type still come from the scenario method's FQN, but the method node is the human
        // scenario name so the tree reads AppointmentTests -> Scenarios -> <scenario name> -> steps.
        var definition = Definition(
            display: "book an appointment",
            method: "MyApp.Booking.Scenarios.BookAppointment",
            nodes: [Node(0, "a", "step a")]);

        var node = Assert.Single(RaunDiscoverer.BuildNodes(definition));

        var id = Assert.Single(node.Properties.OfType<TestMethodIdentifierProperty>());
        Assert.Equal("MyApp.Booking", id.Namespace);
        Assert.Equal("Scenarios", id.TypeName);
        Assert.Equal("book an appointment", id.MethodName);
    }
```

- [ ] **Step 3: Update `RaunStepReporterTests`**

(a) Add a `group` parameter to the `Node` helper:

```csharp
    static ScenarioNode Node(int index, string stepId, string template, string? file = null, int line = 0, string? group = null) => new()
    {
        Index = index,
        StepId = stepId,
        Phase = "Given",
        OperationName = $"Op{index}",
        DisplayNameTemplate = template,
        SourceFile = file,
        SourceLine = line,
        DependsOn = [],
        GroupId = group,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };
```

(b) Replace `Start_uses_runtime_formatted_display_name_with_scenario_prefix`:

```csharp
    [Fact]
    public async Task Start_uses_numbered_runtime_formatted_display_name_without_prefix()
    {
        var def = Definition(id: "s", display: "patient booking", nodes: [Node(0, "a", "patient exists")]);
        var (reporter, bus) = NewReporter(def);

        // The scheduler computes the formatted name at run time (placeholders resolved); the reporter
        // surfaces that, numbered and without the old scenario prefix.
        await reporter.OnStepStartingAsync(new StepContext { Node = def.Nodes[0], DisplayName = "patient Jane exists" });

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }
```

(c) Replace `Finished_update_uses_the_results_formatted_display_name`:

```csharp
    [Fact]
    public async Task Finished_update_uses_the_numbered_results_formatted_display_name()
    {
        var def = Definition(id: "s", display: "booking", nodes: [Node(0, "a", "patient exists")]);
        var (reporter, bus) = NewReporter(def);

        await reporter.OnStepFinishedAsync(new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "patient Jane exists",
            Status = StepStatus.Passed,
        });

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("1. patient Jane exists", node.DisplayName);
    }
```

(d) Add a group-member test:

```csharp
    [Fact]
    public async Task Group_member_step_is_numbered_with_sub_index()
    {
        var def = Definition(
            id: "s",
            nodes:
            [
                Node(0, "clean", "the database is clean"),
                Node(1, "p", "patient exists", group: "g1"),
                Node(2, "slot", "slot exists", group: "g1"),
            ]);
        var (reporter, bus) = NewReporter(def);

        await reporter.OnStepStartingAsync(new StepContext { Node = def.Nodes[1], DisplayName = "patient Jane exists" });

        var node = Assert.Single(bus.Nodes);
        Assert.Equal("2.1 patient Jane exists", node.DisplayName);
    }
```

- [ ] **Step 4: Update `DiscoveryRequestTests`**

In `Discovered_nodes_carry_display_name_state_and_location`, the scenario has one standalone step, so its leaf is now numbered. Change line 82 from:

```csharp
        Assert.Equal("meta scenario ▸ the only step", node.DisplayName);
```

to:

```csharp
        Assert.Equal("1. the only step", node.DisplayName);
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test Raun.slnx`
Expected: FAIL — `ScenarioTestIdentity.Create` no longer compiles with the new 2-arg call in the test (and the discoverer/reporter still call it with one arg), and the display-name assertions don't match. This is the red state.

- [ ] **Step 6: Update `ScenarioTestIdentity.Create`**

In `src/Raun.Mtp/ScenarioTestIdentity.cs`, replace the `Create` method (lines 36-52) and its doc with:

```csharp
    /// <summary>
    /// Builds the method-identity property for a scenario: namespace and type are derived from
    /// <paramref name="methodFullName"/> (the scenario method's FQN), but the method node is the human
    /// <paramref name="scenarioDisplayName"/> so a runner groups steps under the scenario name.
    /// </summary>
    public static TestMethodIdentifierProperty Create(string methodFullName, string scenarioDisplayName)
    {
        ArgumentNullException.ThrowIfNull(scenarioDisplayName);
        Split(methodFullName, out var @namespace, out var typeName, out _);

        // Positional ctor args (assembly, namespace, type, method, method-arity, parameter-types,
        // return-type) — matching xunit.v3's MTP bridge. Scenarios are non-generic, parameterless
        // (the DSL drives them), so arity 0 / no parameters / void.
        return new TestMethodIdentifierProperty(
            AssemblyFullName,
            @namespace,
            typeName,
            scenarioDisplayName,
            0,
            [],
            VoidReturnTypeName);
    }
```

- [ ] **Step 7: Update `RaunDiscoverer`**

In `src/Raun.Mtp/RaunDiscoverer.cs`:

(a) Delete the now-unused separator constant (lines 28-29):

```csharp
    /// <summary>The scenario/step separator used in display names (matches the xUnit reporter).</summary>
    internal const string DisplayNameSeparator = " ▸ ";
```

(b) Replace `BuildNodes` and `BuildNode` (lines 31-66) with:

```csharp
    /// <summary>Builds the per-step <see cref="TestNode"/> list for one scenario.</summary>
    public static IReadOnlyList<TestNode> BuildNodes(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var labels = ScenarioStepNumbering.Compute(definition);
        var nodes = new List<TestNode>(definition.Nodes.Count);
        foreach (var step in definition.Nodes)
        {
            nodes.Add(BuildNode(definition, step, labels));
        }

        return nodes;
    }

    /// <summary>Builds the <see cref="TestNode"/> for a single scenario step.</summary>
    public static TestNode BuildNode(ScenarioDefinition definition, ScenarioNode step, IReadOnlyDictionary<int, string> labels)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(labels);

        var node = new TestNode
        {
            Uid = MakeUid(definition.ScenarioId, step.StepId),
            DisplayName = ScenarioStepNumbering.Format(labels, step, step.DisplayNameTemplate),
        };

        node.Properties.Add(DiscoveredTestNodeStateProperty.CachedInstance);
        node.Properties.Add(ScenarioTestIdentity.Create(definition.MethodName, definition.DisplayName));

        if (TryMakeFileLocation(step, out var location))
        {
            node.Properties.Add(location);
        }

        return node;
    }
```

Also update the class-doc bullet that mentions the `{scenario} ▸ {step template}` display name (lines 16-18) to describe the numbered, prefix-free leaf:

```csharp
///   <item>a numbered leaf display name (<c>"1. {step}"</c>, group member <c>"2.1 {step}"</c>) from
///   <see cref="ScenarioStepNumbering"/>; the reporter refines runtime-bound names at execution time;</item>
```

- [ ] **Step 8: Update `RaunStepReporter`**

In `src/Raun.Mtp/RaunStepReporter.cs`:

(a) Add a label-map field next to the other readonly fields (after line 49, `readonly IDataProducer producer;`):

```csharp
    readonly IReadOnlyDictionary<int, string> labels;
```

(b) In the constructor, after `this.definition = definition;`, compute the map once:

```csharp
        this.labels = ScenarioStepNumbering.Compute(definition);
```

(c) Replace the `BuildNode` body's `TestNode` initializer + identity (lines 155-161) so the leaf text is numbered and prefix-free:

```csharp
        var testNode = new TestNode
        {
            Uid = RaunDiscoverer.MakeUid(definition.ScenarioId, node.StepId),
            DisplayName = ScenarioStepNumbering.Format(labels, node, displayName),
        };

        testNode.Properties.Add(ScenarioTestIdentity.Create(definition.MethodName, definition.DisplayName));
```

Also update the `BuildNode` doc-comment phrase "the runtime-formatted display name with the scenario prefix" (lines 149-152) to "the numbered, runtime-formatted display name".

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test Raun.slnx`
Expected: PASS — 143 baseline + the new numbering tests, 0 failed. (No references to `DisplayNameSeparator` remain — confirm the build has no unused-const or compile warnings-as-errors.)

- [ ] **Step 10: Commit**

```bash
jj commit -m "feat(mtp): number step leaves and group under the scenario name"
```

---

## Task 4: Sample + VS verification

A real end-to-end check that the four sample scenarios render numbered, and the VS-cache re-verification the handoff requires (the method identity now contains spaces).

**Files:** none (verification only).

- [ ] **Step 1: List the sample tests and eyeball the numbering**

Run: `dotnet run --project samples/AppointmentTests -- --list-tests`
Expected: each scenario's steps print numbered and prefix-free, matching the spec's worked examples, e.g.
- `customer books an appointment`: `1. patient Jane exists`, `2. an available slot exists`, `3. creating an appointment`, `4. the appointment should exist`
- `customer books with parallel arrange`: `1. the database is clean`, `2.1 patient Jane exists`, `2.2 an available slot exists`, `3. creating an appointment`, `4. the appointment should exist`
- `bulk user import`: `1.1 user alice exists`, `1.2 user bob exists`, `2. importing the users`, `3. the import should contain 2 users`
- `bulk user import via LINQ`: `1.1 user user-1 exists`, `1.2 user user-2 exists`, `1.3 user user-3 exists`, `2. importing the users`, `3. the import should contain 3 users`

- [ ] **Step 2: Run the sample end-to-end**

Run: `dotnet run --project samples/AppointmentTests`
Expected: all sample scenarios pass; the run completes quickly (the 5s debug delay is gone).

- [ ] **Step 3: VS Test Explorer re-verification (manual — the method identity now has spaces)**

Per the handoff: VS renders the idle tree from a cache that does not refresh on identity changes. Close VS, delete `.vs/Raun.slnx/v18/TestStore`, reopen, Run All. Confirm the tree groups as `AppointmentTests → Scenarios → <scenario name> → 1. … numbered steps`, and that the scenario-name method (with spaces) does not break the bridge managed name. `RAUN_NODE_DEBUG=<file> dotnet run --project samples/AppointmentTests --no-build -- --list-tests` traces the exact emitted identity if anything looks off.

This step is a human-in-the-loop check; surface it to the user rather than marking it done autonomously.

---

## Self-Review (completed during planning)

- **Spec coverage:** R1 sequential numbering → Task 2 `Linear_scenario…`; R2 group sub-numbering → `Tuple_group…`, `Group_at_start…`, `Array_group_of_three…`, `Two_groups…`; R3 leaf text → `Format_*` tests + Task 3 discoverer/reporter tests; R4 zero-padding → `Ten_or_more…`, `Group_with_ten_or_more_members…`, `Labels_sort_lexically…`; R5 scenario name as method → Task 3 `ScenarioTestIdentity`/discoverer identity tests; R6 uid unchanged → existing `Uid_*` tests still pass (uid code untouched); R7 discovery/execution agree → shared `ScenarioStepNumbering.Format` used by both, asserted in both test classes. Out-of-scope: revert delay → Task 1; VS re-verification → Task 4.
- **Placeholder scan:** none — every code/test step carries full source.
- **Type consistency:** `Compute(ScenarioDefinition) → IReadOnlyDictionary<int,string>` and `Format(IReadOnlyDictionary<int,string>, ScenarioNode, string) → string` are used identically in the discoverer (`step`), reporter (`node`), and tests. `Create(string, string)` is called with `(definition.MethodName, definition.DisplayName)` at both sites and `("…FQN…", "…display…")` in the test. `BuildNode` gains the `labels` third parameter everywhere it is called (`BuildNodes` only).
