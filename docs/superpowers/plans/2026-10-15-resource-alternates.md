# Resource Alternates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Give domain objects, external rows, response DTOs, and key-only references one canonical resource identity while retaining their individual report evidence.

**Architecture:** Owners declare static key/label contracts through IResourceAlternate<T>. The generator emits immutable per-assembly manifests and attaches complete imported defaults to scenario definitions; one runtime resolver owns canonicalization. Identify(...).With(...) uses the same engine for custom hosts.

**Tech Stack:** C#, .NET 10, Roslyn incremental generator (netstandard2.0), xUnit v3, Microsoft.Testing.Platform.

**Spec:** [Approved resource representation design](../specs/2026-10-14-resource-representation-mapping-design.md).

**Status:** Complete. Native implementation, one final staff review, both findings fixed with RED-to-GREEN regressions. No release.

## Global Constraints

- No user mapping attributes, new startup convention, runtime assembly scanning, or process-global alternate-type index.
- Identity is Kind + Key; keys remain ResourceKey strings. Keep existing typed constructors and unmapped behavior.
- Normal generated-host usage needs no Main/DI changes. Identity-only owners are never instantiated and need no self KeyFor.
- Defaults apply before formatting, argument preparation, or Setup, including preflight-skipped scenarios.
- Preserve original payloads, recording-time snapshots, source-specific labels, argument evaluation, graph guards, merge assertions, cancellation, and teardown ownership.
- Tests first for behavior. Zero warnings; register new diagnostic IDs in AnalyzerReleases.Unshipped.md.
- Commit/push completed work; no tags, releases, or NuGet publication.

## Review Focus

1. A shared resolver is used concurrently by scenarios: identical defaults remain idempotent after freezing; a new or conflicting declaration fails explicitly. Task 2.
2. Two applications reference different mappings for the same DTO: each definition carries its own defaults and no global reverse mapping leaks between them. Task 3.
3. An internal library owner has a throwing static initializer: importing its manifest neither constructs nor initializes the owner nor calls extraction. Task 3.
4. A supplied ResourceIdentity or ResourceKey<T> title bypasses ordinary object extraction: runtime boundaries normalize it and share ledger/report IDs. Task 4.
5. A declared base representation has a derived runtime payload: key/label selection follows the declared source type while the snapshot retains the runtime object. Task 4.

---

### Task 1: Identity values and extraction contracts

**Files:** Create `src/Raun/Resources/ResourceKind.cs`, `IResourceAlternate.cs`, `ResourceMapping.cs`, `IResourceMappingManifest.cs`; modify `IResource.cs`, `ResourceIdentity.cs`, `ResourcePresentation.cs`; test `test/Raun.Test/Resources/ResourceIdentityTests.cs`, `ResourceMappingTests.cs`.

**Interfaces:**
- `IResource` marker; existing `IResource<TSelf> : IResource` retains its members and constraint.
- `IResourceAlternate<TAlternate> : IResource`: `static abstract ResourceKey KeyFor(TAlternate value)`; `static virtual string? LabelFor(TAlternate value) => null`.
- `ResourceKind`: immutable discriminated value with `ResourceKind(Type type)`, `ResourceKind(string name)`, nullable `Type` / `Name` views and kind-aware equality/hash/display. Reject null/empty/whitespace names.
- `ResourceIdentity`: `ResourceIdentity(Type type, ResourceKey key)`, `(string kind, ResourceKey key)`, `(ResourceKind kind, ResourceKey key)`; `Kind`, `Key`, nullable legacy `Type`; preserve typed deconstruction without adding an ambiguous same-arity overload.
- Immutable `ResourceMapping`: `OwnerType`, `SourceType`; `static ResourceMapping Alternate<TOwner,TAlternate>() where TOwner : IResourceAlternate<TAlternate>`; internal object key/label delegates dispatch through constrained generic interface calls.
- `IResourceMappingManifest`: `static abstract IReadOnlyList<ResourceMapping> GetMappings()`; generated library entry implements this contract without instances.

