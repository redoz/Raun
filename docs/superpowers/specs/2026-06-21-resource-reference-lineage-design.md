# Resource reference lineage (data side)

**Date:** 2026-06-21
**Status:** Design — approved in brainstorming, pending spec review
**Scope of this spec:** the *data side* only. The report visualization (relations marker + hover chain-highlight) is being workshopped in parallel by a separate agent and is **out of scope here**. This spec produces the populated data contract that agent renders against.

## Problem

Raun records what each step does to resources as per-step `ResourceEffect`s — a verb (`Create`/`Load`/`Read`/`Edit`/`Delete`) against a `ResourceIdentity` (`Type` + `Key`). That captures *step → resource* actions, but not *resource → resource* structure. When `CreateAppointment([Reads] Patient, [Reads] Slot)` produces an `Appointment`, nothing records that the Appointment **references the Patient and the Slot**. We want that lineage: a graph of which resources are built from which, so the report can later show a connected data chain.

The relationship is a property of the produced *resource* (it outlives the step that created it), not of the step's action — so it is new information. But, crucially, it can be **derived** from per-step effects rather than stored as a separate edge (see Edge derivation).

## Goal

Capture resource→resource reference lineage, declared at the DSL via two new parameter roles, carried through the existing effect plumbing, and **derived** into a populated adjacency on the HTML report model. Stop before the template.

## Non-goals (explicitly out of scope)

