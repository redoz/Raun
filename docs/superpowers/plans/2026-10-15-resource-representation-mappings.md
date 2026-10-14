# Fluent Resource Representation Mappings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for the established native execution preference. Steps use checkbox syntax for tracking.

**Goal:** Make third-party and owned representations share existing typed or explicitly named kind/key identities.
**Architecture:** ResourceIdentityResolver owns group equivalence and key extraction. Fluent startup configuration supplies one immutable group model for execution; claim/message boundaries normalize symbolic identities through that model while snapshots and labels retain their source representation.
**Tech Stack:** .NET 10, C# static resource interfaces, existing Roslyn generator and HTML reporting.
**Spec:** ../specs/2026-10-14-resource-representation-mapping-design.md

## Global Constraints

- Use Identify<TResource>().With<TRepresentation>(selector) and Identify(string).With<TRepresentation>(selector).
- No mapping attributes, assembly discovery, generated mapping registration, new resource interface, or required identity marker class.
- Existing IResource<T>, IResourceIdentity, ResourceKey, legacy Identify<T>(selector), and Label<T> remain usable.
- Keep identity a kind/key pair; no payload or label participates in equality. Preserve existing typed identity semantics.
- Use startup/DI configuration, including Aspire's existing Services callback. No new bootstrap hooks required.
- Preserve original dirty checkout; work in the existing attached worktree. Commit/push when verified; no release or tag.

## Review Focus

- Typed key-only claims/titles must match mapped instances, including named groups.
- Base-typed values select source label contracts without casting to the canonical kind type.
- Merging already-declared typed groups must preserve every member regardless of registration order.
- Named kind collisions and late configuration must fail before corrupting claims/evidence.
- Default/unmapped identities, explicit IResource implementations, and recording-time snapshots must retain existing behavior.

### Task 1: Identity kinds and the fluent resolver authority

Files: src/Raun/Resources/{ResourceKind.cs,ResourceIdentity.cs,ResourceIdentification.cs,ResourceIdentityResolver.cs}; test/Raun.Test/Resources/ResourceIdentityResolverTests.cs and focused new representation tests.

Interfaces:
- ResourceKind is a readonly discriminated value for Type or named string, with validity, typed view, display/qualified-name views, equality/hash. Typed equality remains Type equality; named equality is ordinal/case-sensitive.
- ResourceIdentity has Kind and Key, old (Type, ResourceKey) and new (string, ResourceKey) constructors, nullable legacy Type view, and legacy typed deconstruction; avoid ambiguous out-var overloads.
- Resolver Identify<T>() and Identify(string) return ResourceIdentification. It exposes With<T>(Func<T, ResourceKey>) and With<T>() where T : IResource<T>, returning the same builder.
- Resolver Resolve(ResourceIdentity identity) normalizes an explicit identity's kind; instance Resolve/TryResolve and key-only ResolveKey(Type representationType, ResourceKey key) use the same root-kind function.

- [ ] Add failing cases proving three representations share a typed/named pair, equal keys in unrelated groups differ, and existing typed construction/hash equality stays valid.
- [ ] Add failing cases for explicit mapped-key precedence, selector-free explicit interface dispatch, typed/named group composition in either order, reciprocal typed declarations, and merging existing members.
- [ ] Implement group roots separately from source key extractors. Union typed roots into the Identify target; prefer a named root over a typed root; reject distinct named roots atomically. Freeze new group/extractor registrations upon first successful identity resolution.
- [ ] Verify late registrations fail, rejected TryResolve does not freeze, and conflicting registration leaves previous configuration intact. Preserve legacy key-selector precedence on unmapped sources.
- [ ] Run focused resource tests before marking the authority complete.

### Task 2: Claims, lineage, messages, and source evidence

Files: src/Raun/Resources/{ResourceContext.cs,ResourcePresentation.cs}; src/Raun/Reporting/Messages/StepMessageBuilder.cs; test/Raun.Test/Resources/representation tests and relevant structured-message tests.

Interfaces:
- ResourceContext forwards Identify<T>()/Identify(string), and Load<T>(key) uses resolver ResolveKey.
- Explicit-identity claims and lineage normalize with Resolver.Resolve(identity) before ledger/dedup.
- ResolveLabel accepts the declared source representation type separately from canonical identity. Source contracts/selectors own labels; the resolved kind supplies fallback display only.

- [ ] Write failing unordered object/key conflict and mixed-representation lineage/dedup cases; both claim orders must preserve the stronger verb and actual instance data.
- [ ] Add source-label and snapshot cases: canonical Patient label selector must never be invoked on PatientRow; each earlier message snapshot survives later representation mutation/recording.
- [ ] Normalize explicit identity wrappers in TryResolve so existing generated key-binding literals pass through the same authority during title formatting and claim recording. Do not change generated argument evaluation or raw step argument types.
- [ ] Implement source-aware labels for declared types and runtime lineage subjects, preserving default/explicit IResource.LabelFor behavior and key-only fallback labels.
- [ ] Run focused resources/structured messages tests and existing cancellation/teardown output checks.

### Task 3: Report and generated-contract parity

Files: src/Raun/Reporting/Html/HtmlReportModelBuilder.cs; test/Raun.Test/Reporting/HtmlReportModelBuilderTests.cs; test/Raun.Generator.Test/{ResourceKeyParameterTests.cs,IdentityLoweringTests.cs}; generator lowering files only if parity evidence requires changes.

Interfaces:
- Report Type/QualifiedType display fields derive from Kind, preserving prior values for typed kinds and identifying named kinds distinctly.
- Report dictionaries/lineage continue to use ResourceIdentity equality and opaque report IDs.

- [ ] Write failing report cases showing one resource ID for mapped effects/messages/lineage, two IDs for equal keys in separate kinds, and unchanged typed-kind report metadata.
- [ ] Compile and execute ResourceKey<Patient> claims/titles with a configured resolver mapping Patient into a named group and mapping third-party DTOs into Patient. Assert one identity across DTO, string, explicit identity, and Load<T>(key) paths.
- [ ] Prefer runtime-boundary normalization over new generated mapping infrastructure. If a key-binding path bypasses the resolver, route that path through an explicit resolver/context helper; preserve all argument, guard, and merge assertions.
- [ ] Run generator/report tests and review any changed existing snapshots rather than blindly accepting them.

### Task 4: User documentation, final verification, review, and integration

Files: README.md; docs/reviews/2026-10-14-raun-staff-review.md; this plan/spec status.

- [ ] Show both With forms, IResource key extraction, startup AddSingleton resolver setup, custom Main/RaunGenerateProgram=false, and Aspire Services registration. Explain immutable execution configuration, named/type distinction, and nullable legacy Type compatibility.
- [ ] Run locked restore, zero-warning Release solution build, full solution tests, existing 73 report checks, and all four package assemblies.
- [ ] Obtain one fresh staff review of the whole change, fix material findings, and rerun only affected checks plus required final gates when source changes.
- [ ] Commit and push the implementation and evidence; mark the recommendation implemented without publishing/tagging. Keep the separate intermittent cancellation follow-up visible.

## Implementation ruling recorded before execution

The earlier draft suggested changing generated key binding immediately. Existing binding constructs a
symbolic ResourceIdentity which formatting/claim boundaries can normalize through the resolver. Use
those boundaries first; only change emitted code if execution evidence finds a bypass. This keeps one
identity authority and preserves the generator's data-only contract without a second mapping mechanism.
