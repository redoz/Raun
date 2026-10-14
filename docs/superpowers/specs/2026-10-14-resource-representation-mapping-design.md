# Resource owners and alternate representations

Created: 2026-10-14
Revised: 2026-10-15
Status: revised written design approved; interface-first implementation plan awaiting review
Scope: canonical identity feature opportunity. No release unless explicitly requested.

## Intent

All representations of a resource resolve to one kind/key pair. Payloads and labels remain source
representation evidence, not identity. Consumers often cannot modify EF rows, generated responses,
or vendor types. Normal generated-host use must not require taking over Main or configuring DI.
User mapping attributes were proposed and explicitly rejected; none are introduced.

The canonical resource owner declares alternate extraction contracts. An existing domain object can
be the owner; otherwise an identity-only owner carries no instance state and is never instantiated.
Fluent Identify(...).With<T>(...) configuration remains an escape hatch for custom hosts.

## Resource contracts and normal usage

Add a non-generic IResource marker identifying resource owners. Existing IResource<TSelf> inherits
that marker and keeps its static abstract KeyFor(TSelf) and static virtual LabelFor(TSelf) unchanged.
Add IResourceAlternate<TAlternate> : IResource, with static abstract ResourceKey KeyFor(TAlternate)
and static virtual string? LabelFor(TAlternate) => null. Inheriting the marker makes every alternate
mapping owner a resource by contract, avoiding an unenforceable requirement to implement an unrelated
interface. It does not force an owner to provide a meaningless self-instance key extractor.

```csharp
public sealed record Patient(string Id, string Name) :
    IResource<Patient>,
    IResourceAlternate<PatientRow>,
    IResourceAlternate<PatientResponse>
{
    public static ResourceKey KeyFor(Patient patient) => patient.Id;
    public static string? LabelFor(Patient patient) => patient.Name;

    static ResourceKey IResourceAlternate<PatientRow>.KeyFor(PatientRow row)
        => row.Id;
    static string? IResourceAlternate<PatientRow>.LabelFor(PatientRow row)
        => row.Name;

    static ResourceKey IResourceAlternate<PatientResponse>.KeyFor(PatientResponse dto)
        => dto.PatientId;
    static string? IResourceAlternate<PatientResponse>.LabelFor(PatientResponse dto)
        => dto.DisplayName;
}
```

The alternate types are untouched. All three produce Patient/key identity. Explicit implementations
support several alternates cleanly and dispatch through the interface, never a public-name lookup.
For a pure owner, no instances, constructor, or KeyFor(owner) method are necessary:

```csharp
public sealed class PatientResource : IResourceAlternate<PatientRow>
{
    static ResourceKey IResourceAlternate<PatientRow>.KeyFor(PatientRow row) => row.Id;
    static string? IResourceAlternate<PatientRow>.LabelFor(PatientRow row) => row.Name;
}
```

Here ResourceKey<PatientResource> string parameters and alternate instances use PatientResource/key.
An owner implementing only IResource can name a key-only resource; it is not an instance key contract.
Self instances use existing IResource<TSelf>, existing runtime identity, selectors, or fallback rules.

## Discovery and ownership

The source generator discovers closed alternate contracts and emits strongly typed registration
metadata, independent of whether the owner has scenarios or whether a generated Main is enabled.
Registration records owner type, alternate type, and interface-dispatch delegates. It must not call
user KeyFor/LabelFor methods, construct an owner, or run an owner's static initializer during discovery.
The current assembly registers before Main through generated initialization.

Emit a generated per-assembly registration manifest with a callable entry point. The consuming
generator imports manifests from referenced resource libraries and explicitly invokes registration,
so a mapping library is not silently ignored merely because only its DTO appears at a call site.
Import generated metadata at compilation; do not scan/load arbitrary assemblies at runtime. Library
manifests hide internal owner types behind generated code compiled in their own assembly. Owners and
alternates must be closed types accessible from the registration code; unsupported inaccessible/open
contracts produce actionable diagnostics rather than disappearing. New manifests remain separate
from scenario-library discovery, whose existing support limits are unchanged.

