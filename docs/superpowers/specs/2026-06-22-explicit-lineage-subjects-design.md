# Design: Explicit lineage subjects + RAUN010

**Date:** 2026-06-22
**Status:** Design — awaiting user review (then writing-plans → implement)
**Relationship to prior work:** Revises the subject-resolution half of the shipped
resource-reference-lineage feature (`docs/superpowers/specs/2026-06-21-resource-reference-lineage-design.md`,
on `main` @ `b6f29308`). Effect tracking and the `references` report data **shape** are unchanged; only
*how lineage edges are determined* changes — from runtime inference to explicit declaration.

---

## 1. Problem

A lineage edge connects a **subject** (a resource the step creates/edits) to a **target** (a resource it
`[References]`/`[Consumes]`). On `main` the subject is **never declared — it is reconstructed at runtime**:
`HtmlReportModelBuilder` collects every `Create`/`Edit` effect in a step, dedups by `ResourceIdentity`, and
*only if exactly one survives* treats it as the subject (`subjects.Count != 1 → skip`,
`src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs` ~line 114–158).

Two consequences the user rejected:

1. **Silent drops.** A step that *declares* `[References]`/`[Consumes]` but resolves to 0 or ≥2 subjects
   silently produces no edge. A declared relationship vanishing without a word is the core complaint.
2. **Inference / heuristics.** Because the subject is a runtime concept, any compile-time guard must
   *predict* the runtime dedup. That prediction is unavoidably heuristic: it cannot tell an edit-in-place
   (`[Edits] T` param + `[return: Edits] T`, one identity) from a genuine two-subject step, nor whether two
   same-typed `[Edits]` params are one instance or two. "Where did that edge come from?" is never obvious.

## 2. Decision

Make lineage **explicit and opt-in**. A target names its subject(s) directly in source; the runtime records
the edge from the named instances; the analyzer validates the declaration at compile time. No inference, no
identity prediction. Concretely:

- Add an optional `params string[] subjects` to `[References]` / `[Consumes]`. Each entry is a parameter name
  (via `nameof`) or the `Subject.Return` sentinel.
- **No `subjects` ⇒ no edge** (the effect is still recorded for C1/C2 semantics). Lineage is opt-in per target.
- **RAUN010 (Error)** fires when a named subject does not resolve to a real subject of the step.
- Existing role/effect attributes (`[Creates]`, `[Edits]`, `[Loads]`, `[Reads]`, `[Deletes]`,
  `[return: Edits]`) keep their current meaning. Lineage is **fully decoupled** from effect inference — so
  there is no edit-in-place special case and **no `Suspend` migration**.

This eliminates the F1 (distinct-type), F3 (in-place merge), and F4 (same-type-multi-subject) ambiguities
entirely: there is nothing to infer.

## 3. Model & rules

- **Subjects of a step** = each parameter with `[Edits]`, plus the return when it carries `[Creates]` or
  `[Edits]`. Each is named: a parameter by its identifier; the return by the `Subject.Return` sentinel.
- **Targets** = parameters with `[References]` or `[Consumes]`.
- **Edges** = for each target, one edge `subject → target` per entry in its `subjects` list, with
  `Kind = Reference | Consume`. Two subjects on one target ⇒ two edges (fully supported).
- **Opt-in:** a target with an empty/absent `subjects` list records its `Reference`/`Consume` effect but
  produces **no edge**.
- **Compile-time validity:** every `subjects` entry must resolve to a subject of the same step (an `[Edits]`
  parameter, or `Subject.Return` when the return is `[Creates]`/`[Edits]`). Otherwise → **RAUN010**.

### Worked examples

| Step | `subjects` | Edges |
|---|---|---|
| `Book([References] Patient p, [Consumes] Slot s) -> [return: Creates] Appt` with `p:[References(Subject.Return)]`, `s:[Consumes(Subject.Return)]` | Return | `Appt→p` (Reference), `Appt⇝s` (Consume) |
| `AssignOwner([Edits] Account acc, [References(nameof(acc))] User u)` | `acc` | `acc→u` |
| `Transfer([Edits] Account from, [Edits] Account to, [References(nameof(from), nameof(to))] User bank)` | `from`,`to` | `from→bank`, `to→bank` |
| `Suspend([Edits] User u, [References] Policy pol)` *(no subjects)* | — | none (effect only) |
| `Reassign([Edits] Appt appt, [References(nameof(zzz))] Patient p)` — `zzz` is not a subject | invalid | **RAUN010** |
| `Validate([References(Subject.Return)] Patient p)` — no `[Creates]`/`[Edits]` return | invalid | **RAUN010** |