- [x] Write failing identity tests: typed equality includes assembly/type, named equality is ordinal/case-sensitive, named and typed display-equivalent kinds differ, legacy typed constructor/deconstruction work, invalid names fail.
- [x] Write failing descriptor tests: explicit alternate key/label implementations dispatch correctly; omitted LabelFor returns null; creating metadata does not invoke key/label/constructors/static owner initialization; pure owners compile without self KeyFor.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*ResourceIdentityTests' --filter-class '*ResourceMappingTests'`; require the new tests to fail for the intended missing behavior.
- [x] Implement the interfaces/value objects above; derive existing typed presentation from Kind without changing typed display names. Descriptor construction creates dispatch delegates, not owner instances.
- [x] Re-run the focused tests and `dotnet build Raun.slnx -c Release`; require passing tests and zero warnings, including nullable Type call sites adjusted to kind-aware display.
- [x] Commit with subject `feat(resources): define alternate resource contracts and identity kinds` and a body explaining shared identity versus representation evidence.

### Task 2: One resolver for defaults and fluent equivalence groups

**Files:** Modify `src/Raun/Resources/ResourceIdentityResolver.cs`; create `ResourceIdentification.cs`, `ResourceIdentityGroups.cs`; test `test/Raun.Test/Resources/ResourceRepresentationMappingTests.cs` and extend `ResourceIdentityResolverTests.cs`.

**Interfaces:**
- `ResourceIdentityResolver.Identify<T>()` / `.Identify(string kind)` return `ResourceIdentification`.
- Builder `.With<T>(Func<T,ResourceKey> selector)` and `.With<T>() where T : IResource<T>` return the same builder.
- `ResourceIdentityResolver.ApplyMappings(IReadOnlyList<ResourceMapping> mappings)` applies an entire default batch atomically.
- `ResourceIdentityResolver.Normalize(ResourceIdentity identity)` canonicalizes key-only/supplied identities.
- Existing Resolve/TryResolve signatures retained. Internal `ResolveLabel(ResourceIdentity identity, Type sourceType, object instance)` selects representation labels.

- [x] Write failing tests asserting `Resolve(patient) == Resolve(row) == Normalize(new ResourceIdentity(typeof(PatientRow), "42"))`, while unrelated kinds with key "42" stay distinct.
- [x] Write failing tests for both typed/named declaration orders, reciprocal and redundant declarations, named-root priority, target-root typed merges, atomic rejection of distinct named roots, duplicate/default-owner conflicts, and retention of every merged source member.
- [x] Write failing tests for explicit mapped selector over default/self key extraction; default alternate labels after explicit Label<T>; legacy selector below self KeyFor; invalid TryResolve does not freeze; first successful resolution freezes new mappings; matching defaults remain idempotent after freeze, including concurrent scenarios (review focus 1).
- [x] Run the new mapping class and existing resolver tests; confirm intended failures before implementation.
- [x] Implement equivalence sets with union roots and one synchronization boundary for configuration/freeze. Validate the complete batch before publishing changes; use exact source-type lookup rather than inferred inheritance. Keep explicit extractor provenance separate from generated defaults so same-group explicit overrides win without hiding declaration conflicts.
- [x] Route Resolve, TryResolve, Normalize, and label extraction through that state; preserve unmapped fallback behavior and legacy Identify<T>(selector)/Label<T> APIs.
- [x] Re-run focused mapping/resolver tests and zero-warning Release build.
- [x] Commit `feat(resources): canonicalize mapped resource representations` with the configuration/freeze rationale.

### Task 3: Generated discovery, manifests, and definition defaults

**Files:** Create `src/Raun.Generator/Analysis/ResourceMappingDiscovery.cs`, `Emit/ResourceMappingEmitter.cs`; modify `ScenarioGenerator.cs`, `Emit/ScenarioEmitter.cs`, `Analysis/ScenarioAnalyzer.cs`, `Diagnostics/Descriptors.cs`, `AnalyzerReleases.Unshipped.md`; modify `src/Raun/Model/ScenarioDefinition.cs`; test `test/Raun.Generator.Test/ResourceAlternateTests.cs`, `GeneratorHarness.cs` where referenced compilation support is needed.

