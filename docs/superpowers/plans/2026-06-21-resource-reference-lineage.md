# Resource Reference Lineage (data side) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture resource→resource lineage by adding two parameter roles — `[References]` (aggregation) and `[Consumes]` (composition) — that record shared effect verbs and are derived into a `ReportReference` adjacency on the HTML report model.

**Architecture:** `[References]`/`[Consumes]` become first-class `LifecycleVerb`s in the Read family (shared lock). They flow through the *existing* effect plumbing (attribute → `AttributeReader` verb string → emitted `ctx.Resources.<Verb>` call → `ResourceEffect`). Lineage edges are **not stored** — `HtmlReportModelBuilder` derives them per step by pairing the step's `Create`/`Edit` effect (the subject) with its `Reference`/`Consume` effects.

**Tech Stack:** .NET 10, C# (preview lang), Roslyn source generator, xUnit v3 (plain `Assert`), Verify.XunitV3 for snapshots, Microsoft.Testing.Platform.

## Global Constraints

- **Version control is `jj`, never `git`.** Commit with `jj commit <paths> -m "..."` (path-scoped, so the untracked `docs/superpowers/handoffs/...` file stays out). Read-only `git` is fine.
- **No `Co-Authored-By` / tooling trailers** in commit messages.
- **Verb naming convention** (existing): the attribute is third-person (`[Reads]`/`ReadsAttribute`); the `LifecycleVerb` enum member, the `ResourceContext` method, and the `AttributeReader` verb string are the base form (`Read`). So `[References]`→`Reference`, `[Consumes]`→`Consume`.
- **`[Consumes]`/`[References]` are `Shared` (no scheduling weight) in C1.** The exclusivity `[Consumes]` implies is C2 — documented in comments only, enforced nowhere. There is no scheduler/lock change in this plan.
- **Edges are derived, never stored.** No `ResourceReference` runtime type; no new `StepResult` field.
- **Single-subject limitation** must be stated in the `[References]`/`[Consumes]` XML doc-comments (IntelliSense): the edge attaches to the step's single created/edited resource; a step that creates/edits more than one resource forms no edge.
- Build: `dotnet build Raun.slnx`. Test: `dotnet test` (whole solution) or `dotnet test test/<Project>`. Source-gen changes take effect on the next build.

## File Structure

**Production (modified):**
- `src/Raun/Resources/LifecycleVerb.cs` — add `Reference`, `Consume` enum values; extend `ToLockMode` (Shared) and `Precedence`.
- `src/Raun/Resources/ResourceContext.cs` — add `Reference<T>` / `Consume<T>` tracer methods.
- `src/Raun/Resources/ResourceRoleAttributes.cs` — add `ReferencesAttribute`, `ConsumesAttribute`.
- `src/Raun.Generator/Lowering/AttributeReader.cs` — map the two new attributes to verb strings.
- `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs` — add `ReportReference` record; add `References` to `ReportScenario`.
- `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs` — derive `ReportReference` edges per step.
- `samples/AppointmentTests/AppointmentDsl.cs` — upgrade `CreateAppointment` to the new roles (living demo).

**Tests (modified):**
- `test/Raun.Test/Resources/ResourceContextTests.cs` — tracer + dedup tests.
- `test/Raun.Generator.Test/SampleSources.cs` — add a lineage DSL step + scenario.
- `test/Raun.Generator.Test/ResourceLoweringTests.cs` — end-to-end lowering test.
- `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs` — edge-derivation tests; re-accept the JSON snapshot.