## 4. Public API (`src/Raun/Resources/ResourceRoleAttributes.cs`)

```csharp
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ReferencesAttribute(params string[] subjects) : Attribute
{
    public string[] Subjects { get; } = subjects;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ConsumesAttribute(params string[] subjects) : Attribute
{
    public string[] Subjects { get; } = subjects;
}

/// <summary>Well-known lineage subjects for [References]/[Consumes].</summary>
public static class Subject
{
    /// <summary>The step's [Creates]/[Edits] return value, as a lineage subject.</summary>
    public const string Return = "<return>"; // reserved token; no C# parameter can be named this
}
```

- `params string[]` is valid in an attribute constructor; `nameof(...)` and `const string` are
  compile-time constants (the only thing attribute args allow). `[References]` with no args compiles to an
  empty array → effect only, no edge — fully backward compatible at the syntax level.
- `Subjects` is a single `string[]` because parameters can only be referenced from an attribute as strings
  (`nameof(x)` → `"x"`); the return sentinel shares that string property as a reserved const.
- The `"<return>"` token cannot collide with a parameter name (not a legal identifier).

## 5. Compile-time validation — RAUN010 (`Descriptors.cs` + `ScenarioAnalyzer.cs`)

Add to `Descriptors.cs` (next code after RAUN009):

```csharp
public static readonly DiagnosticDescriptor InvalidLineageSubject = new(
    "RAUN010",
    "Lineage subject must name a step subject",
    "'{0}' is not a valid lineage subject for step '{1}' — Subject must name an [Edits] parameter "
        + "or the [Creates]/[Edits] return (use Subject.Return)",
    Category,
    DiagnosticSeverity.Error,
    isEnabledByDefault: true);
```

Register it in `ScenarioAnalyzer.SupportedDiagnostics`. Extend `AnalyzeStepResources` (~line 351): for each
parameter whose role is `Reference`/`Consume`, read its `subjects` (`attr.ConstructorArguments[0]` — the
params array) and validate each entry:

- `Subject.Return` (`"<return>"`) is valid **iff** `AttributeReader.ReturnRole(method)` is `Create` or `Edit`.
- any other string is valid **iff** it names a parameter of the method whose `ParameterRole` is `Edit`.
- otherwise report **RAUN010** with `{0}` = the offending entry, `{1}` = step name, squiggle on the
  parameter (`parameter.Locations.FirstOrDefault() ?? method.Locations.FirstOrDefault() ?? Location.None`).

Add a RAUN010 row to `src/Raun.Generator/AnalyzerReleases.Unshipped.md`.

> Note: 0-subject and same-type-multi-subject ambiguities no longer need diagnostics — they cannot arise.
> A target either names valid subjects (edges drawn) or names none (no edge) or names something invalid
> (RAUN010). The runtime `subjects.Count != 1` guard is removed (replaced by recorded edges, §6). This
> supersedes the predecessor handoff's "keep the runtime guard as defense-in-depth" decision, which assumed
> the inference model — with edges recorded directly from named instances there is no inference to guard.

## 6. Lowering & runtime

**Reading subjects (`AttributeReader.cs`).** Add `ParameterSubjects(IParameterSymbol)` returning the
`Reference`/`Consume` attribute's params-array values (empty when none).

**Lowering (`ScenarioParser.BuildResourceClaims`, ~line 453).** Today each role param becomes
`ResourceRoleClaim(role, expression, IsReturn)` where `expression` is the rewritten argument
(`__inputs.Get<T>(i)` or `__r`). Extend the claim with `SubjectExpressions: IReadOnlyList<string>`. For a
`Reference`/`Consume` claim, map each subject name to an instance expression:

- a parameter name → that parameter's own rewritten argument expression (the same value computed when
  building that parameter's `[Edits]` claim — look it up by name);
- `Subject.Return` → `"__r"`.

(Subjects are validated by RAUN010, so lowering can assume they resolve; defensively skip unresolved.)

