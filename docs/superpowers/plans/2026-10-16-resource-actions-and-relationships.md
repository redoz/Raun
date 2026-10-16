# Resource Actions and Relationships Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support metadata-defined domain actions and property-defined relationships whose endpoint bindings can vary per step, including every mapped representation of a canonical resource.

**Architecture:** ResourceAttribute and ResourceAction expose one immutable relationship schema. Property markers describe scalar/array bindings and optional canonical kind expectations; one generator reader normalizes these and standalone relationships before validation, conflict analysis, and emission. Existing array syntax uses those same markers.

**Tech Stack:** C#/.NET 10, Roslyn netstandard2.0 generator/analyzer, xUnit v3, self-contained HTML/JavaScript and Playwright browser harness.

**Spec:** `docs/superpowers/specs/2026-10-16-resource-actions-and-relationships-design.md`

## Global Constraints

- Implement here with one final staff reviewer; preserve that already selected execution method.
- No release, tag, or package publication. Commit/push completed work only after verification.
- Read metadata without executing attribute constructors, getters, initializers, or GetRelationships overrides.
- ResourceActionKind is exactly Create, Load, Read, Edit, Delete; Reference/Consume are separate ResourceRelation values.
- Preserve inline References/Consumes source compatibility and complete return-action override behavior.
- Reference<Patient> accepts every representation mapped to the canonical Patient group, including ResourceKey<T> endpoints. Compare canonical kinds, not CLR assignability.
- Preserve input-before-call/return-after-call claims, captured argument evaluation, graph dependencies, labels, snapshots, teardown/cancellation ownership, report geometry, and all existing guard/merge assertions.
- Keep collectible metadata caches weak-keyed and generated type resolution scoped to the correct assembly load context.
- Tests first; zero-warning Release builds; new diagnostics go in AnalyzerReleases.Unshipped.md.
- Use ASD-STE100-style XML comments for the new public metadata contract.

## Review Focus

1. An inherited relationship property is hidden, overridden, or decorated with conflicting markers in a referenced/private/generic attribute: diagnose ambiguity and never bind a different property by spelling. Tasks 1/2.
2. Required property initializers or constructor assignments look populated, but usage supplies no named argument: diagnose omission without executing user code. Optional omitted arrays remain compatible. Tasks 1/2.
3. Reference<Patient> targets an EF row, DTO, domain object, or typed string key, including fluent mappings unknown at compilation: accept matching canonical groups and reject actual mismatches without copying key-wrapper data. Tasks 2/3.
4. Mixed inline/standalone relations, explicit return overrides, or two parameters aliasing one canonical identity: preserve precedence, reject self/conflicting edges, and avoid first-wins report behavior. Tasks 2/3.
5. Booking domain property names differ from step argument names (Slot binds slot2), with optional and named arguments: preserve actual bindings, single argument evaluation, access timing, lineage, and remembered labels. Tasks 2/3/4.

---

### Task 1: Explicit action metadata and common relationship schema

**Files:** Create `src/Raun/Resources/ResourceAttribute.cs`, `ResourceActionKind.cs`, `ResourceActionDefinitionAttribute.cs`, `ResourceRelation.cs`, `RelationshipAttribute.cs`, `RelationshipDefinition.cs`, `RelatesToAttribute.cs`. Modify `ResourceRoleAttributes.cs`, `ResourceAction.cs`, `ResourceActionMetadata.cs`. Test `test/Raun.Test/Resources/ResourceDefinitionTests.cs`, existing ResourceActionTests/ResourceRoleAttributeTests/SubjectAttributeTests.