**Interfaces:**
- `ScenarioDefinition.ResourceMappings`: optional/default-empty `IReadOnlyList<ResourceMapping>`.
- Each mapping assembly emits public sealed `Raun.Generated.RaunResourceMappings_<stable assembly-identity hash>` implementing IResourceMappingManifest, with public static `GetMappings()` returning cached read-only local descriptors. Unique type names avoid cross-assembly C# name ambiguity.
- Each manifest also exposes generated constant declaration metadata (version 1, indexed owner assembly identities and reference names). The importer resolves each owner through Roslyn and reads its actual alternate interface symbols for validation (including generic argument assembly identities and internal library owners), without exposing those types in public generic signatures or executing GetMappings at compilation. This is generated protocol data, not user mapping attributes.
- Import manifests by compile-time assembly symbols in `Raun.Generated`, implementing the exact manifest contract; deterministic assembly/type order. Call their generated GetMappings entry, never owner methods or runtime assembly scanning.
- Application generated code combines local/imported declarations into cached immutable defaults and attaches those defaults to every generated definition. A library with no scenarios still emits its local manifest. Imported descriptors are not re-exported as local declarations.
- `RAUN025`: invalid/open/inaccessible alternate declaration, Error. `RAUN026`: multiple distinct owners for the same alternate source, Error. Diagnostic arguments name the source/owners and remedy; add both release entries.

- [x] Write failing compilation/execution tests for local declarations and a referenced mapping-only library with internal owners. Execute a generated definition with no custom Main or resource DI and assert its defaults resolve DTOs to the owner and preserve explicit/default label dispatch.
- [x] Write failing tests proving independent consuming compilations retain separate defaults for a common DTO (review focus 2); duplicate imports are harmless; metadata access never triggers throwing owner initialization/extraction/construction (review focus 3).
- [x] Write failing diagnostic tests for open generic owners/alternates, private/inaccessible nested types, duplicate local/referenced owners, and deterministic ordering. Recognize the marker for key-only owners without requiring self extraction.
- [x] Run `dotnet test --project test/Raun.Generator.Test/Raun.Generator.Test.csproj -c Release -- --filter-class '*ResourceAlternateTests'`; confirm intended failures.
- [x] Implement discovery and strongly typed manifest emission. Retain exact interface dispatch; avoid reflection into private owner methods. Emit no mapping files or added definition initializer when no mappings exist, preserving legacy snapshots.
- [x] Integrate complete defaults into scenario definition emission independently of generated Main; validate imported owner/source metadata at compile time as well as runtime batch validation. Keep scenario-library discovery unchanged.
- [x] Run generator tests; inspect any received/verified snapshot diffs before accepting them. Require zero-warning build and no weakened graph assertions.
- [x] Commit `feat(generator): discover resource alternate declarations` with library/import scoping rationale.

### Task 4: Claims, formatting, lineage, and reports share canonical identity

**Files:** Modify `src/Raun/Resources/ResourceContext.cs`, `ScenarioContext.cs`, `Scheduling/ScenarioScheduler.cs`, `Running/RunLoop.cs`, `Reporting/Messages/StepMessageBuilder.cs`, `Reporting/Html/HtmlReportModelBuilder.cs`; test `test/Raun.Test/Resources/ResourceRepresentationIntegrationTests.cs`, `Reporting/HtmlReportModelBuilderTests.cs`, and `test/Raun.Generator.Test/ResourceAlternateTests.cs`, `ResourceKeyParameterTests.cs`.

**Interfaces:** Consume ApplyMappings, Normalize, source-aware ResolveLabel, and definition ResourceMappings from prior tasks. Existing public lifecycle/lineage APIs and emitted raw argument types remain unchanged.