- **Report visualization** — the relations marker, hover behavior, connected-component highlight, and all of `report-template.html`. Owned by the parallel report agent; it consumes the model this spec populates.
- **Scheduling / locking (C2).** There *is* a working DAG scheduler (`ScenarioScheduler.cs`) that parallelizes independent steps within a scenario, but it consumes **only** `DependsOn` (dataflow + source-order). It never reads `LockMode`; resource-lock-driven serialization is C2 and unbuilt, and scenarios run sequentially anyway (`RaunRunLoop.cs` — a plain `foreach`). So `[Consumes]` carries **no** scheduling weight in this spec. The exclusivity it implies is recorded as forward-looking intent in doc-comments only (matching the existing verbs' "(exclusive in C2)" style).
- **Inference.** Edges are explicit only — a `[Reads]` parameter draws nothing. No "every input to a creating step becomes an edge" heuristic.

## The model: two new effect verbs in the Read family

`[Consumes]`/`[References]` are **not** decomposed into a `Read` plus a separate edge. They *are* forms of reading (shared access — you cannot reference a resource without reading it), so they become **first-class effect verbs treated like `Read`**:

| Attribute | Verb (new) | Lock mode (C1) | UML | Role | C2 intent (doc only) |
|---|---|---|---|---|---|
| `[Reads]` (existing) | `Read` | Shared | — | param: read-only / validation | shared |
| `[References]` (new) | `Reference` | Shared | aggregation | param: durable pointer to an independently-living resource | shared |
| `[Consumes]` (new) | `Consume` | Shared | composition | param: input absorbed / used-up into the produced resource | exclusive |

Each tagged parameter records **one** effect on the target (verb `Reference` or `Consume`), via the same plumbing as `Read`. There is no redundant second effect. The verb itself carries the lineage kind; the target's resource lane shows `Consume`/`Reference` directly (more informative than a generic `Read`).

### Edge derivation (why no edge needs to be stored)

The edge's other endpoint is recoverable from the step, so it is **computed at report-build time, not stored**:

> Per step, **subject** = the identity of the step's `Create`/`Edit` effect. For each `Consume`/`Reference` effect in that step, draw an edge **subject → target** with the verb as its kind.

- `CreateAppointment` → `Create Appointment` (subject) + `Consume Slot` + `Reference Patient` ⇒ edges `Appointment → Slot` (Consume), `Appointment → Patient` (Reference).
- `AssignPatient([Edits] Appointment, [References] Patient)` → subject is the edited Appointment ⇒ `Appointment → Patient`.
- A `Consume`/`Reference` effect in a step with **no** `Create`/`Edit` subject ⇒ the effect still shows on the target's lane, but **no edge** (graceful: a touch with nothing to link it to).

### Single-subject limitation (must be documented for the user)

Edge derivation assumes a step mutates **one** subject (one `Create` or one `Edit`). With one subject, "which produced resource consumed the Slot" is unambiguous. **A step that creates/edits more than one resource cannot disambiguate which owns a consumed/referenced input, so no lineage edge is derived for that step.** This limitation MUST be surfaced to the test author — not buried in this spec:

- State it in the XML doc-comments on `[Consumes]` and `[References]` (visible in IntelliSense): the edge is attributed to the step's single created/edited resource; steps that produce multiple resources won't form lineage edges.
- Multi-subject support (explicit subject linkage) is deferred (see Deferred).

## Data model

Changed existing type — **`src/Raun/Resources/LifecycleVerb.cs`**:

- Add `Reference` and `Consume` to the `LifecycleVerb` enum, documented in the existing house style (`Reference`: shared; `Consume`: "used up; exclusive in C2").
- `ToLockMode` — **add explicit `Shared` cases** for both. (The method defaults to `Exclusive`, so omitting them would silently make these verbs exclusive — a trap.)
  `LifecycleVerb.Read or LifecycleVerb.Load or LifecycleVerb.Reference or LifecycleVerb.Consume => LockMode.Shared`
- `Precedence` — insert above `Read` so a usage verb wins a same-identity, same-step dedup against a plain `Read`. New ladder: `Read 1 < Reference 2 < Consume 3 < Load 4 < Create 5 < Edit 6 < Delete 7`. Relative order of existing verbs is preserved, and the "all exclusive (5–7) outrank all shared (1–4)" invariant still holds.

No new runtime model type, and **no new `StepResult` field** — references ride the existing `IReadOnlyList<ResourceEffect> Effects`.

Report-model addition (the handoff surface) — **`src/Raun.Mtp/HtmlReport/HtmlReportModel.cs`**:

```csharp
/// One resource→resource edge for the report's lineage view, derived from a step's
/// Create/Edit subject and its Consume/Reference effects. Endpoints are (Type, Key)
/// pairs matching ReportResource identity.
public sealed record ReportReference
{
    public required string SubjectType { get; init; }
    public required string SubjectKey { get; init; }
    public required string TargetType { get; init; }
    public required string TargetKey { get; init; }
    public required string Kind { get; init; } // "Reference" | "Consume" (from the verb)
}
```

…and the per-scenario report model gains `IReadOnlyList<ReportReference> References`. Endpoints reuse the same `(Type, Key)` identity the existing `ReportResource` nodes use, so the report agent joins edges to nodes directly. `HtmlReportModel.cs` is the **only file both this spec and the report agent touch** — a record addition, low collision risk.

## Capture path

Mirrors the existing effect plumbing at every hop — the runtime change is **2 attributes + 2 enum values + 2 `ResourceContext` methods**, plus builder-side derivation.

1. **`src/Raun/Resources/ResourceRoleAttributes.cs`** — add `ReferencesAttribute` and `ConsumesAttribute`, both `[AttributeUsage(AttributeTargets.Parameter)]`, doc-commented in the house style **and carrying the single-subject limitation remark** for IntelliSense.

2. **`src/Raun/Resources/LifecycleVerb.cs`** — the enum + `ToLockMode` + `Precedence` changes above.

3. **`src/Raun/Resources/ResourceContext.cs`** — add `Reference<T>(T)` and `Consume<T>(T)` methods, each a one-liner delegating to the existing private `Record(...)` exactly like `Read`:
   ```csharp
   public ValueTask Reference<T>(T resource) where T : notnull
       => Record(LifecycleVerb.Reference, _resolver.Resolve(resource), resource);
   public ValueTask Consume<T>(T resource) where T : notnull
       => Record(LifecycleVerb.Consume, _resolver.Resolve(resource), resource);
   ```
   Dedup, identity resolution, and timestamping are inherited unchanged.

4. **`src/Raun.Generator/Lowering/AttributeReader.cs`** (`ParameterRole`) — map `[References]` → verb `Reference`, `[Consumes]` → verb `Consume`, alongside the existing `[Reads]` → `Read`.

5. **`src/Raun.Generator/Emit/ScenarioEmitter.cs`** — emit a single `await __ctx.Resources.Consume(slot)` / `.Reference(patient)` call where it emits `.Read(...)` today, with the same `#line hidden` trivia. No separate edge call.

6. **`src/Raun.Mtp/HtmlReport/HtmlReportModelBuilder.cs`** — derive edges: for each step, find the subject (its `Create`/`Edit` effect identity) and emit a `ReportReference` per `Consume`/`Reference` effect; dedup edges by `(Subject, Target)` across the scenario; attach to the scenario model. Steps with no subject contribute no edges.

## Testing (behavioral-first)

- **Generator/snapshot:** a `[References]`/`[Consumes]` param emits a **single** `Reference(...)`/`Consume(...)` call (not `Read` + edge); a `[Reads]` param still emits `Read`.
- **Runtime:** executing the scenario records `ResourceEffect`s with verbs `Reference`/`Consume` on the targets; `ToLockMode` returns `Shared`; same-step double-tag dedups to the usage verb over `Read`.
- **Report model (the new logic):** `HtmlReportModelBuilder` derives the expected `ReportReference` adjacency — right endpoints, right kind, deduped across steps; **a step with no Create/Edit subject yields the effect but no edge** (the graceful case).
- **Sample as living demo:** `samples/AppointmentTests/AppointmentDsl.cs` `CreateAppointment` upgraded to `[Consumes] Slot slot, [References] Patient patient`. Existing suite stays green; the change is additive (swaps `Read` → `Consumes`/`References` on those params, adds derived edges, removes nothing).

## Deferred (YAGNI)

- **Multiple create/edit subjects in one step** — derivation can't attribute the edge, so no edge is formed (and the limitation is documented for the user). Real support needs explicit subject linkage; revisit when a test demands it.
- C2: `[Consumes]` → `Exclusive` lock enforcement and any scheduler/serialization behavior.
- Kind-specific rendering, popovers, drawn arrows, reverse-edge navigation — report agent's call, later.
- Edges between two resources where neither is the step's subject (a pure "link" step).

## Resolved decisions (from brainstorming)

- **No decomposition into `Read` + edge.** `Consume`/`Reference` are single effect verbs treated like `Read`; there is never a `Consume` without an implied read because the verb *is* the read. Edges are derived, not stored.
- **Dedup precedence:** `Reference`/`Consume` sit just above `Read` (ladder above), so a double-tagged param shows the usage verb and its edge survives. Same-identity collisions with `Load`/`Create`/`Edit`/`Delete` in one step don't occur in practice (subject vs. param are distinct identities), so their placement is immaterial.
- **Kind naming mirrors the verb names** (`Reference`/`Consume`), carried by the verb and surfaced as the `ReportReference.Kind` string — no separate `ReferenceKind` enum.