**Interfaces produced:**
- `abstract ResourceAttribute : Attribute` with read-only `IReadOnlyList<RelationshipDefinition> Relationships`, returning the cached schema for GetType, not evaluating instance bindings.
- Sealed `ResourceActionDefinitionAttribute(ResourceActionKind kind)` on classes; exactly one definition across the entire ResourceAttribute hierarchy.
- `RelationshipAttribute(ResourceRelation relation)` on properties with `bool Required { get; set; } = true`; nonsealed only to support sealed `RelationshipAttribute<TResource>` inheriting it. Marker usage is non-repeatable. No resource-interface generic constraint: mapped third-party types must remain valid.
- Sealed immutable `RelationshipDefinition` with `string PropertyName`, `ResourceRelation Relation`, `Type? ResourceType`, `bool IsCollection`, `bool Required`; internal construction only.
- ResourceAction gets read-only `Relationships` and `GetRelationship(string propertyName)` for metadata inspection/generated code. Preserve Effect/Name/AttributeType/factories and existing built-in labels. Schema ordering is ordinal by property name; resolution uses names, never an unstable reflection index.
- Sealed repeatable `RelatesToAttribute(ResourceRelation relation, string target)` on Parameter|ReturnValue|Method only; no class-level hardcoded targets.
- All built-ins derive ResourceAttribute and use definition metadata. Annotate existing three producer classes' arrays with non-generic Relationship markers, Required=false; no string-name classification in the schema reader.

- [x] Write ResourceDefinitionTests covering all five action/access projections, direct custom and inherited definitions, default/custom labels, missing/repeated/conflicting definitions, invalid enums, unrelated owners, public scalar/array property schemas, typed kind Type identity, optional built-in arrays, and hidden/overridden/invalid-shaped marked properties. Assert Booked schema has Patient/Reference/typeof(Patient)/required and Slot/Consume/typeof(Slot)/required, independent of step names.
- [x] Retain constructor/static-initializer/getter throwing probes, private/generic/load-context factories, and actual collectible-context collection assertions. Add schema ResourceType from a collectible assembly and prove the cache still permits collection.
- [x] Run `dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release -- --filter-class '*ResourceDefinitionTests' --filter-class '*ResourceActionTests' --filter-class '*ResourceRoleAttributeTests' --filter-class '*SubjectAttributeTests'`; expect failures for absent metadata contracts.
- [x] Implement exact-type metadata recognition, metadata-only hierarchy/property inspection and immutable weak-key caching. Validate schema declarations before constructing ResourceAction. Keep public For<T> usable for action-only imperative Apply; a descriptor exposes schema, while actual property bindings belong to an attribute usage. Imperative Reference/Consume remain explicit operations.
- [x] Write clear XML comments defining each semantic choice, requiredness, canonical expectations, locations, access, source/target direction, usage binding, and constructor-free discovery. Document intentional unsealed framework extension points for CA1813.
- [x] Re-run focused tests and `dotnet build Raun.slnx -c Release --no-restore`; require passing tests and zero warnings/errors. Commit `feat(resources): define action and relationship metadata` with a why body.

### Task 2: One generator reader and complete compile-time validation

**Files:** Create `src/Raun.Generator/Lowering/ResourceDefinitionReader.cs`, `RelationshipReader.cs`. Modify `ResourceRoleMetadata.cs`, `AttributeReader.cs`, `Ir.cs`, `ScenarioParser.cs`, `ParallelAccess.cs`, `Analysis/ScenarioAnalyzer.cs`, `Diagnostics/Descriptors.cs`, `AnalyzerReleases.Unshipped.md`. Test `test/Raun.Generator.Test/ResourceRelationshipTests.cs`, CustomResourceActionTests, ResourceLineageTests, ResourceKeyParameterTests and ResourceAlternateTests.

