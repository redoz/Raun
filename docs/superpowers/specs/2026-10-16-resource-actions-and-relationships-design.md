# Separate resource actions and relationships

Date: 2026-10-16
Status: implemented and verified; final staff review findings resolved. See the implementation plan for evidence and execution rulings.
Execution preference: implement here with one final staff reviewer, carried forward from the session.
Release policy: no release, tag, or package publication.

## Intent

Make domain attributes reusable without coupling them to step parameter names. An action describes
one resource operation. A relationship connects two resource subjects. Both generator and runtime
read explicit metadata without executing attribute constructors, getters, initializers, or virtual
GetRelationships methods. Invalid declarations fail at compilation where their inputs are known.

This supersedes the action classification and relationship syntax in the implemented resource-actions-
and-label-memory design. Canonical identities, remembered labels, access timing, frozen evidence,
load-context handling, existing reports, and matrix geometry remain requirements.

## Action definitions

ResourceAttribute is the common abstract base. A sealed class-level ResourceActionDefinitionAttribute
selects exactly one ResourceActionKind: Create, Load, Read, Edit, Delete. This is not Flags. Created,
Loaded, Read, Edited, and Deleted declare their semantics through this metadata and remain extensible.
Consumers may inherit a built-in or declare a direct custom action:

```csharp
[ResourceActionDefinition(ResourceActionKind.Edit)]
public class EditedAttribute : ResourceAttribute;

[System.ComponentModel.DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;
```

There must be exactly one definition across the hierarchy. Redefining even the same inherited effect
is invalid. Kind, Effect, and Access cannot be changed at an attribute usage. Create/Edit/Delete are
exclusive; Load/Read are shared. No attribute name establishes semantics. Preserve built-in labels,
concrete custom names without the terminal Attribute suffix, nearest custom DisplayName overrides,
and rejection of blank final names. Runtime/analyzer validation must agree.

## Relationship property metadata

A RelationshipAttribute(ResourceRelation relation) marks an action attribute property. A generic
RelationshipAttribute<TResource> adds an expected canonical resource kind. ResourceRelation has only
Reference and Consume. Both are shared target accesses; Consume does not imply Delete.

```csharp
[ResourceActionDefinition(ResourceActionKind.Create)]
public sealed class BookedAttribute : ResourceAttribute
{
    [Relationship<Patient>(ResourceRelation.Reference)]
    public string Patient { get; set; } = "";

    [Relationship<Slot>(ResourceRelation.Consume)]
    public string Slot { get; set; } = "";
}

[return: Booked(Patient = nameof(patient), Slot = nameof(slot2))]
public Task<Appointment> Book(Patient patient, Slot slot2);
```

Patient and Slot are roles owned by the domain attribute. Their values name parameters of the
specific step. Renaming a step parameter changes only that usage's nameof expression. Do not bake
"patient" or "slot" into the attribute class as step endpoints. There are no class-level RelatesTo
defaults and no executable GetRelationships override.

Marked properties must be public, readable/writable instance string or string[] properties with no
index parameters. The generator reads their supplied named arguments, never their getters, setters,
or initializer defaults. A single marker per property determines the relation kind. Reject hiding or
overriding an inherited marked property rather than silently reinterpreting its binding.

Relationship metadata has a Required property, default true. Required bindings must be explicitly
supplied and nonempty. Required=false allows omission and empty arrays; an explicitly supplied null,
blank string, or blank array item is always invalid. Optional properties do not supply hidden bindings
from constructor assignments or initializers. The built-in arrays use Required=false for compatibility.

Typed markers express expected canonical kinds, not CLR assignability. A Patient owner can accept a
mapped DTO, row, domain object, or ResourceKey<T> parameter belonging to the same canonical group.
Use known generated mappings for conclusive static validation; check unresolved or fluent mappings
at runtime. Do not reject equivalent representations based only on their declared CLR types.

The non-generic marker supports existing arrays whose targets can represent different resource kinds.
Do not add ResourceRef<T> in this slice: existing ResourceIdentity represents resolved endpoints, while
the generic marker expresses type expectations at the metadata boundary. C# attribute arguments
cannot contain a custom resource-reference struct.

## One Relationships contract and compatible syntax

Expose a read-only Relationships schema on ResourceAttribute and its cached ResourceAction descriptor.
Each RelationshipDefinition identifies a marked property, its relation kind, optional expected resource
Type, scalar/array shape, and requiredness. The base getter can return the metadata descriptor's schema;
framework discovery never constructs a user attribute to read it. The schema describes attribute
properties, not step-bound endpoints. At each application, normalize property values into separate
relationship records with an explicit source and target.

Annotate the built-in References and Consumes array properties with the same non-generic marker:

```csharp
[Relationship(ResourceRelation.Reference, Required = false)]
public string[] References { get; set; } = [];
```

Keep both arrays on Created, Loaded, and Edited and let custom subclasses inherit them. Preserve:

```csharp
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot2)])]
```

