# Explicit Lineage Subjects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make report lineage edges explicit and opt-in — a `[References]`/`[Consumes]` target names its subject(s) via `Subjects = [nameof(x), Subject.Return]`; the runtime records `subject→target` edges from real instances; an analyzer (RAUN010) rejects invalid subject names — replacing the current runtime subject-inference.

**Architecture:** Edges are recorded at the step call site (where every parameter value and the return `__r` are in scope) by extending `ResourceContext.Reference`/`Consume` with subject instances. Recorded edges flow `ResourceContext.Edges → StepResult.Edges → HtmlReportModelBuilder`, which stops inferring subjects and just maps recorded edges to the unchanged `ReportReference` output shape. The generator lowers each target's declared subject names to instance expressions and emits the extended calls; an analyzer validates the names at compile time.

**Tech Stack:** C# / .NET, Roslyn source generator + analyzer, xUnit (run under Microsoft.Testing.Platform), Verify for golden snapshots, `jj` for version control.

Spec: `docs/superpowers/specs/2026-06-22-explicit-lineage-subjects-design.md` (committed `63d823bd`).

## Global Constraints

- **Version control is `jj`, never `git`.** Mutations via `jj` only. Commit with `jj -R "C:/dev/raun" commit -m "<msg>"`. **No `Co-Authored-By` / tooling trailers** in messages.
- **All work is in the isolated workspace `C:/dev/raun` (off `main`).** The harness shell cwd is pinned to `C:\dev\raun` and `cd` does not persist — use absolute paths and `jj -R "C:/dev/raun"`.
- **Build:** `dotnet build "C:/dev/raun/Raun.slnx"` must be clean — **0 warnings** — before any task is considered done.
- **Test:** `dotnet test "C:/dev/raun/test/<Project>" --filter-method "*<Name>*"` (Microsoft.Testing.Platform filter). If the filter flag is rejected by the runner, run the whole project: `dotnet test "C:/dev/raun/test/<Project>"`.
- **TDD:** every behavioral change starts with a failing test. **The `references` JSON output shape (`ReportReference`) must not change.**
- The sentinel literal `"<return>"` is defined in **two** assemblies that don't share code: `Raun.Subject.Return` (runtime) and `AttributeReader.ReturnSubject` (generator). They must stay identical — call this out in both code comments.

---

## File map

| File | Responsibility | Tasks |
|---|---|---|
| `src/Raun/Resources/ResourceRoleAttributes.cs` | `Subjects` ctor on `[References]`/`[Consumes]` | 1 |
| `src/Raun/Resources/Subject.cs` *(new)* | `Subject.Return` sentinel | 1 |
| `src/Raun/Model/ResourceLineageEdge.cs` *(new)* | recorded edge record | 2 |
| `src/Raun/Resources/ResourceContext.cs` | record edges; `Edges` property | 2 |
| `src/Raun/Model/StepResult.cs` | carry `Edges` | 3 |
| `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs` | map recorded edges → `ReportReference` | 3 |
| `src/Raun.Generator/Lowering/AttributeReader.cs` | `ParameterSubjects`, `ReturnSubject` const | 4 |
| `src/Raun.Generator/Lowering/Ir.cs` | `ResourceRoleClaim.SubjectExpressions` | 4 |
| `src/Raun.Generator/Lowering/ScenarioParser.cs` | resolve subject names → expressions | 4 |
| `src/Raun.Generator/Emit/ScenarioEmitter.cs` | emit multi-arg edge calls | 4 |
| `src/Raun/Scheduling/ScenarioScheduler.cs` | propagate `Edges` into `StepResult` | 5 |
| `src/Raun.Generator/Analysis/Descriptors.cs` + `ScenarioAnalyzer.cs` + `AnalyzerReleases.Unshipped.md` | RAUN010 | 6 |
| `test/Raun.Generator.Test/SampleSources.cs` | migrate `BookWithLineage` fixture | 4 |
| `samples/AppointmentTests/AppointmentDsl.cs` | migrate `CreateAppointment` | 7 |

---

## Task 1: Attribute API — `Subjects` + `Subject.Return`

**Files:**
- Modify: `C:/dev/raun/src/Raun/Resources/ResourceRoleAttributes.cs` (the `ReferencesAttribute` and `ConsumesAttribute` declarations)
- Create: `C:/dev/raun/src/Raun/Resources/Subject.cs`
- Test: `C:/dev/raun/test/Raun.Test/Resources/SubjectAttributeTests.cs` (new)

**Interfaces:**
- Produces: `ReferencesAttribute(params string[] subjects)` with `string[] Subjects`; `ConsumesAttribute(params string[] subjects)` with `string[] Subjects`; `static class Subject { const string Return = "<return>"; }` in namespace `Raun`.

- [ ] **Step 1: Write the failing test**

Create `C:/dev/raun/test/Raun.Test/Resources/SubjectAttributeTests.cs`:

```csharp
using Raun;
using Xunit;

namespace Raun.Test.Resources;

public class SubjectAttributeTests
{
    [Fact]
    public void References_captures_subjects_and_defaults_to_empty()
    {
        Assert.Equal(["acc", "<return>"], new ReferencesAttribute("acc", Subject.Return).Subjects);
        Assert.Empty(new ReferencesAttribute().Subjects);
    }

    [Fact]
    public void Consumes_captures_subjects_and_defaults_to_empty()
    {
        Assert.Equal(["from"], new ConsumesAttribute("from").Subjects);
        Assert.Empty(new ConsumesAttribute().Subjects);
    }

    [Fact]
    public void Subject_Return_is_the_reserved_token()
    {
        Assert.Equal("<return>", Subject.Return);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "C:/dev/raun/test/Raun.Test" --filter-method "*SubjectAttribute*"`