**Interfaces:**
- ResourceDefinitionReader reads action kinds and property schema by exact metadata symbol through inheritance. Preserve value-equatable custom action metadata and assembly-qualified private/generic identities.
- Internal value records `RelationshipPropertyMetadata` describe PropertyName, semantic kind, expected resource identity string or null, shape and requiredness; `DeclaredRelationship` holds Source, Target, semantic kind, and optional declaring action/property metadata for runtime canonical checks. Source/Target are resolved method subject names or AttributeReader.ReturnSubject.
- `RelationshipReader.Read(IMethodSymbol)` returns the selected, normalized immutable relationship list and immutable diagnostics. It owns action/standalone selection, named property binding, normalization, requiredness and duplicate/conflict checks. Analyzer, parser, and ParallelAccess consume this result; no separate References/Consumes interpretation remains.
- Keep RAUN010 for invalid subject names and existing 027/028/029 meanings. Add RAUN030 invalid action definition, RAUN031 invalid relationship property schema, RAUN032 invalid relationship binding/configuration; register SupportedDiagnostics and unshipped release entries.

- [x] Write tests for Booked(Patient=nameof(patient), Slot=nameof(slot2)), required omissions despite nonempty initializers/constructor assignments, optional omission, null/blank scalar/array values, inherited schemas and malformed unused local definitions. Referenced invalid definitions report at the consumer call; local errors report once at their declarations/usages.
- [x] Assert direct custom actions have the same semantic claims as inherited ones; unrelated same-name attributes do nothing. Test private/generic referenced relationship properties and metadata-only discovery with throwing constructors/getters.
- [x] Assert legacy array syntax and marked custom scalar/array properties normalize to identical source/target/kind edges. Test standalone RelatesTo, mixed-form duplicate/conflicting edges, source self-reference, missing returns, Read/Delete source rejection, and unsupported endpoints. Preserve complete inline return-action override; separately test standalone return-set override and mixed selection.
- [x] Add representation cases for Patient domain object, mapped Row, mapped Response DTO, and [ResourceKey<Row>] string with typed Patient markers. Validate available identity support statically and defer canonical equivalence to runtime where fluent mappings can affect it. Do not introduce CLR-assignability rejection.
- [x] Pin ParallelAccess to the normalized target access and existing explicit action precedence: Reference/Consume targets share; Create/Edit/Delete exclude in both parallel group orders. Keep existing guard/merge and precise diagnostic counts.
- [x] Pin omitted optional relationship subjects at a call: if no captured argument would exist, emit RAUN032 instructing the caller to supply it rather than silently drop the declared edge. Unrelated optional arguments remain unchanged.
- [x] Run the focused generator classes before implementation and require intended failures. Implement metadata readers and normalized validation, routing all existing consumers through one relationship interpretation. Update existing test expectations only for intentional diagnostic/API changes; never remove guard/merge/identity assertions.
- [x] Run `dotnet test --project test/Raun.Generator.Test/Raun.Generator.Test.csproj -c Release`; inspect snapshot diffs rather than bulk accepting. Require incremental caching/emission discipline and zero-warning solution build. Commit `feat(generator): normalize resource relationships from property metadata`.

### Task 3: Emit and enforce canonical relationship bindings

**Files:** Modify `src/Raun.Generator/Lowering/ScenarioParser.cs`, `Emit/ScenarioEmitter.cs`, `src/Raun/Resources/ResourceContext.cs`; inspect `Resources/ResourceIdentityResolver.cs` and `Reporting/Html/HtmlReportModelBuilder.cs` for needed canonical/conflict boundaries only. Test `test/Raun.Test/Resources/ResourceRelationshipTests.cs`, generator ResourceRelationshipTests, existing argument/title/key/lineage tests, and label-memory integration tests.

**Interfaces:**
- Add generated-code `ValueTask Relate<TSource,TTarget>(ResourceRelation relation, TSource source, TTarget target, Type? expectedResourceType = null)` with both types notnull. Resolve subjects using their declared generic types; supplied ResourceIdentity remains key-only. Validate kind/canonical aliases/expected kind before recording that relationship. Delegate recording to existing Reference/Consume semantics, using the resolved source identity so lineage cannot change source representation by GetType.
- Expected kind compares normalized `new ResourceIdentity(expectedResourceType, targetIdentity.Key).Kind` with targetIdentity.Kind. Read the expected Type through `ResourceAction.FromGenerated(scope, actionTypeIdentity).GetRelationship(propertyName).ResourceType`, avoiding inaccessible direct C# type references.
- Existing imperative Reference/Consume signatures stay valid. Conflicting canonical relationship kinds/self-edges must fail deterministically; repeated same-kind imperative observations can remain idempotent.

