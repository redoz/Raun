# Resource effects, access, actions, and links

Date: 2026-10-18
Status: Written design for review. Not implemented.
Companion project: [Resource authoring code fixes](2026-10-18-resource-code-fixes-project.md).

## Intent

Make resource declarations understandable without learning overlapping framework vocabulary. A user must be able to identify a resource, describe an operation, request scenario isolation, and connect resources without one declaration silently doing unrelated work. Reusable domain attributes package the same primitives available explicitly at a step declaration.

This document supersedes the effect/link portions of the older resource-action and lineage designs. It does not change resource identity mapping, remembered labels, evidence capture, scenario flow, or report source links. No release is authorized by this work.

## The model

| Concern | Representation | Rule |
|---|---|---|
| Identity | Existing ResourceIdentity(ResourceKind, ResourceKey) | Mapped DTOs, rows, domain values, and typed keys identify the same resource. Labels are not identity. |
| Effect | ResourceEffect: Create, Read, Update, Delete | One canonical effect enum replaces the duplicated LifecycleVerb/ResourceActionKind vocabulary. |
| Step access | LockMode.Shared or LockMode.Exclusive | Read implies Shared; Create, Update, and Delete imply Exclusive. It is not an independently configurable override. |
| Scenario access | Uses<T>(LockMode), existing contended-resource policy | Explicit admission reservation, independent of effects on individual data resources. |
| Domain action | ResourceAttribute with exactly one ResourceActionDefinition | Gives an effect a domain name, such as Reserve or Seed. |
| Relationship | Source identity, role, target identity | A role-named link. No hidden access, ownership, deletion, or cascading behavior. |

A ResourceEffect is a declaration of what the step does, not an implementation of that operation. Raun validates conflicting unordered claims; it does not perform the external write or supply a database transaction.

### Canonical effects and built-ins

| Attribute | Effect | Step access |
|---|---|---|
| CreatedAttribute | Create | Exclusive |
| ReadAttribute | Read | Shared |
| UpdatedAttribute | Update | Exclusive |
| DeletedAttribute | Delete | Exclusive |
| SeedAttribute : CreatedAttribute | Create, named Seed | Exclusive |

Seed means the step inserts new test data behind the scenes. Create itself does not imply an API route. Domain actions describe that route where useful.

Remove Load/Loaded rather than retaining a second shared observation concept. Fetching an existing object is Read. Remove Reference and Consume from the effect enum, action kinds, runtime APIs, and relationship-kind vocabulary. A slot reservation changes availability and is Update/Exclusive, not a shared Consume effect. Relationships do not mean the target disappears.

No deprecation period, compatibility aliases, or migration code fixes are required: this project has no external consumers to preserve. Update repository consumers directly. Historical design documents can retain their historical vocabulary; current documentation and runnable examples must use the new model.

### Scenario reservations remain a separate scope

Preserve IContendedResource, UsesAttribute<T>, ExclusiveResourceAttribute, SharedResourceAttribute, PooledResourceAttribute, and ContendedResourceUse. These are admission tokens, not instances of tracked IResource data, and must not be forcibly merged into data-resource identities.

For example, a SharedResource clinic-system token can be used by normal scenarios with shared reservations and by a reset scenario with Uses<ClinicSystem>(LockMode.Exclusive). An ExclusiveResource token always has one holder; a pooled token retains its capacity rules. Shared/Exclusive are still the only access modes; capacity is an admission policy.

Preserve additive declarations on supported assembly/class/scenario/step sites, strongest-mode reduction, and atomic acquisition of the complete reservation set. Acquire before scenario setup. Release only after teardown and asynchronous scope disposal, including cancellation/error paths. A scenario may reserve an external system exclusively while all its business steps only Read individual resources. It must not invent an Update effect to obtain isolation. Step effects do not automatically create scenario reservations.

## Domain action declarations

ResourceAttribute remains the base for effect-bearing attributes. ResourceActionDefinitionAttribute takes ResourceEffect and exactly one definition must exist in an action's hierarchy. Derived actions cannot contradict an inherited effect. Preserve DisplayName overrides and the default action name with a trailing Attribute suffix removed.

User domain actions may deliberately omit the Attribute suffix. Do not create an Xy peer beside XyAttribute. The sample uses ordinary explicit nested types:

```csharp
public sealed class Reserved : UpdatedAttribute;

public sealed class Booked : CreatedAttribute
{
    public sealed class PatientAttribute
        : ResourceLinkAttribute<Patient, ReadAttribute>;

    public sealed class SlotAttribute
        : ResourceLinkAttribute<Slot, Reserved>;
}
```