Expected: FAIL to compile — `ReferencesAttribute` has no constructor taking arguments; `Subject` does not exist.

- [ ] **Step 3: Implement — extend the attributes**

In `C:/dev/raun/src/Raun/Resources/ResourceRoleAttributes.cs`, replace the two marker declarations (currently `public sealed class ReferencesAttribute : Attribute;` and `public sealed class ConsumesAttribute : Attribute;`) with bodies. Keep their existing XML doc comments above them; the new bodies are:

```csharp
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ReferencesAttribute : Attribute
{
    /// <summary>Lineage subjects: each is a parameter name (via <c>nameof</c>) or <see cref="Subject.Return"/>.</summary>
    public ReferencesAttribute(params string[] subjects) => Subjects = subjects;

    /// <summary>The produced/edited resources that hold this reference; empty ⇒ no lineage edge.</summary>
    public string[] Subjects { get; }
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ConsumesAttribute : Attribute
{
    /// <summary>Lineage subjects: each is a parameter name (via <c>nameof</c>) or <see cref="Subject.Return"/>.</summary>
    public ConsumesAttribute(params string[] subjects) => Subjects = subjects;

    /// <summary>The produced/edited resources that consume this resource; empty ⇒ no lineage edge.</summary>
    public string[] Subjects { get; }
}
```

- [ ] **Step 4: Implement — the sentinel**

Create `C:/dev/raun/src/Raun/Resources/Subject.cs`:

```csharp
namespace Raun;

/// <summary>Well-known lineage subjects for <c>[References]</c>/<c>[Consumes]</c>.</summary>
public static class Subject
{
    /// <summary>
    /// The step's <c>[Creates]</c>/<c>[Edits]</c> return value, as a lineage subject. The value is a
    /// reserved token no C# parameter can be named. MUST stay identical to
    /// <c>Raun.Generator.Lowering.AttributeReader.ReturnSubject</c> (separate assembly).
    /// </summary>
    public const string Return = "<return>";
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test "C:/dev/raun/test/Raun.Test" --filter-method "*SubjectAttribute*"`
Expected: PASS (3 tests).

- [ ] **Step 6: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings.
```bash
jj -R "C:/dev/raun" commit -m "feat(resources): Subjects params on [References]/[Consumes] + Subject.Return sentinel"
```

---

## Task 2: Runtime edge model + recording

**Files:**
- Create: `C:/dev/raun/src/Raun/Model/ResourceLineageEdge.cs`
- Modify: `C:/dev/raun/src/Raun/Resources/ResourceContext.cs` (add `_edges`/`Edges`; add subjects to `Reference`/`Consume`; add `RecordEdges`)
- Test: `C:/dev/raun/test/Raun.Test/Resources/ResourceContextTests.cs` (add tests)

**Interfaces:**
- Consumes: `ResourceIdentity` (`(Type Type, ResourceKey Key)`), `LifecycleVerb`, `ResourceIdentityResolver.Resolve(Type, object)`.
- Produces: `sealed record ResourceLineageEdge { ResourceIdentity Subject; ResourceIdentity Target; LifecycleVerb Kind; }`; `ResourceContext.Reference<T>(T resource, params object[] subjects)`; `ResourceContext.Consume<T>(T resource, params object[] subjects)`; `IReadOnlyList<ResourceLineageEdge> ResourceContext.Edges`.

- [ ] **Step 1: Write the failing tests**

Add to `C:/dev/raun/test/Raun.Test/Resources/ResourceContextTests.cs` (the class already has `static ResourceContext NewContext(out FixedTimeProvider clock)` and uses the `User(string Email)` resource record):

```csharp
    [Fact]
    public async Task Reference_with_a_subject_records_a_lineage_edge_and_the_effect()
    {
        var ctx = NewContext(out _);
        var subject = new User("appt@acme.com");   // stands in for the produced resource
        var target = new User("jane@acme.com");

        await ctx.Reference(target, subject);

        var effect = Assert.Single(ctx.Effects);            // the Reference effect is still recorded
        Assert.Equal(LifecycleVerb.Reference, effect.Verb);
        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), effect.Identity);

        var edge = Assert.Single(ctx.Edges);                // and a subject -> target edge
        Assert.Equal(new ResourceIdentity(typeof(User), "appt@acme.com"), edge.Subject);
        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), edge.Target);
        Assert.Equal(LifecycleVerb.Reference, edge.Kind);
    }

    [Fact]
    public async Task Consume_with_two_subjects_records_two_edges()
    {
        var ctx = NewContext(out _);
        var a = new User("a@acme.com");
        var b = new User("b@acme.com");
        var target = new User("slot@acme.com");

        await ctx.Consume(target, a, b);

        Assert.Equal(2, ctx.Edges.Count);
        Assert.All(ctx.Edges, e => Assert.Equal(LifecycleVerb.Consume, e.Kind));
        Assert.All(ctx.Edges, e => Assert.Equal(new ResourceIdentity(typeof(User), "slot@acme.com"), e.Target));
        Assert.Contains(ctx.Edges, e => e.Subject == new ResourceIdentity(typeof(User), "a@acme.com"));
        Assert.Contains(ctx.Edges, e => e.Subject == new ResourceIdentity(typeof(User), "b@acme.com"));
    }

    [Fact]
    public async Task Reference_without_subjects_records_no_edge()
    {
        var ctx = NewContext(out _);

        await ctx.Reference(new User("jane@acme.com"));

        Assert.Single(ctx.Effects);
        Assert.Empty(ctx.Edges);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "C:/dev/raun/test/Raun.Test" --filter-method "*records_a_lineage_edge*"`