- [x] Write runtime tests for each canonical representation and key-only endpoint accepted by typed Patient expectations. Include fluent mappings, same string keys on different groups rejected, same-group source/target aliases rejected, conflicting kinds rejected, and unchanged original labels/snapshots. No wrapper or borrowed payload in key-only effects.
- [x] Add generator execution tests proving scalar/array bindings use actual captured arguments once, named arguments bind slot2 correctly, parameter claims remain pre-call, return-dependent relations remain post-call, and failed invocations produce no return relationships. Test expected-type metadata for private/generic referenced resource owners without inaccessible emitted symbols.
- [x] Run focused runtime and generator relationship projects/classes and require intended failures. Emit every normalized relation through the canonical guard with its original source/target expressions. Preserve target-lineage-before-source-effect order and semantic LifecycleVerb projections.
- [x] Keep graph-model compatibility and resource identity correlation; diagnose declaration conflicts before report building and reject runtime canonical conflicts before accepting evidence, so report dedup cannot hide evidence. Across steps, preserve each distinct (source, target, kind) observation. Preserve same-kind imperative compatibility.
- [x] Run full runtime and generator projects sequentially, then zero-warning build. Retain existing ResourceLabelMemoryIntegrationTests, argument evaluation, graph, and process-timeout assertions. Commit `feat(resources): enforce canonical domain relationship bindings`.

### Task 4: Appointment-domain example, README, and actual report checks

**Files:** Create `samples/AppointmentTests/BookingAttributes.cs`; modify AppointmentDsl.cs, LifecycleDsl.cs, ResourceActionScenarios.cs only if needed, README.md, test/Browser/resource-matrix.cjs and generator relationship regression fixtures. Preserve PlatformSurfaceTests exact totals unless authored graph nodes actually change.

**Domain attributes:**
- Registered : CreatedAttribute for patient registration; Held : EditedAttribute for slot holds; Cancelled : DeletedAttribute for booking cancellation. Use DisplayName where an imperative verb is clearer in the report.
- Booked : ResourceAttribute with explicit Create definition and required [Relationship<Patient>(Reference)] string Patient / [Relationship<Slot>(Consume)] string Slot.
- Rescheduled : ResourceAttribute with explicit Create definition and required typed Consume Slot property. The old booking keeps its explicit Delete action; Consume still does not imply deletion.
- Use Booked on both CreateAppointment and CreateUrgentAppointment, Registered on existing patient-creation returns, Held on HoldSlot input/return, Rescheduled on its result, and Cancelled on CancelAppointment's input. Use slot2 as one booking method's parameter name without changing scenario semantics. Keep Approved/Audited and remembered-key example.

- [x] Add browser assertions on actual sample output: custom domain action names retain Create/Edit/Delete verbs, patient reference and slot consumption connect the right canonical identities, both booking variants work, and key-only audit has the remembered name without data. Preserve every existing matrix, mobile, keyboard, grouping, failure, geometry and snapshot assertion.
- [x] Run the new sample/generator assertions before product sample migration and observe the missing domain metadata/bindings failures. Implement the declarations and usages, retaining the scenarios and authored operation bodies.
- [x] Document generic property metadata, role-to-parameter binding via nameof, canonical alternative types, Required defaults, legacy arrays, standalone relationships, return precedence, and no execution of attribute code. Use the real booking example rather than another disconnected toy. Review XML comments against the approved STE-style requirement.
- [x] Run `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -c Release -- --report-html`, focused HTML model tests, `node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs`, and `node test/Browser/resource-matrix.cjs samples/AppointmentTests/bin/Release/net10.0/TestResults/raun-report.html artifacts/resource-relationships/browser`; require pass, inspect screenshot. If node count changes, retain exact totals and six-node filtered selection checks.
- [x] Commit `feat(samples): demonstrate appointment domain resource attributes` with a why body.