Retain standalone, repeatable RelatesToAttribute(ResourceRelation relation, string target) on parameters,
return values, and methods. Its location supplies the source endpoint. This is another syntax for
identical normalized relationship records; it does not select an action or bypass validation.

All property annotations, inherited convenience arrays, and standalone declarations use one normalized
model. There are no consumer-specific mappings, concrete-action switches, arbitrary property-name
recognition, or special runtime constructor conventions.

## Subject selection and validation

The source must have a Create, Load, or Edit action. Reference means the source keeps a link to the
target; Consume means the target contributes to producing the source. The target names a resource
parameter, including a key-only parameter, or Subject.Return when the source is a parameter. The
relationship supplies shared target access, so a separate Read declaration is unnecessary. It cannot
weaken a separately declared exclusive action on that target.

Diagnose missing/duplicate/invalid action definitions, unrelated metadata owners, invalid enum values,
invalid property shapes, missing required bindings, blank/unknown endpoints, unsupported resource
subjects, missing return values, direct self-relations, duplicate edges, and competing kinds for one
source/target pair. Validate unadorned local steps and referenced library call sites; avoid duplicate
local declaration and call diagnostics. Preserve existing diagnostic IDs where their meaning remains
accurate and register new ones.

A return action overrides the complete method action, including its property bindings. An explicit
return action with no optional arrays does not inherit the replaced action's arrays. Explicit return
RelatesTo declarations replace the method's standalone set; otherwise that method set applies. Combine
these selected declarations and validate the complete relationship set. Separate method relationships
are not erased merely by replacing method action shorthand.

At runtime, reject canonical source/target self-aliases, conflicting canonical relation kinds, and
typed-kind mismatches that cannot be established statically. Existing imperative operations retain
their signatures. Repeated imperative observations of the same relation can remain idempotent;
duplicate source declarations are compiler errors. Kind conflicts apply within one step. Different
steps can record different relationship kinds for the same pair; preserve both historical kinds
in the report by deduplicating on source, target, and kind. Never silently retain the first conflicting kind.

## Execution and evidence

Preserve LifecycleVerb and its seven runtime/report values as projections of the separate concepts.
Five action kinds project to primary verbs; Reference/Consume are relationship target effects.
Preserve ResourceAction.For<TAttribute>(), Apply, built-in aggregation, distinct custom action Type
identity, chronological logs, snapshot retention, and metadata-only factories with weak-key caches.

Parameter claims remain before invocation. Relationships or claims requiring a return value follow
invocation. Use captured arguments exactly once. A missing optional argument involved in a declared
relationship must use its real default or produce a diagnostic, never silently drop a declared edge.
Validate runtime-only alias/type constraints before accepting that relation's claim or evidence.

Preserve source-specific key/label resolution, canonical mapping, scenario-owned label memory,
key-only data-free references, branch/merge behavior, cancellation drain, cleanup scopes, and existing
matrix connections and semantic styling. New normalized records are internal; do not require new
members in previously generated scenario graphs or report payloads.

## Appointment-domain example and README

Add domain attributes used by existing executable sample steps, not only unused declarations:
Registered (Create) for patients, Held (Edit) for slots, Booked (Create) with typed Patient/Slot property
bindings, Rescheduled (Create) with a typed consumed Slot property, and Cancelled (Delete) for bookings.
Use Booked on both ordinary and urgent booking methods. Demonstrate a step argument named slot2 in
an executable scenario/step or generator regression, proving the domain attribute does not prescribe
parameter names. Keep the existing Approved/Audited and key-only remembered-name example working.

Update README with property metadata, generic expected kinds, required/optional bindings, inherited
array shorthand, standalone syntax, return precedence, and metadata-only discovery. Write ASD-STE100-
style XML comments on every new public attribute, enum member, property, and relationship descriptor.
Explain meaning, access, recording time, source/target direction, and Consume versus Delete.

## Verification and alternatives

Tests first. Cover inherited/direct/private/generic/referenced definitions, invalid declarations and
usages, constructor-free discovery, requiredness, property shadowing, mixed syntax, return precedence,
canonical alternate and key-only endpoints, runtime alias/type guards, and analyzer/runtime schema
parity. Retain attribute unload, graph, argument evaluation, title, snapshot, and cancellation checks.
Run locked restore, zero-warning Release build, full solution tests, report JavaScript checks, real
sample and matrix browser checks, and four local package builds. Obtain one final staff review. No
release is authorized.

Rejected: one seven-value action enum (relationships require endpoints); executable GetRelationships
(the generator cannot run it); custom object attribute arguments (not supported by C#); class-level
hardcoded step names (break reusable role binding); name-based role/property magic (no explicit
contract); removing compatible array shorthand (unnecessary source churn); separate interpretations
for each syntax (ambiguity and drift). Shared metadata schema and normalization provide one authority.

## Self-review

The property names and endpoint names have separate owners. Definitions use metadata that both
compilation and execution can inspect; the common Relationships schema is not a collection of hidden
runtime bindings. Requiredness is explicit and does not depend on initializer code. Canonical expected
kinds permit alternate representations. Examples exercise the public contract and preserved evidence.