Manifests expose immutable declaration data. Each generated ScenarioDefinition carries its assembly's
complete imported defaults in an optional, default-empty ResourceMappings member. The scheduler
applies those defaults to the scenario resolver before formatting, argument preparation, or Setup;
preflight-skipped formatting uses the same defaults. Existing generated definitions remain valid.
The existing scenario registry still registers definitions; do not add an unqualified process-global
reverse index of DTO types that could leak mappings between independently compiled applications.

A supplied resolver retains its explicit fluent configuration. Applying matching declaration defaults
is idempotent, including repeated scenarios and library imports; explicit same-group extractors win.
New/conflicting defaults after resolution cannot silently mutate a frozen resolver. Generated defaults
are complete before execution, so ordinary shared-provider scenarios do not discover additional
mappings mid-run. Two distinct owners claiming the same alternate type are diagnosed; runtime
conflicts fail explicitly, never choose whichever registration happened first.

## Fluent custom-host escape hatch

Keep both accepted fluent forms. No source types need to implement the new contracts in this path.

```csharp
var identities = new ResourceIdentityResolver();
identities.Identify<Patient>()
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(dto => dto.PatientId);

// Alternative named identity kind, when no CLR owner is desired:
identities.Identify("patient")
    .With<Patient>() // uses IResource<Patient>.KeyFor
    .With<PatientRow>(row => row.Id);
```

Register the resolver with the services passed to RaunTestApplication.RunAsync, or Aspire's Services
callback. Custom Main uses RaunGenerateProgram=false. The generated-host default requires none of
this. With<T>(selector) supplies an explicit key extractor; selector-free With<T>() reuses existing
IResource<T>.KeyFor. An explicit fluent extractor can replace a default extractor for the same group;
Label<T> remains the explicit label override. It is not a means to silently detach a representation
from its existing declared group or select between ambiguous interface declarations.

## Identity and compatibility

Keep ResourceIdentity as two values: Kind and Key. ResourceKind is a discriminated value describing
either an existing CLR Type or a nonempty named kind; it is not a consumer-authored marker class.
Retain ResourceIdentity(Type, ResourceKey), add ResourceIdentity(string, ResourceKey), and keep Key.
Named strings use ordinal, case-sensitive equality; typed kinds retain CLR Type equality, including
assembly distinction. A named kind does not accidentally equal a CLR kind with the same display name.
Existing IResource<T>, IResourceIdentity, and ResourceKey remain unchanged.

Expose Kind for new code. The legacy Type view becomes nullable for named kinds: this is an honest
reflection of the new value domain, and internal code must use Kind rather than inventing a CLR type.
Preserve typed construction/deconstruction where possible and document that consumers reading Type
must handle named kinds. Existing unmapped identities and report output retain their meaning. No new
required generated-model members. This beta development change requires rebuilding affected consumers;
it will not be tagged or published without a separate request.

## One runtime identity authority

ResourceIdentityResolver normalizes kind/group identity for every instance, supplied ResourceIdentity,
and key-only claim. Alternate defaults supply the owner's typed kind, a source key extractor, and an
optional source label extractor. Existing unmapped KeyFor/selector/runtime/fallback precedence remains.
An explicit fluent mapped extractor takes precedence for its mapped source; legacy Identify<T>(selector)
retains its current lower priority than IResource<T>.KeyFor.

Typed and named fluent groups compose in either declaration order. Group declarations form equivalence
sets: typed merges retain all members and use the Identify target root; named kinds win over typed
roots; different named roots conflict atomically. Union roots, avoiding cycles by construction and
making reciprocal/redundant declarations harmless. Canonical keys must agree; raw key-only values
are already canonical, because there is no source object from which to transform them.

Freeze new group/extractor configuration on the resolver's first successful identity resolution.
Late fluent group changes throw rather than invalidating stored claims or report references. Legacy
selector APIs retain their existing behavior. Generated defaults must be available before freezing;
newly loaded mapping declarations cannot silently change an in-flight resolver's snapshot.

ResourceContext.Load<T>(key), explicit claims, lineage endpoints, and ResourceKey<T> parameter/title
paths resolve through this authority. Existing generated symbolic kind/key literals can remain if
runtime claim/message boundaries normalize them; emitted code must be changed only where execution
proof finds a bypass. Do not re-evaluate arguments, change raw step argument types, weaken graph
assertions, or create a second generator-side runtime identity policy.