**Handoff (do NOT touch — owned by the report agent):** `src/Raun.Mtp/HtmlReport/report-template.html`. The contract is the camelCase-serialized `references` array on each scenario (via `HtmlReportSink`'s existing `JsonNamingPolicy.CamelCase`).

---

### Task A: New lifecycle verbs + tracer methods

Runtime can record `Reference`/`Consume` as shared effects, with correct dedup precedence.

**Files:**
- Modify: `src/Raun/Resources/LifecycleVerb.cs`
- Modify: `src/Raun/Resources/ResourceContext.cs:69-82`
- Test: `test/Raun.Test/Resources/ResourceContextTests.cs`

**Interfaces:**
- Produces: `LifecycleVerb.Reference`, `LifecycleVerb.Consume` (both `ToLockMode() == LockMode.Shared`; `Precedence()` above `Read`). `ResourceContext.Reference<T>(T)` and `Consume<T>(T)` returning `ValueTask`.

- [ ] **Step 1: Write the failing tests**

Add these three tests to `test/Raun.Test/Resources/ResourceContextTests.cs` (before the `FixedTimeProvider` nested class):

```csharp
[Fact]
public async Task Reference_records_a_shared_effect_with_resolved_identity()
{
    var ctx = NewContext(out _);

    await ctx.Reference(new User("jane@acme.com"));

    var effect = Assert.Single(ctx.Effects);
    Assert.Equal(LifecycleVerb.Reference, effect.Verb);
    Assert.Equal(LockMode.Shared, effect.Mode);
    Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), effect.Identity);
}

[Fact]
public async Task Consume_records_a_shared_effect_with_resolved_identity()
{
    var ctx = NewContext(out _);

    await ctx.Consume(new User("jane@acme.com"));

    var effect = Assert.Single(ctx.Effects);
    Assert.Equal(LifecycleVerb.Consume, effect.Verb);
    Assert.Equal(LockMode.Shared, effect.Mode);
    Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), effect.Identity);
}

[Fact]
public async Task Consume_outranks_read_in_dedup()
{
    var ctx = NewContext(out _);
    var user = new User("jane@acme.com");

    await ctx.Read(user);
    await ctx.Consume(user);

    // Same identity ⇒ one effect; the usage verb (Consume) outranks plain Read.
    var effect = Assert.Single(ctx.Effects);
    Assert.Equal(LifecycleVerb.Consume, effect.Verb);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Test --filter "Name~Reference_records|Name~Consume_records|Name~Consume_outranks"`
Expected: FAIL — `ResourceContext` has no `Reference`/`Consume` method; `LifecycleVerb.Reference`/`.Consume` do not exist (compile error).

- [ ] **Step 3: Add the enum values + lock/precedence**

In `src/Raun/Resources/LifecycleVerb.cs`, append two members to the enum (after `Delete,` — appending keeps existing numeric values stable):

```csharp
    /// <summary>Removes a resource (exclusive).</summary>
    Delete,

    /// <summary>References an independently-living resource — a durable pointer the produced
    /// resource keeps (aggregation; shared). Carries a lineage edge in the report.</summary>
    Reference,

    /// <summary>Consumes/uses-up a resource into the one the step produces (composition; shared in
    /// C1, exclusive in C2). Carries a lineage edge in the report.</summary>
    Consume,
}
```

Replace `ToLockMode` so the two new verbs are explicitly `Shared` (the method defaults to `Exclusive`, so omitting them would silently make them exclusive):

```csharp
    public static LockMode ToLockMode(this LifecycleVerb verb) => verb switch
    {
        LifecycleVerb.Read or LifecycleVerb.Load
            or LifecycleVerb.Reference or LifecycleVerb.Consume => LockMode.Shared,
        _ => LockMode.Exclusive,
    };
```

Replace `Precedence` so the usage verbs sit just above `Read` (and update the doc-comment):

```csharp
    /// <summary>
    /// Lifecycle precedence for dedup (higher wins):
    /// Delete &gt; Edit &gt; Create &gt; Load &gt; Consume &gt; Reference &gt; Read.
    /// All exclusive verbs (Create/Edit/Delete) still outrank all shared ones, so exclusive wins
    /// over shared on the same identity automatically.
    /// </summary>
    public static int Precedence(this LifecycleVerb verb) => verb switch
    {
        LifecycleVerb.Delete => 7,
        LifecycleVerb.Edit => 6,
        LifecycleVerb.Create => 5,
        LifecycleVerb.Load => 4,
        LifecycleVerb.Consume => 3,
        LifecycleVerb.Reference => 2,
        _ => 1,
    };
```

- [ ] **Step 4: Add the tracer methods**

In `src/Raun/Resources/ResourceContext.cs`, after the `Read<T>` method (line 72), add:

```csharp
    /// <summary>Records the produced resource keeping a durable reference to <paramref name="resource"/> (shared).</summary>
    public ValueTask Reference<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Reference, _resolver.Resolve(resource), resource);

    /// <summary>Records consuming/using-up <paramref name="resource"/> into the produced resource (shared in C1).</summary>
    public ValueTask Consume<T>(T resource)
        where T : notnull
        => Record(LifecycleVerb.Consume, _resolver.Resolve(resource), resource);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/Raun.Test --filter "Name~Reference_records|Name~Consume_records|Name~Consume_outranks"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
jj commit src/Raun/Resources/LifecycleVerb.cs src/Raun/Resources/ResourceContext.cs test/Raun.Test/Resources/ResourceContextTests.cs -m "feat: Reference/Consume lifecycle verbs (shared) + tracer methods"
```

---

### Task B: Attributes + generator wiring

`[References]`/`[Consumes]` on a parameter lower into `await __ctx.Resources.Reference/Consume(arg)`, producing shared effects in declaration order.

**Files:**
- Modify: `src/Raun/Resources/ResourceRoleAttributes.cs`
- Modify: `src/Raun.Generator/Lowering/AttributeReader.cs:76-97`
- Test: `test/Raun.Generator.Test/SampleSources.cs`
- Test: `test/Raun.Generator.Test/ResourceLoweringTests.cs`

**Interfaces:**
- Consumes: `LifecycleVerb.Reference`/`.Consume`, `ResourceContext.Reference`/`.Consume` (Task A).
- Produces: `[References]`/`[Consumes]` parameter attributes; `AttributeReader.ParameterRole` returns `"Reference"`/`"Consume"`. No change to `ScenarioParser`, `Ir`, or `ScenarioEmitter` — they are verb-string-driven.

- [ ] **Step 1: Add the lineage DSL step + scenario to the test sources**

In `test/Raun.Generator.Test/SampleSources.cs`, inside the `ResourceDsl` const's `extension(When)` block, add a step after `Book` (note: do NOT modify `Book` — existing tests assert on it):

```csharp
                [StepName("booking with lineage")]
                [return: Creates]
                public static async Task<Appointment> BookWithLineage([References] User user, [Consumes] Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(user, slot);
                }
```

Then add a new scenario const after `BookingScenario`:

```csharp
    // Scenario appended to ResourceDsl: exercises the lineage roles ([References] User, [Consumes]
    // Slot) plus [return: Creates], proving they lower to shared Reference/Consume effects.
    public const string LineageScenario =
        """

        public static class LineageResourceScenarios
        {
            [Scenario("booking with lineage")]
            public static async Task BookWithLineage()
            {
                var user = await Given.UserExists("jane@acme.com");
                var slot = await Given.SlotExists();
                var appt = await When.BookWithLineage(user, slot);
            }
        }
        """;
```

- [ ] **Step 2: Write the failing test**

In `test/Raun.Generator.Test/ResourceLoweringTests.cs`, add (after `Multi_param_roles_emit_in_param_then_return_order`):

```csharp
[Fact]
public async Task Reference_and_consume_params_lower_to_shared_lineage_effects()
{
    var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.LineageScenario);
    result.AssertCompiles();
    var results = await result.Definitions().Single().RunAsync();

    // Step 2: When.BookWithLineage([References] User, [Consumes] Slot) [return: Creates] —
    // effects appear in param-declaration order, then the return role.
    var book = results[2].Effects;
    Assert.Equal(3, book.Count);

    Assert.Equal(LifecycleVerb.Reference, book[0].Verb);
    Assert.Equal(LockMode.Shared, book[0].Mode);
    Assert.Equal("User:jane@acme.com", book[0].Identity.ToString());

    Assert.Equal(LifecycleVerb.Consume, book[1].Verb);
    Assert.Equal(LockMode.Shared, book[1].Mode);
    Assert.Equal("Slot:1", book[1].Identity.ToString());

    Assert.Equal(LifecycleVerb.Create, book[2].Verb);
    Assert.Equal("Appointment:jane@acme.com@1", book[2].Identity.ToString());
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test test/Raun.Generator.Test --filter "Name~Reference_and_consume_params"`
Expected: FAIL — the source string won't compile (no `[References]`/`[Consumes]` attributes), so `AssertCompiles` fails.

- [ ] **Step 4: Add the attributes**

In `src/Raun/Resources/ResourceRoleAttributes.cs`, after `ReadsAttribute` (line 13), add:

```csharp
/// <summary>
/// Parameter role: the produced resource keeps a durable reference to this one (aggregation; shared).
/// Records a <see cref="LifecycleVerb.Reference"/> effect and, paired with the step's
/// <c>[Creates]</c>/<c>[Edits]</c> subject, a lineage edge subject→target in the report. The edge is
/// attributed to the step's SINGLE created/edited resource; a step that creates or edits more than
/// one resource forms no lineage edge for its referenced inputs.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ReferencesAttribute : Attribute;

/// <summary>
/// Parameter role: the step consumes/uses-up this resource into the one it produces (composition;
/// shared in C1, exclusive in C2). Records a <see cref="LifecycleVerb.Consume"/> effect and, paired
/// with the step's <c>[Creates]</c>/<c>[Edits]</c> subject, a lineage edge subject→target in the
/// report. The edge is attributed to the step's SINGLE created/edited resource; a step that creates
/// or edits more than one resource forms no lineage edge for its consumed inputs.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ConsumesAttribute : Attribute;
```

- [ ] **Step 5: Map the attributes to verbs**

In `src/Raun.Generator/Lowering/AttributeReader.cs`, add two cases to the `RoleVerb` switch (inside the `foreach`, alongside `"ReadsAttribute"`):

```csharp
            var verb = attr.AttributeClass?.Name switch
            {
                "ReadsAttribute" when parameterRoles => "Read",
                "ReferencesAttribute" when parameterRoles => "Reference",
                "ConsumesAttribute" when parameterRoles => "Consume",
                "DeletesAttribute" when parameterRoles => "Delete",
                "EditsAttribute" => "Edit",
                "CreatesAttribute" when !parameterRoles => "Create",
                "LoadsAttribute" when !parameterRoles => "Load",
                _ => null,
            };
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test test/Raun.Generator.Test --filter "Name~Reference_and_consume_params"`
Expected: PASS. (If the generator change seems not to take effect, force a rebuild: `dotnet build Raun.slnx` then re-run.)

- [ ] **Step 7: Run the full generator suite (no regressions)**

Run: `dotnet test test/Raun.Generator.Test`
Expected: PASS — existing snapshots unchanged (the new DSL step is unused by existing scenarios; the generator only emits `[Scenario]` methods).

- [ ] **Step 8: Commit**

```bash
jj commit src/Raun/Resources/ResourceRoleAttributes.cs src/Raun.Generator/Lowering/AttributeReader.cs test/Raun.Generator.Test/SampleSources.cs test/Raun.Generator.Test/ResourceLoweringTests.cs -m "feat: [References]/[Consumes] parameter roles lower to lineage effects"
```

---

### Task C: Derive lineage edges into the report model

`HtmlReportModelBuilder` produces a `ReportReference` adjacency per scenario by pairing each step's Create/Edit subject with its Reference/Consume effects.

**Files:**
- Modify: `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`
- Modify: `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs:113-131`
- Test: `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`

**Interfaces:**
- Consumes: `LifecycleVerb.Reference`/`.Consume`/`.Create`/`.Edit` (Task A), `ResourceEffect.Identity`/`.Verb`.
- Produces: `ReportReference { SubjectType, SubjectKey, TargetType, TargetKey, Kind }`; `ReportScenario.References : IReadOnlyList<ReportReference>`. Serialized camelCase (`references`, `subjectType`, …) by `HtmlReportSink` — the report agent's contract.

- [ ] **Step 1: Write the failing tests**

In `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs`, add `using System.Linq;` to the top usings, then add these two tests:

```csharp
[Fact]
public void References_and_consumes_derive_lineage_edges_from_the_step_subject()
{
    var n0 = Node(0, "c", "When", "When creating an appointment");
    var def = Def(n0);
    var appointment = new ResourceIdentity(typeof(string), "appt-1");
    var patient = new ResourceIdentity(typeof(string), "Jane");
    var slot = new ResourceIdentity(typeof(int), "7");

    var builder = new HtmlReport.HtmlReportModelBuilder();
    builder.OnScenarioStarted(def);
    builder.OnStepFinished(def, Result(n0, T0, 10, effects:
    [
        new ResourceEffect { Verb = LifecycleVerb.Reference, Identity = patient, StepId = "c", Timestamp = T0.AddMilliseconds(1) },
        new ResourceEffect { Verb = LifecycleVerb.Consume, Identity = slot, StepId = "c", Timestamp = T0.AddMilliseconds(2) },
        new ResourceEffect { Verb = LifecycleVerb.Create, Identity = appointment, StepId = "c", Timestamp = T0.AddMilliseconds(3) },
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
public void A_reference_effect_without_a_subject_yields_no_edge()
{
    var n0 = Node(0, "t", "Then", "Then the appointment should exist");
    var def = Def(n0);
    var patient = new ResourceIdentity(typeof(string), "Jane");

    var builder = new HtmlReport.HtmlReportModelBuilder();
    builder.OnScenarioStarted(def);
    builder.OnStepFinished(def, Result(n0, T0, 10, effects:
    [
        new ResourceEffect { Verb = LifecycleVerb.Reference, Identity = patient, StepId = "t", Timestamp = T0.AddMilliseconds(1) },
    ]));

    var scenario = Assert.Single(builder.Build("x").Scenarios);
    Assert.Empty(scenario.References);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Raun.Mtp.Test --filter "Name~derive_lineage_edges|Name~without_a_subject"`
Expected: FAIL — `ReportScenario` has no `References` property (compile error).

- [ ] **Step 3: Add the model types**

In `src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`, add a `References` property to `ReportScenario` (after `Resources`):

```csharp
    public required IReadOnlyList<ReportResource> Resources { get; init; }
    public required IReadOnlyList<ReportReference> References { get; init; }
}
```

And add a new record (place it after `ReportResourceEvent` at the end of the file):

```csharp
/// <summary>One resource→resource lineage edge, derived from a step's Create/Edit subject and its
/// Reference/Consume effects. Endpoints are (Type, Key) pairs matching <see cref="ReportResource"/>.</summary>
public sealed record ReportReference
{
    public required string SubjectType { get; init; }
    public required string SubjectKey { get; init; }
    public required string TargetType { get; init; }
    public required string TargetKey { get; init; }
    public required string Kind { get; init; } // "Reference" | "Consume" (from the verb)
}
```

- [ ] **Step 4: Derive the edges in the builder**

In `src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`, inside `ScenarioAccumulator.Build()`, after the `resources` block (ends line 111) and before the `status` line, insert:

```csharp
            // Lineage edges (2026-06-21 spec): per step, the Create/Edit effect is the subject; each
            // Reference/Consume effect is a target. Derived here, not stored. A step with no subject
            // yields no edge; deduped by (subject, target) across the scenario.
            var references = new List<ReportReference>();
            var seenEdges = new HashSet<(string, string, string, string)>();
            foreach (var r in ordered)
            {
                var subject = r.Effects.FirstOrDefault(
                    e => e.Verb is LifecycleVerb.Create or LifecycleVerb.Edit);
                if (subject is null)
                {
                    continue;
                }

                foreach (var e in r.Effects)
                {
                    if (e.Verb is not (LifecycleVerb.Reference or LifecycleVerb.Consume))
                    {
                        continue;
                    }

                    var subjectType = subject.Identity.Type.Name;
                    var subjectKey = subject.Identity.Key.ToString();
                    var targetType = e.Identity.Type.Name;
                    var targetKey = e.Identity.Key.ToString();
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
                        Kind = e.Verb.ToString(),
                    });
                }
            }
```

Then add `References = references,` to the `new ReportScenario { ... }` initializer (after `Resources = resources,`):

```csharp
                Steps = steps,
                Resources = resources,
                References = references,
            };
```

- [ ] **Step 5: Run the new tests to verify they pass**

Run: `dotnet test test/Raun.Mtp.Test --filter "Name~derive_lineage_edges|Name~without_a_subject"`
Expected: PASS (2 tests).

- [ ] **Step 6: Re-accept the JSON snapshot (additive `references` field)**

The `Builds_the_expected_json_model` snapshot now gains a `"References": []` array on its scenario. Run:

`dotnet test test/Raun.Mtp.Test --filter "Name~Builds_the_expected_json_model"`
Expected: FAIL — Verify reports a diff. A `*.received.txt` appears next to `test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt`.

Read the `.received.txt` and confirm the ONLY change is the added `"References": []` (no other field changed). Then accept by overwriting the verified file:

```bash
mv "test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.received.txt" \
   "test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt"
```

Re-run to confirm: `dotnet test test/Raun.Mtp.Test` → PASS (whole project).

- [ ] **Step 7: Commit**

```bash
jj commit src/Raun.Mtp/HtmlReport/HtmlReportModel.cs src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs test/Raun.Mtp.Test/HtmlReportModelBuilderTests.cs "test/Raun.Mtp.Test/HtmlReportModelBuilderTests.Builds_the_expected_json_model.verified.txt" -m "feat: derive ReportReference lineage edges in the report model"
```

---

### Task D: Upgrade the sample (living demo) + full green

The sample's `CreateAppointment` declares the new roles, so a real run produces lineage edges end-to-end.

**Files:**
- Modify: `samples/AppointmentTests/AppointmentDsl.cs:99-106`

**Interfaces:**
- Consumes: `[References]`/`[Consumes]` attributes (Task B).

- [ ] **Step 1: Upgrade `CreateAppointment`**

In `samples/AppointmentTests/AppointmentDsl.cs`, change the `CreateAppointment` parameters from `[Reads]`/`[Reads]` to `[References]`/`[Consumes]`:

```csharp
        [StepName("When creating an appointment")]
        [return: Creates]
        public static Task<Appointment> CreateAppointment([References] Patient patient, [Consumes] Slot slot, ScenarioContext? ctx = null)
        {
            ctx?.SimulateElapsed(TimeSpan.FromMilliseconds(600));
            return Task.FromResult(new Appointment(patient, slot));
        }
```

Leave `AppointmentExists([Reads] Appointment ...)` unchanged — it only reads.

- [ ] **Step 2: Run the sample to verify it still passes**

Run: `dotnet test samples/AppointmentTests`
Expected: PASS — the sample's own assertions (`AppointmentExists` checks Patient/Slot non-null) are unaffected; the change swaps `Read` → `Reference`/`Consume` on those params and adds derived edges.

- [ ] **Step 3: Run the FULL solution suite + re-accept any other additive snapshots**

Run: `dotnet test`
Expected: PASS. If any *other* Verify snapshot fails ONLY because of the new additive `references` array (e.g. an end-to-end report snapshot), confirm the diff is additive-only and accept it the same way as Task C Step 6 (`mv` the `.received.` over the `.verified.`), then re-run `dotnet test` to confirm green. If a snapshot diff is NOT additive-only, stop and investigate — that's a real regression.

- [ ] **Step 4: Commit**

```bash
jj commit samples/AppointmentTests/AppointmentDsl.cs -m "sample: CreateAppointment uses [References] Patient / [Consumes] Slot"
```

If you accepted additional snapshot files in Step 3, include their paths in the `jj commit` path list.

---

## Self-Review

- **Spec coverage:** §"two new effect verbs" → Task A + B. §"edge derivation" → Task C. §"single-subject limitation documented for the user" → Task B Step 4 (attribute XML docs). §"ReportReference handoff contract" → Task C Step 3. §"sample as living demo" → Task D. §non-goals (no scheduling, no stored edge, no inference) → honored (verbs are `Shared`; no runtime edge type; explicit roles only). ✅
- **No generator/IR/emitter/parser edits:** confirmed unnecessary — `ScenarioEmitter.ResourceCallStatement` and `ScenarioParser.BuildResourceClaims` are verb-string-driven, so the two `AttributeReader` cases suffice.
- **Type consistency:** verb base form `Reference`/`Consume` used uniformly across `LifecycleVerb`, `ResourceContext` methods, `AttributeReader` strings, and `ReportReference.Kind` (`e.Verb.ToString()` → `"Reference"`/`"Consume"`). Attributes are `[References]`/`[Consumes]`.
- **Dedup precedence:** `Reference`(2)/`Consume`(3) above `Read`(1); all exclusive verbs (5–7) still outrank all shared (1–4). Covered by Task A's `Consume_outranks_read_in_dedup`.
- **Snapshot churn:** the additive `references` field changes the `Builds_the_expected_json_model` snapshot (Task C Step 6) and possibly other report snapshots (Task D Step 3); both handled with additive-only verification before acceptance.