Expected: FAIL to compile — `ctx.Reference` has no 2-arg overload, `ctx.Edges` does not exist, `ResourceLineageEdge` does not exist.

- [ ] **Step 3: Implement — the edge record**

Create `C:/dev/raun/src/Raun/Model/ResourceLineageEdge.cs`:

```csharp
using Raun;

namespace Raun.Model;

/// <summary>
/// One explicitly-declared lineage edge recorded by a step: the produced/edited
/// <see cref="Subject"/> holds a <see cref="Kind"/> relationship to <see cref="Target"/>.
/// </summary>
public sealed record ResourceLineageEdge
{
    /// <summary>The produced/edited resource (a <c>[Creates]</c>/<c>[Edits]</c> subject).</summary>
    public required ResourceIdentity Subject { get; init; }

    /// <summary>The referenced/consumed resource.</summary>
    public required ResourceIdentity Target { get; init; }

    /// <summary><see cref="LifecycleVerb.Reference"/> or <see cref="LifecycleVerb.Consume"/>.</summary>
    public required LifecycleVerb Kind { get; init; }
}
```

- [ ] **Step 4: Implement — recording in `ResourceContext`**

In `C:/dev/raun/src/Raun/Resources/ResourceContext.cs`:

(a) Add the backing list next to `_effects` (after the `readonly List<ResourceEffect> _effects = [];` line):
```csharp
    readonly List<ResourceLineageEdge> _edges = [];
```

(b) Add the `Edges` accessor next to the `Effects` property:
```csharp
    /// <summary>Lineage edges recorded by this step's [References]/[Consumes] subjects.</summary>
    public IReadOnlyList<ResourceLineageEdge> Edges
    {
        get { lock (_lock) { return _edges.ToArray(); } }
    }
```

(c) Replace the existing `Reference<T>` and `Consume<T>` methods with subject-aware versions:
```csharp
    /// <summary>Records the produced resource keeping a durable reference to <paramref name="resource"/> (shared),
    /// plus a lineage edge from each subject to it.</summary>
    public ValueTask Reference<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        RecordEdges(LifecycleVerb.Reference, target, subjects);
        return Record(LifecycleVerb.Reference, target, resource);
    }

    /// <summary>Records consuming/using-up <paramref name="resource"/> into the produced resource (shared in C1),
    /// plus a lineage edge from each subject to it.</summary>
    public ValueTask Consume<T>(T resource, params object[] subjects)
        where T : notnull
    {
        var target = _resolver.Resolve(resource);
        RecordEdges(LifecycleVerb.Consume, target, subjects);
        return Record(LifecycleVerb.Consume, target, resource);
    }
```

(d) Add the edge sink (place it next to `Record`):
```csharp
    void RecordEdges(LifecycleVerb kind, ResourceIdentity target, object[] subjects)
    {
        if (subjects is null || subjects.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var subject in subjects)
            {
                var subjectIdentity = _resolver.Resolve(subject.GetType(), subject);
                _edges.Add(new ResourceLineageEdge { Subject = subjectIdentity, Target = target, Kind = kind });
            }
        }
    }
```

> Note: `ResourceContext.cs` already has `using Raun.Model;`. The `params object[]` overloads replace the prior single-arg `Reference`/`Consume`; existing callers like `await ctx.Reference(user)` still bind (empty array), so `ResourceContextTests` Reference/Consume effect tests keep passing.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test "C:/dev/raun/test/Raun.Test" --filter-method "*ResourceContext*"`
Expected: PASS — the three new tests plus all pre-existing `ResourceContextTests`.

- [ ] **Step 6: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings.
```bash
jj -R "C:/dev/raun" commit -m "feat(resources): record lineage edges from [References]/[Consumes] subjects"
```

---

## Task 3: Carry edges to the report builder

**Files:**
- Modify: `C:/dev/raun/src/Raun/Model/StepResult.cs` (add `Edges`)
- Modify: `C:/dev/raun/src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs` (replace inference loop)
- Test: `C:/dev/raun/test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs` (extend `Result` helper; rewrite the 4 lineage tests)

**Interfaces:**
- Consumes: `ResourceLineageEdge` (Task 2), `ReportReference` (`SubjectType/Key, TargetType/Key, Kind` strings).
- Produces: `StepResult.Edges` (`IReadOnlyList<ResourceLineageEdge>`, default `[]`); builder maps `ordered.SelectMany(r => r.Edges)` → `ReportReference`, deduped by `(SubjectType, SubjectKey, TargetType, TargetKey)`.

- [ ] **Step 1: Add `Edges` to `StepResult` and the test helper (enabling step — no behavior yet)**

In `C:/dev/raun/src/Raun/Model/StepResult.cs`, directly after the `Effects` property (`public IReadOnlyList<ResourceEffect> Effects { get; init; } = [];`) add:
```csharp
    /// <summary>Lineage edges the step recorded from [References]/[Consumes] subjects.</summary>
    public IReadOnlyList<ResourceLineageEdge> Edges { get; init; } = [];
```

In `C:/dev/raun/test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`, change the `Result` helper signature and body to accept edges:
```csharp
    private static StepResult Result(ScenarioNode node, DateTimeOffset startedAt, double ms,
        StepStatus status = StepStatus.Passed, IReadOnlyList<ResourceEffect>? effects = null,
        IReadOnlyList<string>? logs = null, IReadOnlyList<ResourceLineageEdge>? edges = null) => new()
    {
        Node = node, DisplayName = node.DisplayNameTemplate, Status = status,
        StartedAt = startedAt, Duration = TimeSpan.FromMilliseconds(ms),
        Effects = effects ?? [], Logs = logs ?? [], Edges = edges ?? [],
    };
