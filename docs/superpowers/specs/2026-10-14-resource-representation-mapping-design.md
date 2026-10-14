# Resource representation mappings

Date: 2026-10-14
Status: approved with With naming; attribute proposal explicitly rejected 2026-10-15; fluent-only implementation planning
Scope: canonical identity feature opportunity. No release unless explicitly requested.

## Intent and accepted API

Several CLR representations can describe one resource. Identity is only a resource kind/key pair;
no representation payload belongs in equality. Consumers need not own or modify their EF entities,
response DTOs, or other mapped types. They should not create a new marker type solely for identity.
The existing domain type can name a kind, or the consumer can name it directly.

```csharp
identities.Identify<Patient>()
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(response => response.PatientId);

identities.Identify("patient")
    .With<Patient>() // reuses IResource<Patient>.KeyFor
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(response => response.PatientId);
```

These are alternative configurations, not a requirement to register both. Typed and named builders
can nevertheless compose: a typed Patient group follows Patient's configured kind if Patient is
mapped into a named group. Configuration order must not change the resulting equivalence group.

Existing Identify<T>(Func<T, ResourceKey>) remains a self-kind key selector with its existing
precedence: IResource<T>.KeyFor still wins over that legacy selector. New explicit With<T>(selector)
provides both the representation mapping and key extraction and takes precedence for that source.
With<T>() requires IResource<T> and reuses interface dispatch, including explicit implementations.
Identify<T>() itself does not require T to implement IResource; existing behavior for T is preserved
unless a With<T>(...) registration supplies a new extractor. Generic group builders return themselves
for chaining. ResourceContext forwards the fluent APIs to its scenario resolver, as it already does
for Identify<T>(selector); shared resolver registration through consumer DI is the recommended setup.

## Where users configure mappings

Fluent declarations live in the test executable's startup before running Raun. Register the configured
ResourceIdentityResolver instance in the consumer service provider, then pass that provider through
RaunTestApplication.RunAsync(args, services: provider), or Aspire's existing Services configuration.
The resolver belongs to application configuration, not an individual request/DTO. No type changes are
needed. Consumers use a custom Main to supply the configured provider (RaunGenerateProgram=false),
or configure it through Aspire's existing Services callback. Generated Main keeps its current behavior.

```csharp
var identities = new ResourceIdentityResolver();
identities.Identify<Patient>()
    .With<PatientRow>(row => row.Id)
    .With<PatientResponse>(response => response.PatientId);

await using var provider = new ServiceCollection()
    .AddSingleton(identities)
    .BuildServiceProvider();

return await RaunTestApplication.RunAsync(args, services: provider);
```

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

## One runtime authority

ResourceIdentityResolver owns representation-to-kind mappings and key resolution. Explicit mapped
extractors produce canonical keys; unmapped sources use the existing contract/selector/runtime/fallback
chain. Typed group references are resolved to the terminal kind at identity resolution, so mapping
PatientRow to Patient and Patient to "patient" composes in either declaration order. Do not use CLR
inheritance or matching display names to infer membership.

Group declarations form equivalence sets. Joining typed groups merges their existing members;
joining a typed group with a named group retains the named kind. Joining two different named kinds
throws before configuration is changed. For two typed roots, the Identify<T>() target names the merged
group. Union roots rather than creating cyclic aliases; reciprocal/redundant declarations are harmless.
Repeated same-group registration may replace a representation's explicit extractor before freezing. A mapping
does not invoke or convert a representation into another DTO. Keys must be the canonical key values;
there is no key transformation for key-only references because no source object exists there.

Freeze new representation/group configuration on the resolver's first identity resolution. A fluent
registration afterward throws an actionable error rather than changing identities already stored in
claims or reports. Legacy key/label selector APIs retain their existing behavior; users must still use
stable keys. Configuration belongs before running scenarios. This keeps identity resolution independent
of concurrent scheduling without introducing a global registry.

Normalize typed key-only identities through the same resolver. ResourceContext.Load<T>(key), explicit
identity claims and lineage endpoints, and generated ResourceKey<T> parameter claims/title references
must agree with object claims. Generator binding must call a context/resolver identity helper rather
than constructing an unnormalized typeof(T)/key literal. Explicit named identities already identify
a terminal kind. Invalid/default identities stay rejected or unlinked as appropriate to existing APIs.

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

## Generator and analyzer

Keep mappings runtime configuration, so third-party types need no attributes or new interface. No
IResource<TRepresentation,TResource> contract is introduced. No mapping attributes or assembly-level
mapping registration are added. The analyzer cannot infer arbitrary
registration delegates and must not pretend that unrelated source types cannot collide at runtime.
Preserve existing dependency/static conflict analysis; the canonical runtime ledger is authoritative
for cross-representation conflicts. Existing role annotations on external representation parameters
remain usable. ResourceKey<T> continues to name a type whose kind is resolved through the scenario's
resolver; no additional named-key attribute is necessary for this feature.

## Alternatives and why they lose

- Mapping attributes: explicitly rejected by the user. Keep one fluent configuration surface with
  application/DI ownership; do not add attribute discovery or generated default registration.
- Require IResource<TRepresentation,TCanonical>: rejected because consumers often cannot change types,
  and the fluent mapping is sufficient without adding a second resource contract.
- Require a canonical marker class: rejected because an existing domain type or named kind is enough.
- Globally equate bare key strings: rejected because patient "123" and appointment "123" can coexist.
- Convert every DTO into the anchor domain object: rejected because identity resolution needs only a
  key, and conversion loses original evidence while adding allocation and user-code execution.
- Mutate identity after execution begins: rejected because recorded claims and references cannot be
  rewritten safely. Resolve groups from configuration and keep that configuration stable during runs.

## Acceptance and validation

Tests first for this behavioral feature. Verify multiple representations and string/key-only claims
share identities, conflict for unordered edits, and agree on lineage/report resource IDs. Verify
separate kinds with equal keys stay separate. Preserve original snapshots and type-specific labels
without casts, including explicit resource contracts, declared base types, nullable values, and
representation dedup order. Verify registration precedence, idempotent membership, order-independent
composition, contradictory groups/cycles, and rejection after resolution. Compile/execute generated
claims and titles with the configured resolver and retain existing snapshots/guard/merge assertions.

Document both fluent forms, unchanged IResource key extraction, custom-Main/Aspire DI setup,
and configuration ownership in README. Run focused checks, locked restore,
zero-warning solution build, full solution tests, existing report checks, and package assembly. Obtain
one final staff review. Commit and push within the existing authorization; do not create a release.