Usage:

```csharp
[return: Booked]
public Task<Appointment> Book(
    [Booked.Patient] Patient patient,
    [Booked.Slot] Slot slot);
```

The semicolon above abbreviates a step method implementation for the design example. No attribute implements the booking operation.

The patient marker records Read/Shared on the patient and a Patient link from the booking. The slot marker records Reserve/Update/Exclusive on the slot and a Slot link from the booking. The return action records Booked/Create/Exclusive on the booking.

## Link contracts and the raw surface

ResourceLinkAttribute<TResource, TAction> is a parameter attribute base, separate from ResourceAttribute. TAction must identify one valid resource action. A closed concrete marker nested directly in an action declares a role owned by that enclosing action. Its role name is its type name without the final Attribute suffix; PatientAttribute declares Patient. TResource is a canonical identity expectation, not a CLR-assignability requirement.

The marker packages two operations: apply TAction to the target parameter and bind the owning action's source to the target under that role. Both operations normalize to the same records as separate declarations. The relationship itself grants no access.

The explicit spelling is a parameter effect plus LinkAttribute<TOwner>(string role):

```csharp
[return: Booked]
public Task<Appointment> Book(
    [Read, Link<Booked>("Patient")] Patient patient,
    [Reserved, Link<Booked>("Slot")] Slot slot);
```

The role must already exist on the owner's schema; raw links cannot invent undeclared roles. This gives a deliberate escape hatch to select a different explicit target action without changing the meaning of the role. The combination shown above is equivalent to the packaged markers. Do not silently combine a packaged marker with another competing action on the same parameter; report the conflict and require an explicit raw declaration instead.

Owner metadata contains immutable role descriptors: owner action identity, role name, expected canonical resource identity, and packaged target action. Reflection and source generation must normalize the same schema without constructing attributes or executing user getters. A nested non-role helper type does not become a role.

Initially each declared role is required and is bound to exactly one parameter. A supported resource-collection parameter can produce multiple target links, using the existing element-resolution rules. Multiple matching parameters are an ambiguity, not an implicit collection. This project replaces the old property-based References/Consumes surface; it does not introduce a new optional-role or role-options API.

An owner action can appear on a supported parameter, method-return annotation, or return value. A link must identify one source application on that step. The common nested-marker form requires an unambiguous owner application. For multiple source applications, the raw Link form must provide Source = nameof(sourceParameter) or Source = Subject.Return; never guess by source order. Missing/ambiguous owners are diagnostics.

Typed string keys still require ResourceKey<T> metadata. Plain strings are not inferred to be patients or slots from parameter names. Identity-only owners and mapped external types remain supported. Reject self-links after canonical resolution. Keep distinct role-named links even when their canonical endpoints coincide; deduplicate repeated identical source/role/target records.

## Normalization, conflicts, and evidence

Keep three different records and responsibilities:

1. Claims: resource identity, node, effect-derived access; consumed by conflict validation.
2. Effects: resource identity, effect, optional domain action, captured label/data and timing; consumed by logs, telemetry and reports.
3. Links: source identity, role, target identity; consumed by relationship history and reports.

Recording a link must not implicitly call Read, Update, or another effect recorder. A packaged marker emits its target action once and its link once. Runtime lookup and generator-emitted code must share these semantics. Stronger claims on one identity still win for access; preserve distinct named action observations and their captured evidence instead of turning all activity into one label. Update/Create/Delete access must never be weakened by an additional Read or a link.

A return-owned link is recorded only after a source identity is available from a successful return. Parameter claims retain their before-call behavior and return effects their after-call behavior. Error handling must not publish a successful link whose owning result was never produced.

Maintain canonical identity/alternate resolution and scenario label memory. Do not borrow object snapshots for key-only observations. Link rendering uses actual effect endpoints and role metadata; it must no longer search for an effect whose verb happens to equal Reference or Consume. Link roles are data, not a new set of magic report verbs.

## Analyzer contract

Extend existing diagnostics where their meaning remains correct; allocate and document new RAUN IDs where a new concept needs its own actionable diagnostic. Update AnalyzerReleases.Unshipped.md. Analyze declarations as well as applications, even before a broken scenario could be generated.

Required checks:

- Exactly one valid closed action definition; no inherited effect replacement.
- Valid action targets and return actions that actually have results.
- Accessible, closed link owner, target, and action types.
- Nested role marker has a valid owner, a valid target action, and a unique role name.
- No role hiding or ambiguous owner inheritance.
- Each required role is bound exactly once; existing correct bindings are not errors.
- A link has a matching and unambiguous source application.
- Canonical target compatibility, including alternates and typed keys.
- Missing/competing parameter effects; a raw link alone does not satisfy an access declaration.
- A packaged marker plus a competing explicit action does not silently select a winner.
- Existing unordered conflicting-access and contended-reservation diagnostics remain effective.

Error locations should identify the declaration or usage the user can edit. Provide structured diagnostic properties usable by future code fixes, without depending on editor Workspaces APIs inside the generator. Do not require Attribute suffixes on intentionally suffix-free domain actions. XML attribute documentation must use clear ASD-STE100-style sentences and explain operation, implied access, valid location, and recording time.

## Sample and test-framework behavior

Migrate both samples and current README examples. Appointment-domain mapping: Seed/Create, Register/Create, Hold/Update, Reserve/Update, Booked/Create, Rescheduled/Create, Approve/Update, Audit/Read, Cancel/Delete. Booked links Patient and Slot; Rescheduled links its replacement Slot. Show both reusable nested markers and one natural explicit composition example.

Preserve plain scenario titles and the normal test-framework path. Do not rename generated test nodes, change discovery/filter identity, or expose HTML-only grouping labels to MTP to make the report look nicer. Resource-action log labels can change intentionally with the new public action names; scenario and step titles remain authored titles. Verify the Microsoft.Testing.Platform path in addition to HTML. Do not claim native Visual Studio or ReSharper integration was tested unless it actually was.

## Implementation checklist and ownership

This is the core attribute project. Code fixes are a separate follow-up project after this contract is implemented.

- [ ] Contracts/runtime: src/Raun/Resources/{LifecycleVerb.cs,ResourceActionKind.cs,ResourceRoleAttributes.cs,ResourceAttribute.cs,ResourceActionDefinitionAttribute.cs,ResourceActionMetadata.cs,ResourceAction.cs,ResourceContext.cs,ResourceLedger.cs,RelationshipAttribute.cs,RelationshipDefinition.cs,ResourceRelation.cs,RelatesToAttribute.cs}; replace obsolete contracts, add effect/link contracts, preserve scenario admission in ContendedResources.cs and the run loop.
- [ ] Generator/analyzers: src/Raun.Generator/Lowering/{AttributeReader.cs,ResourceRoleMetadata.cs,ResourceDefinitionReader.cs}, Analysis/ScenarioAnalyzer.cs, Emit/ScenarioEmitter.cs, Diagnostics/Descriptors.cs and release tracking. Normalize explicit and packaged forms, emit independent actions/links, and diagnose invalid declarations.
- [ ] Reporting/telemetry: migrate model/builder/template and resource log/span consumers to CRUD plus role-named links. Retain readable names, frozen evidence, matrix alignment, Gantt backgrounds and branch presentation.
- [ ] Consumers/docs: update samples/AppointmentTests, samples/AspireAppointments, tests and current README; remove obsolete live examples and Load/Edit/Reference/Consume APIs without migration shims.
- [ ] Verification: tests first for normalization equivalence, Reserve exclusivity, Seed provenance, canonical alternate/key binding, owner ambiguity, duplicate/missing roles, source failure, role-preserving deduplication, reservation cleanup/disposal lifetime, and runtime/generator metadata parity. Review generator snapshots rather than accepting unexplained churn.
- [ ] Run dotnet build Raun.slnx with zero warnings, relevant suites followed by dotnet test Raun.slnx, report JavaScript/browser checks, and real sample reports. One final independent review; fix material findings. Commit/push only green work. No tag or release.

## Alternatives rejected

- Retain Load beside Read: no useful different access behavior; implementation detail belongs in a title or domain action.
- Keep Reference/Consume as effects or rename Consume to Input: still mixes operations, access and links.
- Add SharedResourceLink/ExclusiveResourceLink as the main abstraction: exclusive alone cannot describe Create versus Update versus Delete.
- Configure access independently on a step effect: permits contradictory definitions; derive the minimum mode instead.
- Derive scenario reservations from CRUD: reading can require whole-scenario isolation and reservations are not mutation claims.
- Generate Xy beside XyAttribute: adds naming/lookup ambiguity and splits the declaration. Use explicit nested types inside a suffix-free action.
- Run attribute constructors/getters to discover links: metadata must be inspectable during compilation and runtime discovery without user code execution.
- Rewrite test names for HTML: breaks the normal test-framework experience for a presentation concern.