```

- [ ] **Step 2: Write the failing tests (rewrite the 4 lineage tests)**

In `HtmlReportModelBuilderTests.cs`, replace the four tests
`References_and_consumes_derive_lineage_edges_from_the_step_subject`,
`A_reference_effect_without_a_subject_yields_no_edge`,
`A_step_with_two_subjects_yields_no_edges`, and
`A_repeated_subject_target_edge_is_deduped_across_steps`
with the explicit-edge versions below:

```csharp
    [Fact]
    public void Recorded_edges_become_lineage_references()
    {
        var n0 = Node(0, "c", "When", "When creating an appointment");
        var def = Def(n0);
        var appointment = new ResourceIdentity(typeof(string), "appt-1");
        var patient = new ResourceIdentity(typeof(string), "Jane");
        var slot = new ResourceIdentity(typeof(int), "7");

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, edges:
        [
            new ResourceLineageEdge { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference },
            new ResourceLineageEdge { Subject = appointment, Target = slot, Kind = LifecycleVerb.Consume },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(2, scenario.References.Count);

        var aggregation = scenario.References.Single(e => e.Kind == "Reference");
        Assert.Equal("String", aggregation.SubjectType);
        Assert.Equal("appt-1", aggregation.SubjectKey);
        Assert.Equal("String", aggregation.TargetType);
        Assert.Equal("Jane", aggregation.TargetKey);

        var composition = scenario.References.Single(e => e.Kind == "Consume");
        Assert.Equal("appt-1", composition.SubjectKey);
        Assert.Equal("Int32", composition.TargetType);
        Assert.Equal("7", composition.TargetKey);
    }

    [Fact]
    public void A_step_with_no_edges_yields_no_references()
    {
        var n0 = Node(0, "t", "Then", "Then the appointment should exist");
        var def = Def(n0);

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, effects:
        [
            new ResourceEffect { Verb = LifecycleVerb.Reference, Identity = new ResourceIdentity(typeof(string), "Jane"), StepId = "t", Timestamp = T0.AddMilliseconds(1) },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Empty(scenario.References);
    }

    [Fact]
    public void Multiple_subjects_on_one_target_yield_multiple_edges()
    {
        var n0 = Node(0, "w", "When", "When transferring between accounts");
        var def = Def(n0);
        var from = new ResourceIdentity(typeof(string), "acc-from");
        var to = new ResourceIdentity(typeof(string), "acc-to");
        var bank = new ResourceIdentity(typeof(string), "Bank");

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, edges:
        [
            new ResourceLineageEdge { Subject = from, Target = bank, Kind = LifecycleVerb.Reference },
            new ResourceLineageEdge { Subject = to, Target = bank, Kind = LifecycleVerb.Reference },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(2, scenario.References.Count);
        Assert.Contains(scenario.References, e => e.SubjectKey == "acc-from" && e.TargetKey == "Bank");
        Assert.Contains(scenario.References, e => e.SubjectKey == "acc-to" && e.TargetKey == "Bank");
    }

    [Fact]
    public void A_repeated_edge_is_deduped_across_steps()
    {
        var n0 = Node(0, "a", "When", "When step a");
        var n1 = Node(1, "b", "When", "When step b");
        var def = Def(n0, n1);
        var appointment = new ResourceIdentity(typeof(string), "appt-1");
        var patient = new ResourceIdentity(typeof(string), "Jane");

        var builder = new HtmlReport.HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, edges:
            [new ResourceLineageEdge { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference }]));
        builder.OnStepFinished(def, Result(n1, T0, 10, edges:
            [new ResourceLineageEdge { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference }]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Single(scenario.References);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test "C:/dev/raun/test/Raun.Mtp.Test" --filter-method "*edges*"`
Expected: FAIL — the builder still derives from effects, so `Recorded_edges_become_lineage_references` and `Multiple_subjects...` produce 0 references.

- [ ] **Step 4: Implement — builder maps recorded edges**

In `C:/dev/raun/src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`, replace the entire inference block (the comment starting `// Lineage edges (2026-06-21 spec)` through the closing brace of the `foreach (var r in ordered)` loop that builds `references`) with:

```csharp
            // Lineage edges (2026-06-22 spec): edges are recorded explicitly at runtime from each
            // [References]/[Consumes] target's declared subjects. Map them straight through; dedup by
            // (subject, target) across the scenario. No subject inference.
            var references = new List<ReportReference>();
            var seenEdges = new HashSet<(string, string, string, string)>();
            foreach (var r in ordered)
            {
                foreach (var edge in r.Edges)
                {
                    var subjectType = edge.Subject.Type.Name;
                    var subjectKey = edge.Subject.Key.ToString();
                    var targetType = edge.Target.Type.Name;
                    var targetKey = edge.Target.Key.ToString();
                    if (!seenEdges.Add((subjectType, subjectKey, targetType, targetKey)))
                    {
                        continue;
                    }

                    references.Add(new ReportReference
                    {
                        SubjectType = subjectType,
                        SubjectKey = subjectKey,
                        TargetType = targetType,
                        TargetKey = targetKey,
                        Kind = edge.Kind.ToString(),
                    });
                }
            }
```

(The `References = references` assignment further down stays unchanged. If `LifecycleVerb` is now unused elsewhere in the file, leave the `using` — it is still referenced by `ResourceEffect` usages.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test "C:/dev/raun/test/Raun.Mtp.Test"`
Expected: PASS — the four rewritten tests plus the existing model/json tests (the `Builds_the_expected_json_model` golden assertion is unaffected: that scenario records no edges and the `references` array stays `[]`).

- [ ] **Step 6: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings.
```bash
jj -R "C:/dev/raun" commit -m "feat(report): build lineage from recorded edges instead of subject inference"
```

---

## Task 4: Lowering + emit the edge calls

**Files:**
- Modify: `C:/dev/raun/src/Raun.Generator/Lowering/AttributeReader.cs` (add `ReturnSubject`, `ParameterSubjects`)
- Modify: `C:/dev/raun/src/Raun.Generator/Lowering/Ir.cs` (add `SubjectExpressions` to `ResourceRoleClaim`)
- Modify: `C:/dev/raun/src/Raun.Generator/Lowering/ScenarioParser.cs` (`BuildResourceClaims` + `ResolveSubjectExpressions`)
- Modify: `C:/dev/raun/src/Raun.Generator/Emit/ScenarioEmitter.cs` (`ResourceCallStatement`)
- Modify: `C:/dev/raun/test/Raun.Generator.Test/SampleSources.cs` (`BookWithLineage` gets explicit subjects)
- Test: `C:/dev/raun/test/Raun.Generator.Test/ResourceLoweringTests.cs` (new emitted-text test)

**Interfaces:**
- Consumes: `ResourceContext.Reference/Consume(target, params object[])` (Task 2, so generated code compiles).
- Produces: `AttributeReader.ReturnSubject` (`const string "<return>"`); `AttributeReader.ParameterSubjects(IParameterSymbol) → ImmutableArray<string>`; `ResourceRoleClaim.SubjectExpressions` (`IReadOnlyList<string>`, default `[]`); emitter renders `await __ctx.Resources.{Verb}(<target>, <subject>...);`.

- [ ] **Step 1: Write the failing test + migrate the fixture**

In `C:/dev/raun/test/Raun.Generator.Test/SampleSources.cs`, change the `BookWithLineage` step (currently `BookWithLineage([References] User user, [Consumes] Slot slot)`) so its target params declare the created return as their subject:

```csharp
                [StepName("booking with lineage")]
                [return: Creates]
                public static async Task<Appointment> BookWithLineage([References(Subject.Return)] User user, [Consumes(Subject.Return)] Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(user, slot);
                }
```

Add a test to `C:/dev/raun/test/Raun.Generator.Test/ResourceLoweringTests.cs`:

```csharp
    [Fact]
    public void Reference_and_consume_subjects_emit_edge_calls()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();

        // Each [References(Subject.Return)]/[Consumes(Subject.Return)] target emits the call with __r appended.
        Assert.Matches(@"Resources\.Reference\([^)]*,\s*__r\)", result.GeneratedSource);
        Assert.Matches(@"Resources\.Consume\([^)]*,\s*__r\)", result.GeneratedSource);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*subjects_emit_edge_calls*"`
Expected: FAIL — the emitter still renders single-argument `Resources.Reference(<target>);`, so the regex does not match.

- [ ] **Step 3: Implement — `AttributeReader`**

In `C:/dev/raun/src/Raun.Generator/Lowering/AttributeReader.cs`, add a const at the top of the class and a reader method (mirror `ParameterRole`). It needs `using System.Collections.Immutable;` and `using System.Linq;` (already present):

```csharp
    /// <summary>The reserved <c>[References]</c>/<c>[Consumes]</c> subject token meaning the step's return.
    /// MUST stay identical to <c>Raun.Subject.Return</c> (separate assembly).</summary>
    public const string ReturnSubject = "<return>";

    /// <summary>
    /// The lineage subject names declared on a <c>[References]</c>/<c>[Consumes]</c> parameter (its
    /// <c>params string[] subjects</c>), or empty when none/not a lineage role.
    /// </summary>
    public static ImmutableArray<string> ParameterSubjects(IParameterSymbol parameter)
    {
        foreach (var attr in parameter.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "ReferencesAttribute" or "ConsumesAttribute"
                && attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0] is { Kind: TypedConstantKind.Array } arrayArg)
            {
                return arrayArg.Values
                    .Select(v => v.Value as string)
                    .Where(s => s is not null)
                    .ToImmutableArray()!;
            }
        }

        return ImmutableArray<string>.Empty;
    }
```

- [ ] **Step 4: Implement — `ResourceRoleClaim` gains `SubjectExpressions`**

In `C:/dev/raun/src/Raun.Generator/Lowering/Ir.cs`, change the `ResourceRoleClaim` record struct to carry subject expressions (keep the doc comment above it):

```csharp
internal readonly record struct ResourceRoleClaim(string Verb, string Expression, bool IsReturn)
{
    /// <summary>Instance expressions for the lineage subjects (a parameter's rewritten argument, or
    /// <c>__r</c>). Empty for non-lineage roles. Emitted as extra arguments to Reference/Consume.</summary>
    public IReadOnlyList<string> SubjectExpressions { get; init; } = [];
}
```

(Ensure `Ir.cs` has `using System.Collections.Generic;` — add it if absent.)

- [ ] **Step 5: Implement — resolve subjects in `BuildResourceClaims`**

In `C:/dev/raun/src/Raun.Generator/Lowering/ScenarioParser.cs`, in `BuildResourceClaims`, replace the parameter-claim add (currently the two lines
`var expression = ((ExpressionSyntax)rewriter.Visit(argument.Expression)).ToFullString().Trim();`
`claims.Add(new ResourceRoleClaim(role, expression, IsReturn: false));`)
with:

```csharp
            var expression = ((ExpressionSyntax)rewriter.Visit(argument.Expression)).ToFullString().Trim();
            var subjectExpressions = role is "Reference" or "Consume"
                ? ResolveSubjectExpressions(parameter, method, arguments, rewriter)
                : [];
            claims.Add(new ResourceRoleClaim(role, expression, IsReturn: false) { SubjectExpressions = subjectExpressions });
```

Then add the helper next to `BuildResourceClaims`:

```csharp
    /// <summary>
    /// Maps a [References]/[Consumes] parameter's declared subject names to instance expressions:
    /// <c>Subject.Return</c> ⇒ <c>__r</c>; a parameter name ⇒ that parameter's rewritten argument
    /// expression. Unresolved names are skipped (the analyzer reports them as RAUN010).
    /// </summary>
    private static IReadOnlyList<string> ResolveSubjectExpressions(
        IParameterSymbol parameter,
        IMethodSymbol method,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IdentifierReplacer rewriter)
    {
        var result = new List<string>();
        foreach (var subject in AttributeReader.ParameterSubjects(parameter))
        {
            if (subject == AttributeReader.ReturnSubject)
            {
                result.Add("__r");
                continue;
            }

            for (var i = 0; i < method.Parameters.Length; i++)
            {
                if (method.Parameters[i].Name != subject)
                {
                    continue;
                }

                var arg = FindArgument(arguments, subject, i);
                if (arg is not null)
                {
                    result.Add(((ExpressionSyntax)rewriter.Visit(arg.Expression)).ToFullString().Trim());
                }

                break;
            }
        }

        return result;
    }
```

- [ ] **Step 6: Implement — emit extra arguments in `ScenarioEmitter`**

In `C:/dev/raun/src/Raun.Generator/Emit/ScenarioEmitter.cs`, replace the body of `ResourceCallStatement` (currently builds a `SingletonSeparatedList` of one argument) with a multi-argument version:

```csharp
    private static StatementSyntax ResourceCallStatement(ResourceRoleClaim claim)
    {
        var arguments = new List<ArgumentSyntax> { Argument(ParseExpression(claim.Expression)) };
        foreach (var subject in claim.SubjectExpressions)
        {
            arguments.Add(Argument(ParseExpression(subject)));
        }

        var call = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("__ctx"),
                    IdentifierName("Resources")),
                IdentifierName(claim.Verb)))
            .WithArgumentList(ArgumentList(SeparatedList(arguments)));

        return ExpressionStatement(AwaitExpression(call)).WithLeadingTrivia(HiddenTrivia());
    }
```

(`SeparatedList` and `List<>` are already in scope in this file via the SyntaxFactory `using static` and `System.Collections.Generic`.)

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*subjects_emit_edge_calls*"`
Expected: PASS.

- [ ] **Step 8: Run the full generator suite (catch snapshot/lowering regressions)**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test"`
Expected: PASS. In particular `Role_free_scenario_emits_no_resource_calls` and `Reference_and_consume_params_lower_to_shared_lineage_effects` (effects unchanged) and the `Resource_scenario` Verify snapshot (its scenario uses `Suspend`, not `BookWithLineage`, so the golden file is unaffected) all stay green.

- [ ] **Step 9: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings.
```bash
jj -R "C:/dev/raun" commit -m "feat(generator): lower and emit lineage subject edge calls"
```

---

## Task 5: Propagate edges through the scheduler (end-to-end)

**Files:**
- Modify: `C:/dev/raun/src/Raun/Scheduling/ScenarioScheduler.cs` (two `StepResult` construction sites)
- Test: `C:/dev/raun/test/Raun.Generator.Test/ResourceLoweringTests.cs` (new end-to-end test)

**Interfaces:**
- Consumes: `ResourceContext.Edges` (Task 2), `StepResult.Edges` (Task 3), the emitted edge calls (Task 4).
- Produces: `StepResult.Edges` populated from the per-step `ResourceContext.Edges` for both the pass and fail/skip paths.

- [ ] **Step 1: Write the failing end-to-end test**

Add to `C:/dev/raun/test/Raun.Generator.Test/ResourceLoweringTests.cs`:

```csharp
    [Fact]
    public async Task BookWithLineage_records_edges_from_the_created_appointment()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
        result.AssertCompiles();
        var results = await result.Definitions().Single().RunAsync();

        // Step 2: BookWithLineage([References(Subject.Return)] User, [Consumes(Subject.Return)] Slot) [return: Creates] Appointment
        var edges = results[2].Edges;
        Assert.Equal(2, edges.Count);

        var reference = edges.Single(e => e.Kind == LifecycleVerb.Reference);
        Assert.Equal("Appointment:jane@acme.com@1", reference.Subject.ToString());
        Assert.Equal("User:jane@acme.com", reference.Target.ToString());

        var consume = edges.Single(e => e.Kind == LifecycleVerb.Consume);
        Assert.Equal("Appointment:jane@acme.com@1", consume.Subject.ToString());
        Assert.Equal("Slot:1", consume.Target.ToString());
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*records_edges_from_the_created_appointment*"`
Expected: FAIL — `results[2].Edges` is empty; the scheduler never copies `context.Resources.Edges` into `StepResult`.

- [ ] **Step 3: Implement — wire both `StepResult` sites**

In `C:/dev/raun/src/Raun/Scheduling/ScenarioScheduler.cs`, in the **success** `StepResult` initializer (the one with `Status = StepStatus.Passed`), add an `Edges` line directly after `Effects = context.Resources.Effects,`:
```csharp
                Effects = context.Resources.Effects,
                Edges = context.Resources.Edges,
```

In the **fail/skip** `StepResult` initializer (inside the `Outcome(...)` helper, the one with `Exception`/`SkipReason`), add the same line after its `Effects = context.Resources.Effects,`:
```csharp
                Effects = context.Resources.Effects,
                Edges = context.Resources.Edges,
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*records_edges_from_the_created_appointment*"`
Expected: PASS.

- [ ] **Step 5: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings.
```bash
jj -R "C:/dev/raun" commit -m "feat(scheduling): propagate recorded lineage edges into StepResult"
```

---

## Task 6: Analyzer — RAUN010 invalid lineage subject

**Files:**
- Modify: `C:/dev/raun/src/Raun.Generator/Analysis/Descriptors.cs` (add descriptor)
- Modify: `C:/dev/raun/src/Raun.Generator/Analysis/ScenarioAnalyzer.cs` (register + validate)
- Modify: `C:/dev/raun/src/Raun.Generator/AnalyzerReleases.Unshipped.md` (add row)
- Test: `C:/dev/raun/test/Raun.Generator.Test/AnalyzerTests.cs` (add tests)

**Interfaces:**
- Consumes: `AttributeReader.ParameterSubjects`, `AttributeReader.ReturnSubject`, `AttributeReader.ParameterRole`, `AttributeReader.ReturnRole` (Tasks 4).
- Produces: `Descriptors.InvalidLineageSubject` (id `RAUN010`, Error); validation in `AnalyzeStepResources`.

- [ ] **Step 1: Write the failing tests**

Add to `C:/dev/raun/test/Raun.Generator.Test/AnalyzerTests.cs` (uses the existing `GeneratorHarness.AnalyzeAsync` + `AssertHas` helpers):

```csharp
    [Fact]
    public void RAUN010_is_a_supported_diagnostic()
    {
        var analyzer = new Raun.Generator.Analysis.ScenarioAnalyzer();
        Assert.Contains(analyzer.SupportedDiagnostics, d => d.Id == "RAUN010");
    }

    private const string LineageDsl =
        """
        using System.Threading.Tasks;
        using Raun;
        namespace Bad;
        public sealed record User(string Email) : IResource<User> { public static ResourceKey KeyFor(User i) => i.Email; }
        public sealed record Account(string Id) : IResource<Account> { public static ResourceKey KeyFor(Account i) => i.Id; }

        """;

    [Fact]
    public async Task RAUN010_unknown_subject_name()
    {
        var source = LineageDsl +
            """
            public static class BadDsl
            {
                extension(When)
                {
                    [StepName("transfer")]
                    public static async Task Transfer([Edits] Account acc, [References("ghost")] User who) { await Task.Yield(); }
                }
            }
            """;

        AssertHas(await GeneratorHarness.AnalyzeAsync(source), "RAUN010");
    }

    [Fact]
    public async Task RAUN010_subject_names_a_non_subject_role()
    {
        var source = LineageDsl +
            """
            public static class BadDsl
            {
                extension(When)
                {
                    [StepName("transfer")]
                    public static async Task Transfer([Reads] Account acc, [References(nameof(acc))] User who) { await Task.Yield(); }
                }
            }
            """;

        AssertHas(await GeneratorHarness.AnalyzeAsync(source), "RAUN010");
    }

    [Fact]
    public async Task RAUN010_return_sentinel_without_a_creating_return()
    {
        var source = LineageDsl +
            """
            public static class BadDsl
            {
                extension(When)
                {
                    [StepName("look up")]
                    public static async Task LookUp([References(Subject.Return)] User who) { await Task.Yield(); }
                }
            }
            """;

        AssertHas(await GeneratorHarness.AnalyzeAsync(source), "RAUN010");
    }

    [Fact]
    public async Task RAUN010_clean_for_valid_subjects()
    {
        var source = LineageDsl +
            """
            public static class GoodDsl
            {
                extension(When)
                {
                    [StepName("assign")]
                    public static async Task Assign([Edits] Account acc, [References(nameof(acc))] User who) { await Task.Yield(); }

                    [StepName("create")]
                    [return: Creates]
                    public static async Task<Account> Create([References(Subject.Return)] User who) { await Task.Yield(); return new Account("a"); }

                    [StepName("note")]
                    public static async Task Note([References] User who) { await Task.Yield(); }
                }
            }
            """;

        Assert.DoesNotContain(await GeneratorHarness.AnalyzeAsync(source), d => d.Id == "RAUN010");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*RAUN010*"`
Expected: FAIL — `RAUN010_is_a_supported_diagnostic` fails (not registered) and the positive tests fail (no RAUN010 produced).

- [ ] **Step 3: Implement — descriptor**

In `C:/dev/raun/src/Raun.Generator/Analysis/Descriptors.cs`, add after `MissingResourceRole`:

```csharp
    public static readonly DiagnosticDescriptor InvalidLineageSubject = new(
        "RAUN010",
        "Lineage subject must name a step subject",
        "'{0}' is not a valid lineage subject for step '{1}' — Subject must name an [Edits] parameter or the [Creates]/[Edits] return (use Subject.Return)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

- [ ] **Step 4: Implement — register + validate**

In `C:/dev/raun/src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`, add `Descriptors.InvalidLineageSubject,` to the `SupportedDiagnostics` collection initializer (after `Descriptors.MissingResourceRole,`).

Then extend `AnalyzeStepResources` — append this validation at the end of the method (after the existing return-role check), reusing `SymbolHelpers.TryUnwrapReturn`:

```csharp
        var editParamNames = method.Parameters
            .Where(p => AttributeReader.ParameterRole(p) == "Edit")
            .Select(p => p.Name)
            .ToImmutableHashSet();
        var returnIsSubject = SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var subjectReturn)
            && subjectReturn is not null
            && AttributeReader.ReturnRole(method) is "Create" or "Edit";

        foreach (var parameter in method.Parameters)
        {
            if (AttributeReader.ParameterRole(parameter) is not ("Reference" or "Consume"))
            {
                continue;
            }

            foreach (var subject in AttributeReader.ParameterSubjects(parameter))
            {
                var valid = subject == AttributeReader.ReturnSubject
                    ? returnIsSubject
                    : editParamNames.Contains(subject);
                if (!valid)
                {
                    var location = parameter.Locations.FirstOrDefault() ?? method.Locations.FirstOrDefault() ?? Location.None;
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.InvalidLineageSubject, location, subject, method.Name));
                }
            }
        }
```

(Confirm `using System.Collections.Immutable;` is present in `ScenarioAnalyzer.cs` — it is, per the existing `ImmutableArray`/`ImmutableHashSet` usage.)

- [ ] **Step 5: Implement — release tracking**

In `C:/dev/raun/src/Raun.Generator/AnalyzerReleases.Unshipped.md`, add one row after the `RAUN009` row, in the same pipe-delimited format:

```
RAUN010 | Raun.Usage | Error | Lineage subject must name a step subject
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test" --filter-method "*RAUN010*"`
Expected: PASS (5 tests).

- [ ] **Step 7: Run the full generator suite**

Run: `dotnet test "C:/dev/raun/test/Raun.Generator.Test"`
Expected: PASS — including `Valid_scenarios_produce_no_diagnostics` and the RAUN009 tests (BookWithLineage now declares valid `Subject.Return` subjects, so no RAUN010 noise there).

- [ ] **Step 8: Build clean + commit**

Run: `dotnet build "C:/dev/raun/Raun.slnx"` → 0 warnings (the release-tracking analyzer is satisfied by the new Unshipped row).
```bash
jj -R "C:/dev/raun" commit -m "feat(analyzer): RAUN010 — reject invalid lineage subjects"
```

---

## Task 7: Migrate the sample DSL + final green

**Files:**
- Modify: `C:/dev/raun/samples/AppointmentTests/AppointmentDsl.cs:89-90` (`CreateAppointment`)

**Interfaces:**
- Consumes: `Subject.Return` (Task 1).

- [ ] **Step 1: Migrate `CreateAppointment` to declare its subject**

In `C:/dev/raun/samples/AppointmentTests/AppointmentDsl.cs`, the `CreateAppointment` step currently reads
`public static Task<Appointment> CreateAppointment([References] Patient patient, [Consumes] Slot slot, ScenarioContext? ctx = null)`.
Change the two role attributes so the created `Appointment` is the lineage subject (the method has `[return: Creates]`):

```csharp
    public static Task<Appointment> CreateAppointment([References(Subject.Return)] Patient patient, [Consumes(Subject.Return)] Slot slot, ScenarioContext? ctx = null)
```

(Leave the rest of the signature/body unchanged. Confirm `AppointmentDsl.cs` has `using Raun;` — it does, given it already uses `[References]`.)

- [ ] **Step 2: Verify the whole solution builds and all tests pass**

Run: `dotnet build "C:/dev/raun/Raun.slnx"`
Expected: build succeeds, **0 warnings** (includes the samples project; RAUN010 sees valid `Subject.Return` subjects).

Run, in turn:
- `dotnet test "C:/dev/raun/test/Raun.Test"`
- `dotnet test "C:/dev/raun/test/Raun.Generator.Test"`
- `dotnet test "C:/dev/raun/test/Raun.Mtp.Test"`

Expected: ALL PASS. If any other test project exists under `C:/dev/raun/test`, run it too.

- [ ] **Step 3: Commit**

```bash
jj -R "C:/dev/raun" commit -m "chore(samples): declare explicit lineage subjects on CreateAppointment"
```

---

## Self-review (completed against the spec)

- **§2 API** → Task 1 (attributes + `Subject.Return`). ✅
- **§5 RAUN010** → Task 6 (descriptor + register + validate + release tracking + tests). ✅
- **§6 lowering/runtime** → `ParameterSubjects`/`SubjectExpressions`/`BuildResourceClaims`/emitter (Task 4), `ResourceContext` edge recording + `ResourceLineageEdge` (Task 2), `StepResult.Edges` + scheduler (Tasks 3, 5), builder mapping (Task 3). ✅
- **§7 output shape preserved** → Task 3 keeps `ReportReference` fields and dedup; builder json test unaffected. ✅
- **§8 migration** → `BookWithLineage` (Task 4, needed for its tests), `CreateAppointment` (Task 7), the four builder tests rewritten (Task 3). The only existing `[References]`/`[Consumes]` usages in the repo are those two plus the test fixture; all are covered. ✅
- **§3 edit-in-place / no `Suspend` migration** → `Suspend` is untouched (no `[References]`); effect semantics unchanged. ✅
- **Type consistency:** `ResourceLineageEdge { Subject, Target, Kind }` is defined in Task 2 and consumed identically in Tasks 3/5; `Edges` property name consistent across `ResourceContext`/`StepResult`; `ReturnSubject`/`Subject.Return` both `"<return>"`. ✅
- **Placeholder scan:** none — every code step shows complete code. ✅
