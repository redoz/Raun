# Separate resource actions and relationships

Date: 2026-10-16
Status: conversational design approved; written design ready for review.
Execution preference: implementation here with one final reviewer, carried forward from the session.
Release policy: no release, tag, or package publication.

## Intent

Define resource actions and resource relationships as separate, validated concepts. An action says
what a step does to one resource. A relationship says how two resources connect. Neither attribute
names nor arbitrary constructor code establish semantics. Consumers define a custom action once,
and invalid declarations fail during compilation whenever their inputs are statically available.

This supersedes the action classification and lineage-declaration portions of the implemented
2026-10-16 resource-actions-and-label-memory design. Scenario-owned label memory, canonical identities,
frozen evidence, access timing, and report geometry remain requirements.

## Public action contract

Introduce an abstract ResourceAttribute as the common base for action attributes, and a five-value
ResourceActionKind enum: Create, Load, Read, Edit, Delete. This is a single choice, not Flags.

Declare the effect with a sealed ResourceActionDefinitionAttribute on the action attribute class:

```csharp
[ResourceActionDefinition(ResourceActionKind.Edit)]
public class EditedAttribute : ResourceAttribute;

[System.ComponentModel.DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;

// Direct definitions also work, without selecting a built-in attribute name.
[ResourceActionDefinition(ResourceActionKind.Read)]
public sealed class AuditedAttribute : ResourceAttribute;
```

Created, Loaded, Read, Edited, and Deleted remain convenient built-ins. They all derive from
ResourceAttribute and declare their semantics through the same definition metadata. A consumer can
inherit one or define a custom action directly. Metadata must have exactly one definition across the
hierarchy: a derived class cannot replace or repeat an inherited definition. The applied attribute
has no writable Kind, Effect, or Access property. Producer action attributes retain References and
Consumes arrays as relationship shorthand. Custom attributes derived from them inherit that syntax.

Access follows the kind: Create/Edit/Delete are exclusive; Load/Read are shared. The definition
cannot choose a contradictory lock mode. Reference and Consume are not valid action kinds.

Discover definitions through exact Raun metadata symbols and CustomAttributeData. Never instantiate
attributes, evaluate properties, inspect constructor bodies, or interpret arbitrary IL. Verify that
the definition owner derives from ResourceAttribute. Reject missing, competing, or invalid enum
values, and metadata applied to an unrelated class. Validate unused local definitions too, and
validate referenced definitions at the consumer call when their source is unavailable.

Keep existing naming rules: default concrete name minus final Attribute suffix; nearest custom
DisplayName override; no blank resolved names. Preserve the five built-in labels. Built-in identity
may select its familiar presentation label, but never establishes action semantics. Runtime and
analyzer validation must agree. Retain private/generic/referenced types, correct load-context lookup,
and weak-key caching that permits collectible assemblies to unload.

## Public relationship contract

Introduce a two-value ResourceRelation enum: Reference and Consume. Introduce a sealed, repeatable
RelatesToAttribute(ResourceRelation kind, string target), valid on parameters, return values, and
methods. Both constructor arguments are required and read as metadata.

```csharp
[return: Created]
[return: RelatesTo(ResourceRelation.Reference, nameof(patient))]
[return: RelatesTo(ResourceRelation.Consume, nameof(slot))]
public Task<Appointment> Book(Patient patient, Slot slot);
```

The placement supplies the source endpoint. The required target names a parameter, or Subject.Return
for a relation from a parameter to the returned resource. Method placement is shorthand for return
placement. The relation always points from the produced/loaded/updated source to the named target.
There is no endpoint-less relationship and no implicit conversion from an action kind to a relation.

Producer action attributes also support the existing compact form:

```csharp
[return: Created(References = [nameof(patient)], Consumes = [nameof(slot)])]
public Task<Appointment> Book(Patient patient, Slot slot);
```

This is equivalent to the separate declarations above. The action attribute's location supplies the
source; each array item supplies a target; the framework-declared property supplies Reference or
Consume. These properties are a defined Raun metadata contract, not arbitrary custom properties
recognized by spelling. Keep them on Created, Loaded, and Edited and their derived custom attributes.
Read only explicitly supplied named arguments; do not execute constructor assignments or property
initializers to discover defaults. Direct custom actions can use separate RelatesTo declarations.

Normalize both syntax forms into the same relationship records before validation and emission.
They have identical endpoint, producer, access, timing, and diagnostic rules. Both forms may occur
on one source, but duplicate edges and conflicting kinds are errors across the combined declarations.
There is one semantic authority after normalization, with no first-wins or last-wins interpretation.

The source must have a Create, Load, or Edit action, preserving the existing producer-side contract.
A relationship does not stand in for that source action. A target must support a canonical resource
identity, including ResourceKey<T> parameters. A target relationship supplies the existing shared
Reference/Consume access, so a separate Read attribute is unnecessary. A separately declared exclusive
target action remains exclusive and cannot be weakened by the relation.

Reference means the source keeps a link to an independently identified target. Consume means the
target is an input to producing the source. Both target accesses are shared. Consume never implies
Delete: removal requires an explicit Delete action. A relationship is report evidence, not a lock on
an external system and not proof that the operation succeeded.