- [x] Write failing end-to-end tests: Patient, row, response, Load<PatientRow>(key), supplied identity, and [ResourceKey<Patient>] string titles/claims share one identity; incompatible ledger claims conflict in both recording orders; lineage endpoints and report rollups use that same opaque ID.
- [x] Write failing tests for setup/title default availability and preflight-skipped formatting; external supplied DI resolvers preserve explicit same-group selectors and labels.
- [x] Write failing tests for supplied identity/title normalization (review focus 4), declared base extraction plus derived snapshot (review focus 5), optional/null values, same-step strongest-verb/latest-non-null aggregation, and chronological title/log snapshots across different representations.
- [x] Run the focused runtime/generator tests; verify intended failures.
- [x] Apply definition defaults before scenario formatting/Setup in both execution and skip paths. Normalize explicit claims, key-only Load, lineage identities, and symbolic title references at runtime boundaries; carry declared source Type to label resolution. Preserve claim-before-snapshot ordering.
- [x] Replace report Type-dependent identity metadata with Kind-aware metadata: typed output unchanged, named qualified metadata explicitly distinct. Keep dictionary identity keys and original serialized payloads.
- [x] Verify existing generated ResourceIdentity literals pass through these boundaries. Change ResourceKeyBinding emission only if a concrete execution test proves a remaining bypass; do not create generator-side canonicalization or duplicate argument evaluation.
- [x] Run focused checks and report JavaScript tests; require existing cancellation/cleanup/disposal and snapshot assertions unchanged.
- [x] Commit `feat(resources): unify alternate claims and report references` with evidence-preservation rationale.

### Task 5: Documentation, full verification, and final review

**Files:** Modify `README.md`, approved spec status, this plan's checkboxes/status; update canonical sample resource owner/steps only where needed to demonstrate an actual alternate payload.

- [x] Document interface-first owned domain and identity-only owner examples, optional labels, untouched external DTOs, and referenced mapping-only libraries. Show string ResourceKey<Owner> references resolving to the same identity.
- [x] Document Identify<Owner>().With<T>(selector) and Identify("kind") escape hatches, exact custom-Main/DI and Aspire Services placement, precedence/freeze constraints, named nullable Type compatibility, and required consumer rebuild. Add no user attributes or new startup convention.
- [x] Run `dotnet restore Raun.slnx --locked-mode`; require success without unplanned dependency graph changes.
- [x] Run `dotnet build Raun.slnx -c Release --no-restore`; require zero warnings/errors.
- [x] Run `dotnet test Raun.slnx -c Release --no-build`; require no failures and inspect sample scenario results.
- [x] Run `node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs`; require all report checks passing.
- [x] Pack Raun, Raun.Mtp, Raun.Coverage, and Raun.Aspire in Release with --no-build into a task artifact directory; require all four packages produced. Do not publish.
- [x] Obtain one final independent staff review of the complete feature diff against the spec. Ask specifically about manifest scoping/import ambiguity, freeze/default concurrency, typed-group merge order, evidence provenance, and backward generated-definition compatibility. Fix substantive findings and repeat only affected checks.
- [x] Commit docs/final corrections with why bodies, run `git -c core.whitespace=cr-at-eol diff --check`, then non-force push `HEAD:main`. Record verification and review outcome; no release/tag.

## Self-review

Checked against every spec section: contracts/kinds (task 1), fluent/default identity authority (task 2), local/library discovery and diagnostics (task 3), all runtime boundaries and evidence (task 4), docs and shipping checks (task 5). Each review-focus condition has an owning behavioral test. Manifests are immutable declarations consumed by definitions, not global registrations; no stage depends on a custom Main. Task interfaces agree on ResourceMapping, ResourceMappings, ApplyMappings, Normalize, and source-aware ResolveLabel. Native execution and one final reviewer remain the agreed approach.

Execution note: tasks 3 and 4 were committed together because their generated-host verification exercises both discovery and runtime default application. Inherited class contracts retain their declaring owner; direct declarations determine mapping ownership. Existing snapshot and graph assertions were preserved.

Final verification: locked restore passed; Release solution build has zero warnings/errors; full solution has 1,464 passed, 5 skipped, 0 failed (1,469 total); report checks 73/73; all four packages produced locally. Both staff findings fixed; none deferred.