### Task 5: Full verification and one final staff review

**Files:** Completion status/evidence in this plan and the spec; temporary logs in this plan's ignored workspace only.

- [x] Run locked restore and inspect lock-file changes (none expected), then `dotnet build Raun.slnx -c Release --no-restore` with zero warnings/errors. Run builds separately from timeout-host tests.
- [x] Run `dotnet test Raun.slnx -c Release --no-build`; require zero failures and inspect expected sample branch skips. Do not overlap full suites.
- [x] Run final report JS and real sample browser checks; inspect updated domain-action report. Pack Raun, Raun.Mtp, Raun.Coverage and Raun.Aspire locally with --no-build/--no-restore; require four packages. Publish nothing.
- [x] Dispatch the one authorized final fresh staff reviewer over the whole change, spec, plan Review Focus and execution rulings. Inspect metadata identity, property/schema parity, binding precedence, requiredness, canonical alternate/fluent mappings, argument timing, preserved snapshots/labels, and actual booking examples. Fix substantive findings with failing regressions then green checks.
- [x] Record verified counts, review outcome and any rulings; complete checklist, run `git -c core.whitespace=cr-at-eol diff --check`, commit with why bodies and non-force push. Import refs into the colocated JJ repository without editing user files. Keep the managed worktree and report previews. No release.

## Self-review

Each requirement has one owner: metadata schema and XML docs (1), generic source decoding/validation and static access (2), emitted canonical runtime checks and preserved execution semantics (3), real booking-domain usage and docs/browser evidence (4), full gates/final review/push (5). Property roles never prescribe step names. Typed expectations are canonical resource kinds, including mapped external objects and key-only values. Runtime schema describes declarations; source-bound relationships remain separately normalized. Existing arrays use the same marker and validation, and imperative action-only Apply does not invent bindings. No new reference-wrapper type or executable user discovery hook is needed.


## Completion evidence — 2026-10-16

Implemented and verified. Release build: zero warnings and zero errors. Full solution:
1,559 tests total, 1,554 passed, five expected branch skips, zero failures. The full generator
suite passes all 632 tests; no verified snapshots changed. The appointment sample passes
95 tests with five expected skips. Report JavaScript passes all 72 tests, and the real-report
browser checks pass, including domain verbs, typed relationships, remembered labels, matrix
geometry, and relationship history across steps. Four current packages were packed locally;
none were published. Locked restore changed no lock files.

The one final staff review (Astra, High) found three issues: a field could hide an inherited
relationship property, referenced private metadata differed between Roslyn and reflection,
and report deduplication discarded a later relationship kind. Each issue was reproduced by
failing tests and fixed. Focused follow-up: 32 runtime/model and 21 generator cases passed,
followed by the full solution run above. No declined-to-judge behaviors or deferred minors.

Execution rulings:
- Equivalent Python/PowerShell bookkeeping was used on Windows; it is reconstructible from Git.
- Tasks 2 and 3 landed together because normalized declarations and guarded emission form one
  semantic transition; RED/GREEN evidence was retained. This increases the review range only.
- Canonical kind conflicts are rejected within one step. Different steps can observe different
  relationship kinds for the same endpoints; both remain report history. A later policy change
  must revisit this explicitly rather than discard evidence.

Implementation commits: 7d4c9b4 (contracts), 8b4b52b (generator/runtime integration),
094897e (booking sample and README), and 63068e7 (final review fixes), followed by this completion record.
The original user checkout is preserved; the managed worktree and report previews are retained.