Diagnose invalid enum values, blank/unknown targets, unsupported source/target types, missing return
values, a source naming itself, duplicate relations, and conflicting kinds for the same endpoint
pair. Resolve aliases to canonical identities at runtime and reject a self-edge that could not be
known statically. Do not silently choose the first of competing relationships.

A return action overrides a method action as today, including the complete inline References/Consumes
arrays owned by that action. An explicit return action with empty arrays does not inherit arrays
from the replaced method action. For standalone RelatesTo declarations, an explicit return set replaces
the method-level set; otherwise the method set applies. Combine the selected action's inline relations
with the selected standalone relations, then validate the complete normalized set. A return action
does not discard separate method-level RelatesTo declarations merely because it replaces the method
action. Validate both sites and document these two selection rules with mixed-form examples.

## Lowering, execution, and reports

Lower actions and relationships into distinct validated records. An action has a resource subject,
base kind, and optional custom action identity/name. A relationship has a relation kind and two
resolved subjects. Only resolved, valid records reach emission; reject invalid input instead of
constructing partial relationship records or silently discarding target names.

Keep LifecycleVerb and the existing runtime/report representation for compatibility: the five action
kinds map to the corresponding verbs, and the two relation kinds map to Reference/Consume. They are
projections of separate declarations, not one seven-choice action configuration surface.

Keep parameter access claims before invocation. Record anything requiring the returned value only
after invocation. Do not duplicate raw argument evaluation, move exclusive input claims after a call,
or fabricate a ResourceIdentity wrapper snapshot. Reuse the current imperative Reference/Consume
APIs and their explicit endpoints; this change does not need another imperative facade.

Preserve action aggregation by canonical identity and concrete custom attribute Type, semantic
conflict rules, chronological logs, captured snapshots, deterministic label memory, teardown scope,
resource-matrix selection/connections, and data-free key-only references. Reject contradictory
relationship declarations before the report builder; it must not resolve ambiguity by first-wins
pair deduplication.

## Migration

Preserve existing References and Consumes arrays as supported shorthand; no source migration is
required for existing producer attributes or custom attributes derived from them. Add standalone
RelatesTo declarations as another syntax for the same normalized relationship model. Update README
and samples to show equivalence and when each form is useful, rather than require wholesale rewrites.

Existing array declarations in referenced libraries continue to work through the same framework
metadata contract. Retain the existing inline return-action override behavior. Previously generated
graph delegates and saved reports do not require new mandatory model fields.

Keep ResourceAction.For<TAttribute>(), imperative lifecycle methods, existing built-in usage without
lineage arrays, and the previously approved custom inheritance usage. Update factory/analyzer error
messages to refer to the new explicit action definition contract.

## Documentation and validation

Use ASD-STE100-style XML comments on ResourceAttribute, the definition attribute and enum values,
all five built-ins, RelatesToAttribute, ResourceRelation, and each public argument/property. Explain
source/target direction, access, timing, method/return precedence, and Consume versus Delete.

Tests first. Cover built-in and direct custom definitions; indirect inheritance; missing/duplicate/
conflicting definitions; invalid enum casts; same-name unrelated classes; blank labels; private and
generic referenced attributes; and metadata discovery without constructors/static initialization.
Retain collectible load-context regression coverage and analyzer/runtime parity.

Cover return/parameter/method relationship placement, required endpoints, canonical aliases,
key-only targets, supported producer actions, independent action/relation override precedence,
legacy array compatibility, mixed-form normalization, and duplicate/conflicting/self relations. Assert access and recording
order, unchanged raw argument evaluation and graph dependencies, target conflict semantics, generated
lineage, remembered names, and frozen evidence. Retain all guard/merge/node-count assertions when
snapshot shapes or sample counts change.

Run locked restore, zero-warning Release build, full solution tests, report JavaScript checks,
canonical sample and resource-matrix browser checks, and four local package builds. Obtain the
already requested one final staff review. Do not publish a release.

## Alternatives considered

- Treat arrays as a separate semantic model: rejected. Keep their compact syntax, but normalize them
  into the same endpoint-bearing relationship records as standalone declarations.
- One seven-value action enum: rejected because a Reference/Consume action alone cannot describe
  both endpoints and mixes access operations with lineage edges.
- Store an overridable effect property or constructor-selected enum on ResourceAttribute: rejected
  because arbitrary custom code is not readable across referenced libraries without execution or IL
  interpretation. Class metadata declares the value explicitly and consistently.
- Classify built-ins or custom attributes by their names: rejected because spelling is not a semantic
  contract.
- New generic marker types for each effect: rejected because the five enum values already express
  the closed choices without additional user-owned types.
- Remove inline arrays: rejected after user review because separate semantic models do not require
  separate syntax at every call site. Shared normalization and combined validation avoid competing
  interpretation paths while preserving useful compact declarations.

## Self-review

The design separates action semantics from endpoint-bearing relationships, gives each one validation
ownership, and specifies metadata discovery, timing, return precedence, source compatibility, and evidence
preservation. C# requires constructor arguments but cannot constrain nameof to a step parameter or
prevent enum casts. Raun diagnostics close those static gaps; runtime validation covers canonical
aliases and imperative metadata. This is compile-time enforcement where possible, not a claim that
C# alone can prove domain behavior.