**Emitting (`ScenarioEmitter`, `BuildInvokeLambda`/`ResourceCallStatement`, ~line 220–291).** The invoke
lambda already emits `var __r = await CALL;` then one `await __ctx.Resources.{Verb}({arg});` per claim, with
both `__inputs[...]` and `__r` in scope. For a `Reference`/`Consume` claim **with** subject expressions, emit
the edge form, e.g.:

```csharp
await __ctx.Resources.Reference(<targetArg>, <subjectArg1>, <subjectArg2>); // params object[] subjects
```

A claim with no subjects emits exactly today's `await __ctx.Resources.Reference(<targetArg>);` — byte-identical.

**Recording (`ResourceContext.cs`).** Add subject overloads to `Reference`/`Consume`:
`Reference<T>(T resource, params object[] subjects)`. They record the `Reference`/`Consume` effect as today,
then for each subject resolve its identity (`_resolver.Resolve(subject)`) and append a
`ResourceLineageEdge { SubjectIdentity, TargetIdentity = resolved(resource), Kind }` (new record in
`Raun.Model`, alongside `ResourceEffect`) to a new per-step `_edges` list, exposed as
`IReadOnlyList<ResourceLineageEdge> Edges` (mirrors `Effects`). The exact subject-overload signature
(`params object[]` vs a typed path) and whether `_resolver.Resolve` needs a non-generic entry point for
`object` subjects are settled in writing-plans.

**Report build (`HtmlReportModelBuilder`).** Replace the subject-inference loop (~114–158) with: read each
step's recorded `Edges`, dedup by `(SubjectType, SubjectKey, TargetType, TargetKey)` across the scenario
(as today), and map to `ReportReference`. The aggregation that currently carries per-step `Effects` into the
run model carries `Edges` the same way.

## 7. Report output — unchanged shape

`ReportReference` (`HtmlReportModel.cs`) keeps its fields: `SubjectType`, `SubjectKey`, `TargetType`,
`TargetKey`, `Kind` (singular `"Reference"`/`"Consume"`). The serialized `references` array is identical in
shape and semantics. **A parallel report agent renders this data** (`report-template.html`); preserving the
output shape keeps it insulated — it consumes the same edge list regardless of how edges are produced.

## 8. Migration / compatibility

The shipped feature drew edges by inference; explicit-only removes that. Therefore:

- **Behavior change:** existing `[References]`/`[Consumes]` usages that relied on implicit subject derivation
  stop drawing edges until they add `subjects`. This is intended (opt-in).
- **Action (enumerated during writing-plans):** locate every existing `[References]`/`[Consumes]` usage and
  every test asserting derived edges (e.g. fixtures in `test/`, the report-builder lineage tests, any sample
  in `SampleSources.cs`), and migrate them to declare explicit `subjects`. Effect-only assertions are
  unaffected.
- No change to `[Creates]`/`[Edits]`/`[Loads]`/`[Reads]`/`[Deletes]` semantics or their tests.

## 9. Testing (TDD; `test/Raun.Generator.Test` + report-builder tests)

- **Analyzer:** RAUN010 fires for an entry that names a non-subject param, a non-existent name, and
  `Subject.Return` without a `[Creates]`/`[Edits]` return; RAUN010 does **not** fire for valid single
  subject, valid multi-subject, and a `[References]` with no subjects (mirror existing RAUN009 tests).
- **Lowering/emit:** snapshot the invoke lambda — no-subjects claim is byte-identical to today; single- and
  multi-subject claims emit the edge call with correct instance expressions (params, `__r`).
- **Runtime/builder:** a step recording `from→bank` and `to→bank` yields two `references`; a no-subjects
  reference yields none; cross-step dedup holds; the `references` JSON shape is unchanged (golden test).
- Full `dotnet build Raun.slnx` clean (0 warnings) and full test pass before completion.

## 10. Out of scope / future

- **Dynamic (runtime) lineage** for the rare cases attributes can't express (the "2%") — explicitly deferred.
- No new edge kinds, no per-edge metadata, no cross-step subject references (a subject must be a subject of
  the *same* step).

## Appendix — environment

Work proceeds in an isolated `jj` workspace off `main` (`C:/dev/raun`, driven via
`jj -R "C:/dev/raun"`). Version control is `jj`, never `git`; no `Co-Authored-By` trailers.
Tests via `dotnet test "C:/dev/raun/test/Raun.Generator.Test"` (MTP; filter with
`--filter-method "*Name*"`).