## Representation evidence

Labels are selected using the declared source representation type, never the canonical kind's CLR type.
For lineage subjects whose source type is determined at runtime, use that runtime source type. Existing
registered Label<T>, IResource<T>.LabelFor, explicit implementations, and fallback labels retain their
priority. Default labels display the resolved kind/key; selecting a Patient label function must never
cast a PatientRow to Patient.

Capture the original runtime object exactly as today. Canonicalization affects identity, not payload
or serialization. Keep recording-time snapshots on titles, notes, logs, and effects. Preserve existing
same-step effect aggregation (strongest verb, latest non-null payload) and chronological observer logs;
do not add a second snapshot system or overwrite earlier message snapshots with a later representation.
Reports group by canonical identity, display the resource kind, and preserve each recorded label/data.
Typed kind display honors existing DisplayName; named kinds display their supplied name. Qualified kind
metadata distinguishes named versus CLR kinds, while opaque report IDs remain the identity authority.

Alternate labels dispatch through the owning IResourceAlternate<T> contract for the declared source
type. Explicit Label<TSource> registration has priority; a supplied alternate label follows; existing
source self-contract labels may provide a fallback, then resolved kind/key display. An owner's self
LabelFor(Patient) is never called with PatientRow. Omitted alternate labels therefore need no fake
conversion into the owner. Snapshots always serialize the supplied representation.

## Analyzer and compatibility boundaries

Recognize the owner marker and alternate contracts without making third-party types implement them.
The generator/analyzer validate ownership declarations, generic closure/accessibility, duplicate
alternate owners, and static contract dispatch. Existing parameter/return role annotations continue
to work on third-party representation types. Runtime configured mappings cannot be fully inferred
statically; the canonical runtime ledger remains authoritative for their cross-type conflicts.

No mapping attributes, new startup convention, per-value registration, or required generated-model
members. Existing unmapped resource contracts and output stay unchanged. Named-kind Type compatibility
and rebuild requirements are documented. Source-generated manifest imports must be deterministic and
must report conflicting referenced declarations as clearly as local declarations.

## Alternatives rejected

- Mandatory custom Main/DI configuration: rejected for normal use; retain it only as the escape hatch.
- User mapping attributes: explicitly rejected. Interfaces place key/label behavior on a resource owner.
- IResource<TRepresentation,TCanonical> on each DTO: rejected because external types cannot be changed.
- Require fake self-instance KeyFor on an identity-only owner: rejected; the marker/alternate contract
  suffices and an owner is never instantiated.
- Bare global key strings: rejected because patient "123" and appointment "123" can coexist.
- Convert alternates to owner instances: rejected; identity needs only a key and conversion loses evidence.
- Runtime assembly scanning or last-registration-wins declaration conflicts: rejected as nondeterministic.

## Acceptance and validation

Tests first for this behavioral feature. Compile/execute local and referenced-library declarations
through a generated host without custom Main or resource DI. Prove keys and optional labels dispatch
through explicit/default alternate interfaces, and discovery invokes no user extraction or owner
construction. Verify identity-only owners need no self KeyFor; ambiguous/inaccessible/open declarations
are diagnosed and duplicate manifest registration is harmless.

Prove multiple representations, ResourceKey<T> strings, Load<T>(key), and supplied identities share
claims, conflicts, lineage, and report IDs. Preserve separate kinds with equal keys. Preserve source
labels, recording-time snapshots, declared base types, nullable behavior, and effect dedup order.
Verify fluent/default precedence, declaration-order independence, named conflicts, and freezing.
Retain all existing argument, branch, merge, cancellation, cleanup, and report assertions.

README must show interface-first normal usage, pure resource owners, labels, third-party mappings,
referenced mapping libraries, and fluent custom-Main/Aspire configuration. Run focused checks, locked
restore, zero-warning build, full solution tests, report checks, and all four package assemblies.
Obtain one final staff review, then commit/push. Do not publish or tag a release unless requested.
